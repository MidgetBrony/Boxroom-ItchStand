using BR_MediaAPI;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using SteamShelf.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;

namespace Boxroom_ItchStand;

public sealed class ItchGameItem : MediaItemBase
{
    public ItchGameItem(ItchCatalogEntry entry, byte[] cover)
        : base((eMediaType)Core.MediaTypeId, entry.Id)
    {
        Title = entry.Title;
        Url = entry.Url;
        Creator = entry.Creator;
        Description = entry.Description;
        Platforms = entry.Platforms;
        CoverArtBytes = cover;
    }

    public string Title { get; }
    public string Url { get; }
    public string Creator { get; }
    public string Description { get; }
    public string Platforms { get; }
    public override string DisplayName => Title;
}

public sealed class ItchCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Platforms { get; set; } = string.Empty;
}

internal sealed class ItchCatalogCache
{
    public int Version { get; set; } = 1;
    public string CatalogDate { get; set; } = string.Empty;
    public int Reroll { get; set; }
    public List<ItchCatalogEntry> Entries { get; set; } = new();
}

public sealed class ItchLibrary : IMediaLibrary
{
    public static ItchLibrary Instance { get; } = new();

    private const string FeedUrl = "https://itch.io/games/price-free.xml";
    private const int SelectionSize = 18;
    private static readonly HttpClient Client = CreateClient();
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly object stateLock = new();
    private Dictionary<string, ItchGameItem> items = new(StringComparer.OrdinalIgnoreCase);
    private ItchCatalogCache cache = new();

    public eMediaType HandledType => (eMediaType)Core.MediaTypeId;
    public bool IsReady { get; private set; }
    public int Revision { get; private set; }
    public bool IsRefreshing { get; private set; }
    public event Action<IMediaItem> OnItemReady;
    public event Action<IReadOnlyList<IMediaItem>> OnLibraryReady;

    private static string Root => Path.Combine(MelonEnvironment.UserDataDirectory, "Boxroom-ItchStand");
    private static string CachePath => Path.Combine(Root, "catalog.json");
    private static string CoverRoot => Path.Combine(Root, "covers");

    public IReadOnlyList<IMediaItem> GetKnownItems()
    {
        lock (stateLock) return items.Values.Cast<IMediaItem>().ToList();
    }

    public IMediaItem GetItemSync(MediaRef mediaRef)
    {
        if (mediaRef.Type != HandledType) return null;
        lock (stateLock) return items.TryGetValue(mediaRef.Id, out ItchGameItem item) ? item : null;
    }

    public Task<IMediaItem> GetItemAsync(MediaRef mediaRef) => Task.FromResult(GetItemSync(mediaRef));

    public void LoadCache()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CoverRoot);
        try
        {
            if (File.Exists(CachePath))
                cache = JsonConvert.DeserializeObject<ItchCatalogCache>(File.ReadAllText(CachePath)) ?? new ItchCatalogCache();
        }
        catch (Exception exception)
        {
            Core.Log?.Warning($"Could not read the itch catalogue cache: {exception.Message}");
            cache = new ItchCatalogCache();
        }

        if (cache.Entries.Count > 0)
            BuildSelectionFromDisk();
    }

    public async Task EnsureDailySelectionAsync()
    {
        await refreshLock.WaitAsync();
        try
        {
            IsRefreshing = true;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            bool needsFeed = cache.Entries.Count == 0 || !string.Equals(cache.CatalogDate, today, StringComparison.Ordinal);
            if (needsFeed)
            {
                try
                {
                    List<ItchCatalogEntry> fresh = await DownloadFeedAsync();
                    if (fresh.Count > 0)
                    {
                        cache.Entries = fresh;
                        cache.CatalogDate = today;
                        cache.Reroll = 0;
                        SaveCache();
                    }
                }
                catch (Exception exception)
                {
                    Core.Log?.Warning($"itch.io refresh failed; using cached games: {exception.Message}");
                }
            }
            await BuildSelectionAsync();
        }
        finally
        {
            IsRefreshing = false;
            refreshLock.Release();
        }
    }

    public async Task RerollAsync()
    {
        await refreshLock.WaitAsync();
        try
        {
            IsRefreshing = true;
            cache.Reroll++;
            SaveCache();
            await BuildSelectionAsync();
        }
        finally
        {
            IsRefreshing = false;
            refreshLock.Release();
        }
    }

    private void BuildSelectionFromDisk()
    {
        List<ItchCatalogEntry> selected = SelectEntries();
        var ready = new Dictionary<string, ItchGameItem>(StringComparer.OrdinalIgnoreCase);
        foreach (ItchCatalogEntry entry in selected)
        {
            byte[] cover = TryReadCover(entry.ImageUrl);
            ready[entry.Id] = new ItchGameItem(entry, cover);
        }
        Publish(ready);
    }

    private async Task BuildSelectionAsync()
    {
        List<ItchCatalogEntry> selected = SelectEntries();
        var ready = new Dictionary<string, ItchGameItem>(StringComparer.OrdinalIgnoreCase);
        foreach (ItchCatalogEntry entry in selected)
        {
            byte[] cover = TryReadCover(entry.ImageUrl);
            if ((cover == null || cover.Length == 0) && !string.IsNullOrWhiteSpace(entry.ImageUrl))
            {
                try
                {
                    cover = await Client.GetByteArrayAsync(entry.ImageUrl);
                    if (cover.Length > 0) File.WriteAllBytes(CoverPath(entry.ImageUrl), cover);
                }
                catch (Exception exception)
                {
                    Core.Log?.Warning($"Could not download cover for '{entry.Title}': {exception.Message}");
                }
            }
            ready[entry.Id] = new ItchGameItem(entry, cover);
        }
        Publish(ready);
    }

    private void Publish(Dictionary<string, ItchGameItem> ready)
    {
        lock (stateLock)
        {
            foreach (ItchGameItem old in items.Values)
            {
                old.IsSpawned = false;
                old.IsInHand = false;
            }
            items = ready;
            IsReady = true;
            Revision++;
        }

        foreach (ItchGameItem item in ready.Values) OnItemReady?.Invoke(item);
        OnLibraryReady?.Invoke(ready.Values.Cast<IMediaItem>().ToList());
        Core.Log?.Msg($"Itch Stand selection ready: {ready.Count} free games (reroll {cache.Reroll}).");
    }

    private List<ItchCatalogEntry> SelectEntries()
    {
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        List<ItchCatalogEntry> pool = cache.Entries
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Url))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var random = new System.Random(StableSeed($"{day}:{cache.Reroll}"));
        for (int index = pool.Count - 1; index > 0; index--)
        {
            int swap = random.Next(index + 1);
            (pool[index], pool[swap]) = (pool[swap], pool[index]);
        }
        return pool.Take(SelectionSize).ToList();
    }

    private static async Task<List<ItchCatalogEntry>> DownloadFeedAsync()
    {
        string xml = await Client.GetStringAsync(FeedUrl);
        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        return document.Descendants("item").Select(ParseEntry)
            .Where(entry => entry != null && !entry.ImageUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static ItchCatalogEntry ParseEntry(XElement element)
    {
        string url = Value(element, "link");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return null;
        string description = Value(element, "description");
        description = WebUtility.HtmlDecode(Tags.Replace(description ?? string.Empty, " "));
        description = Regex.Replace(description, "\\s+", " ").Trim();
        string platforms = string.Join(", ", element.Element("platforms")?.Elements()
            .Where(value => string.Equals(value.Value, "yes", StringComparison.OrdinalIgnoreCase))
            .Select(value => PlatformName(value.Name.LocalName)) ?? Array.Empty<string>());

        return new ItchCatalogEntry
        {
            Id = url.TrimEnd('/'),
            Title = WebUtility.HtmlDecode(Value(element, "plainTitle") ?? Value(element, "title") ?? uri.Segments.Last().Trim('/')),
            Url = url,
            ImageUrl = Value(element, "imageurl") ?? string.Empty,
            Creator = uri.Host.Split('.')[0],
            Description = description,
            Platforms = platforms
        };
    }

    private static string Value(XElement element, string name) => element.Element(name)?.Value?.Trim();

    private static string PlatformName(string value) => value.ToLowerInvariant() switch
    {
        "windows" => "Windows",
        "linux" => "Linux",
        "osx" => "macOS",
        "html" => "Browser",
        "android" => "Android",
        _ => value
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Boxroom-ItchStand/0.1.1 (+https://itch.io)");
        return client;
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char character in value) hash = (hash ^ character) * 16777619;
            return (int)(hash & 0x7fffffff);
        }
    }

    private static string CoverPath(string url)
    {
        using SHA256 sha = SHA256.Create();
        string name = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(url)).Select(value => value.ToString("x2")));
        return Path.Combine(CoverRoot, name + ".img");
    }

    private static byte[] TryReadCover(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            string path = CoverPath(url);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(CachePath, JsonConvert.SerializeObject(cache, Formatting.Indented));
        }
        catch (Exception exception)
        {
            Core.Log?.Warning($"Could not save the itch catalogue cache: {exception.Message}");
        }
    }
}

