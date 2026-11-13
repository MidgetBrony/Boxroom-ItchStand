using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using SteamShelf;
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
using UnityEngine;

namespace Boxroom_ItchStand;

public sealed class ItchCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Platforms { get; set; } = string.Empty;
    public List<string> ScreenshotUrls { get; set; } = new();
    public bool PageMetadataFetched { get; set; }
    public bool PageDescriptionFetched { get; set; }
}

internal sealed class ItchCatalogCache
{
    public int Version { get; set; } = 6;
    public string CatalogDate { get; set; } = string.Empty;
    public string FilterSignature { get; set; } = string.Empty;
    public int Reroll { get; set; }
    public List<ItchCatalogEntry> Entries { get; set; } = new();

    // One stable BOXROOM Custom Game App ID per position on the stand. Rerolls
    // update these slots instead of permanently adding another eighteen games.
    public List<int> NativeAppIds { get; set; } = new();
    public int RefreshAppId { get; set; }
}

// Selects itch.io entries and synchronises them into BOXROOM's own
// SteamLibrarySystem Custom Game registry. This is deliberately not a separate
// media library and does not introduce another eMediaType.
public sealed class ItchLibrary
{
    public static ItchLibrary Instance { get; } = new();

    private const string LaunchMarker = "boxroom-itch-url=";
    private const string RefreshMarker = "boxroom-itch-refresh";
    private const int SelectionSize = 29;
    private const int ScreenshotLimit = 3;
    private static readonly HttpClient Client = CreateClient();
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ScreenshotAnchors = new(
        "<a\\b[^>]*\\bdata-image_lightbox\\s*=\\s*([\"'])true\\1[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HrefAttribute = new(
        "\\bhref\\s*=\\s*([\"'])(?<url>.*?)\\1",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex DescriptionBlocks = new(
        "<(?:p|li)\\b[^>]*>(?<text>.*?)</(?:p|li)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex BreakTags = new(
        "<br\\s*/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CatalogueCells = new(
        "<div\\s+class=([\"'])game_cell\\b(?<cell>.*?)(?=<div\\s+class=([\"'])game_cell\\b|<div\\s+class=([\"'])pager|</section>|\\z)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ThumbAnchor = new(
        "<a\\b(?=[^>]*\\bclass=([\"'])[^\"']*\\bthumb_link\\b[^\"']*\\1)[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly SemaphoreSlim nativeSyncLock = new(1, 1);
    private readonly object stateLock = new();
    private readonly object cacheSaveLock = new();
    private readonly Dictionary<int, string> urlsByAppId = new();
    private List<ItchCatalogEntry> selectedEntries = new();
    private List<SteamGameData> games = new();
    private SteamGameData refreshGame;
    private ItchCatalogCache cache = new();
    private bool nativeLibraryReady;
    private bool shutdown;
    private int selectionGeneration;

    public bool IsReady { get; private set; }
    public int Revision { get; private set; }
    public bool IsRefreshing { get; private set; }
    public event Action<IReadOnlyList<SteamGameData>> OnLibraryReady;

    private static string Root => Path.Combine(MelonEnvironment.UserDataDirectory, "Boxroom-ItchStand");
    private static string CachePath => Path.Combine(Root, "catalog.json");
    private static string CoverRoot => Path.Combine(Root, "covers");

    public void LoadCache()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CoverRoot);
        try
        {
            if (File.Exists(CachePath))
                cache = JsonConvert.DeserializeObject<ItchCatalogCache>(File.ReadAllText(CachePath))
                    ?? new ItchCatalogCache();
        }
        catch (Exception exception)
        {
            Core.Log?.Warning($"Could not read the itch catalogue cache: {exception.Message}");
            cache = new ItchCatalogCache();
        }

        cache.NativeAppIds ??= new List<int>();
        cache.Entries ??= new List<ItchCatalogEntry>();
        foreach (ItchCatalogEntry entry in cache.Entries.Where(entry => entry != null))
            entry.ScreenshotUrls ??= new List<string>();
        if (!SteamLibrarySystem.IsCustomAppId(cache.RefreshAppId) &&
            cache.NativeAppIds.Count > SelectionSize)
        {
            int last = cache.NativeAppIds.Count - 1;
            cache.RefreshAppId = cache.NativeAppIds[last];
            cache.NativeAppIds.RemoveAt(last);
            cache.Version = 6;
            SaveCache();
        }
        selectedEntries = SelectEntries();
    }

    public void OnNativeGameLibraryReady()
    {
        if (shutdown) return;
        nativeLibraryReady = true;
        _ = SynchronizeNativeCustomGamesAsync();
    }

    public void Shutdown()
    {
        shutdown = true;
        Interlocked.Increment(ref selectionGeneration);
        nativeLibraryReady = false;
        Client.CancelPendingRequests();
        lock (stateLock)
        {
            games.Clear();
            refreshGame = null;
            urlsByAppId.Clear();
            IsReady = false;
        }
        OnLibraryReady = null;
    }

    public IReadOnlyList<SteamGameData> GetKnownGames()
    {
        lock (stateLock) return games.ToList();
    }

    public SteamGameData GetRefreshGame()
    {
        lock (stateLock) return refreshGame;
    }

    public bool IsRefreshGame(SteamGameData game)
    {
        if (game == null) return false;
        if (game.AppId == cache.RefreshAppId && SteamLibrarySystem.IsCustomAppId(game.AppId))
            return true;
        return string.Equals(game.LaunchArguments, RefreshMarker, StringComparison.Ordinal);
    }

    public bool IsManagedGame(SteamGameData game) => game != null && IsManagedAppId(game.AppId);

    public bool IsManagedAppId(int appId)
    {
        lock (stateLock)
        {
            if (urlsByAppId.ContainsKey(appId)) return true;
        }
        return (cache.NativeAppIds.Contains(appId) || cache.RefreshAppId == appId) &&
            SteamLibrarySystem.IsCustomAppId(appId);
    }

    public bool TryGetUrl(SteamGameData game, out string url)
    {
        url = null;
        if (game == null) return false;
        lock (stateLock)
        {
            if (urlsByAppId.TryGetValue(game.AppId, out url) &&
                Uri.TryCreate(url, UriKind.Absolute, out _))
                return true;
        }

        string arguments = game.LaunchArguments;
        if (!string.IsNullOrWhiteSpace(arguments) &&
            arguments.StartsWith(LaunchMarker, StringComparison.Ordinal))
        {
            url = arguments.Substring(LaunchMarker.Length);
            return Uri.TryCreate(url, UriKind.Absolute, out _);
        }
        url = null;
        return false;
    }

    public async Task EnsureDailySelectionAsync()
    {
        if (shutdown) return;
        await refreshLock.WaitAsync();
        try
        {
            if (shutdown) return;
            IsRefreshing = true;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            bool needsCatalogue = cache.Entries.Count < SelectionSize ||
                !string.Equals(cache.CatalogDate, today, StringComparison.Ordinal) ||
                !string.Equals(cache.FilterSignature, ItchStandSettings.Signature, StringComparison.Ordinal);
            if (needsCatalogue)
            {
                try
                {
                    await ReplaceCatalogueAsync(today);
                }
                catch (Exception exception)
                {
                    Core.Log?.Warning($"itch.io catalogue refresh failed; using cached games: {exception.Message}");
                }
            }

            selectedEntries = SelectEntries();
            await PublishSelectionFastAsync();
        }
        finally
        {
            IsRefreshing = false;
            refreshLock.Release();
        }
    }

    public async Task RerollAsync()
    {
        if (shutdown) return;
        await refreshLock.WaitAsync();
        try
        {
            if (shutdown) return;
            IsRefreshing = true;
            if (!string.Equals(cache.FilterSignature, ItchStandSettings.Signature, StringComparison.Ordinal))
                await ReplaceCatalogueAsync(DateTime.Now.ToString("yyyy-MM-dd"));
            else
                cache.Reroll++;
            selectedEntries = SelectEntries();
            SaveCache();
            await PublishSelectionFastAsync();
        }
        finally
        {
            IsRefreshing = false;
            refreshLock.Release();
        }
    }

    public async Task<int> ApplySettingsAsync()
    {
        if (shutdown) return 0;
        await refreshLock.WaitAsync();
        try
        {
            if (shutdown) return 0;
            IsRefreshing = true;
            await ReplaceCatalogueAsync(DateTime.Now.ToString("yyyy-MM-dd"));
            selectedEntries = SelectEntries();
            await PublishSelectionFastAsync();
            return cache.Entries.Count;
        }
        finally
        {
            IsRefreshing = false;
            refreshLock.Release();
        }
    }

    private async Task PublishSelectionFastAsync()
    {
        int generation = Interlocked.Increment(ref selectionGeneration);
        List<ItchCatalogEntry> selection = selectedEntries.Take(SelectionSize).ToList();
        await DownloadMissingCoversAsync(selection);
        if (shutdown || generation != selectionGeneration) return;

        await SynchronizeNativeCustomGamesAsync(generation);
        if (shutdown || generation != selectionGeneration) return;

        Core.Log?.Msg("Itch Stand fast refresh complete; richer descriptions and screenshots are loading in the background.");
        _ = EnrichSelectionInBackgroundAsync(selection, generation);
    }

    private async Task EnrichSelectionInBackgroundAsync(
        IReadOnlyList<ItchCatalogEntry> selection,
        int generation)
    {
        try
        {
            await DownloadMissingDetailsAsync(selection);
            if (shutdown || generation != selectionGeneration) return;
            await SynchronizeNativeCustomGamesAsync(generation);
            if (!shutdown && generation == selectionGeneration)
                Core.Log?.Msg("Itch Stand background artwork enrichment complete.");
        }
        catch (Exception exception)
        {
            Core.Log?.Warning($"Itch Stand background enrichment stopped: {exception.Message}");
        }
    }

    private async Task ReplaceCatalogueAsync(string catalogDate)
    {
        List<ItchCatalogEntry> fresh = await DownloadCatalogueAsync();
        if (fresh.Count < SelectionSize)
            throw new InvalidOperationException(
                $"Only {fresh.Count} matching itch.io games were found. Broaden the filters or scan more pages.");
        cache.Entries = fresh;
        cache.CatalogDate = catalogDate;
        cache.FilterSignature = ItchStandSettings.Signature;
        cache.Reroll = 0;
        cache.Version = 6;
        SaveCache();
        Core.Log?.Msg(
            $"Itch Stand catalogue contains {fresh.Count} games from " +
            $"{ItchStandSettings.PageCount} page(s) per filter ({cache.FilterSignature}).");
    }

    private async Task SynchronizeNativeCustomGamesAsync(int expectedGeneration = -1)
    {
        if (shutdown || !nativeLibraryReady || selectedEntries.Count == 0) return;
        await nativeSyncLock.WaitAsync();
        try
        {
            if (shutdown || !nativeLibraryReady ||
                (expectedGeneration >= 0 && expectedGeneration != selectionGeneration))
                return;
            List<ItchCatalogEntry> selection = selectedEntries.Take(SelectionSize).ToList();
            await Awaitable.MainThreadAsync();

            var ready = new List<SteamGameData>(selection.Count);
            var readyUrls = new Dictionary<int, string>();
            int screenshotCount = 0;
            for (int slot = 0; slot < selection.Count; slot++)
            {
                ItchCatalogEntry entry = selection[slot];
                SteamLibrarySystem.CustomGameFormData form = BuildCustomGameForm(entry);
                SteamGameData game = null;

                if (slot < cache.NativeAppIds.Count &&
                    SteamLibrarySystem.IsCustomAppId(cache.NativeAppIds[slot]))
                {
                    int appId = cache.NativeAppIds[slot];
                    game = SteamLibrarySystem.GetGameSync(appId);
                    if (game == null) game = await SteamLibrarySystem.GetGameAsync(appId);
                    if (game != null) game = await SteamLibrarySystem.UpdateCustomGame(appId, form);
                }

                if (game == null)
                {
                    game = await SteamLibrarySystem.RegisterCustomGame(form);
                    if (game == null) continue;
                    if (slot < cache.NativeAppIds.Count)
                        cache.NativeAppIds[slot] = game.AppId;
                    else
                        cache.NativeAppIds.Add(game.AppId);
                }

                IReadOnlyList<byte[]> screenshots = ReadScreenshots(entry.ScreenshotUrls);
                screenshotCount += screenshots.Count;
                ApplyNativeArtwork(game, TryReadImage(entry.ImageUrl), screenshots);
                ready.Add(game);
                readyUrls[game.AppId] = entry.Url;
            }

            SteamGameData nativeRefreshGame = await SynchronizeRefreshGameAsync();

            cache.Version = 6;
            SaveCache();
            lock (stateLock)
            {
                games = ready;
                refreshGame = nativeRefreshGame;
                urlsByAppId.Clear();
                foreach ((int appId, string url) in readyUrls)
                    urlsByAppId[appId] = url;
                IsReady = ready.Count > 0;
                Revision++;
            }

            OnLibraryReady?.Invoke(ready);
            Core.Log?.Msg(
                $"Itch Stand synchronized {ready.Count} games with {screenshotCount} screenshots " +
                $"plus the native refresh case (reroll {cache.Reroll}).");
        }
        catch (Exception exception)
        {
            Core.Log?.Error($"Could not synchronize itch.io Custom Games: {exception}");
        }
        finally
        {
            nativeSyncLock.Release();
        }
    }

    private async Task<SteamGameData> SynchronizeRefreshGameAsync()
    {
        SteamLibrarySystem.CustomGameFormData form = new()
        {
            Name = "Refresh itch.io Games",
            Description = "Select a new set of free itch.io games for The Itch Stand.",
            Developer = "The Itch Stand",
            Publishers = new[] { "itch.io" },
            Genres = new[] { "Utility", "itch.io" },
            ReleaseDate = string.Empty,
            LaunchExePath = string.Empty,
            LaunchArguments = RefreshMarker
        };

        SteamGameData game = null;
        if (SteamLibrarySystem.IsCustomAppId(cache.RefreshAppId))
        {
            game = SteamLibrarySystem.GetGameSync(cache.RefreshAppId);
            if (game == null) game = await SteamLibrarySystem.GetGameAsync(cache.RefreshAppId);
            if (game != null)
                game = await SteamLibrarySystem.UpdateCustomGame(cache.RefreshAppId, form);
        }

        if (game == null)
        {
            game = await SteamLibrarySystem.RegisterCustomGame(form);
            if (game != null) cache.RefreshAppId = game.AppId;
        }

        if (game != null)
            ApplyNativeArtwork(game, ItchStandFactory.GetLogoBytes(), Array.Empty<byte[]>());
        return game;
    }

    private static SteamLibrarySystem.CustomGameFormData BuildCustomGameForm(ItchCatalogEntry entry)
    {
        string title = string.IsNullOrWhiteSpace(entry.Title) ? "itch.io Game" : entry.Title.Trim();
        string description = string.IsNullOrWhiteSpace(entry.Description)
            ? $"Discover {title} on itch.io."
            : entry.Description.Trim();
        string creator = string.IsNullOrWhiteSpace(entry.Creator)
            ? "itch.io creator"
            : entry.Creator.Trim();

        return new SteamLibrarySystem.CustomGameFormData
        {
            Name = title,
            Description = description,
            Developer = creator,
            Publishers = new[] { creator },
            Genres = new[] { entry.Genre, "Indie", "itch.io" }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ReleaseDate = string.Empty,
            LaunchExePath = string.Empty,
            LaunchArguments = LaunchMarker + entry.Url
        };
    }

    private static void ApplyNativeArtwork(
        SteamGameData game,
        byte[] cover,
        IReadOnlyList<byte[]> screenshots)
    {
        if (game == null) return;
        if (cover != null && cover.Length > 0)
        {
            SetNative(game, nameof(SteamGameData.BoxArtBytes), cover.ToArray());
            SetNative(game, nameof(SteamGameData.DiskArtBytes), cover.ToArray());
            SetNative(game, nameof(SteamGameData.BoxArtLoaded), true);
            SetNative(game, nameof(SteamGameData.DiskArtLoaded), true);
            SteamLibrarySystem.ApplyUserBoxArt(game);
            SteamLibrarySystem.ApplyUserDiskArt(game);
        }

        // Slot App IDs are reused on every reroll, so evict the previous game's
        // decoded images before assigning the new game's screenshots.
        SteamTextureCache.EvictScreenshots(game.AppId);
        SetNative(
            game,
            nameof(SteamGameData.ScreenshotBytes),
            screenshots?.Where(bytes => bytes?.Length > 0).Select(bytes => bytes.ToArray()).ToList()
                ?? new List<byte[]>());
        SetNative(game, nameof(SteamGameData.ScreenshotsLoaded), true);
    }

    private static void SetNative(SteamGameData game, string property, object value) =>
        AccessTools.Property(typeof(SteamGameData), property)?.SetValue(game, value);

    private List<ItchCatalogEntry> SelectEntries()
    {
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        List<ItchCatalogEntry> pool = cache.Entries
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Id) &&
                !string.IsNullOrWhiteSpace(entry.Url))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var random = new System.Random(
            StableSeed($"{day}:{cache.Reroll}:{ItchStandSettings.Signature}"));
        for (int index = pool.Count - 1; index > 0; index--)
        {
            int swap = random.Next(index + 1);
            (pool[index], pool[swap]) = (pool[swap], pool[index]);
        }
        return pool.Take(SelectionSize).ToList();
    }

    private async Task DownloadMissingCoversAsync(IEnumerable<ItchCatalogEntry> entries)
    {
        List<ItchCatalogEntry> selected = entries.Where(entry => entry != null).ToList();
        for (int offset = 0; offset < selected.Count; offset += 10)
        {
            await Task.WhenAll(selected.Skip(offset).Take(10).Select(DownloadCoverAsync));
            if (shutdown) return;
        }
    }

    private static async Task DownloadCoverAsync(ItchCatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ImageUrl) && !(TryReadImage(entry.ImageUrl)?.Length > 0))
            await DownloadImageAsync(entry, entry.ImageUrl, "cover");
    }

    private async Task DownloadMissingDetailsAsync(IEnumerable<ItchCatalogEntry> entries)
    {
        List<ItchCatalogEntry> selected = entries.Where(entry => entry != null).ToList();
        for (int offset = 0; offset < selected.Count; offset += 6)
        {
            await Task.WhenAll(selected.Skip(offset).Take(6).Select(DownloadEntryDetailsAsync));
            SaveCache();
            if (shutdown) return;
        }
    }

    private static async Task DownloadEntryDetailsAsync(ItchCatalogEntry entry)
    {
        entry.ScreenshotUrls ??= new List<string>();
        if ((!entry.PageMetadataFetched || !entry.PageDescriptionFetched) &&
            !string.IsNullOrWhiteSpace(entry.Url))
        {
            try
            {
                string html = await Client.GetStringAsync(entry.Url);
                if (!entry.PageMetadataFetched)
                    entry.ScreenshotUrls = ExtractScreenshotUrls(html).ToList();
                string pageDescription = ExtractPageDescription(html);
                if (!string.IsNullOrWhiteSpace(pageDescription))
                    entry.Description = pageDescription;
                entry.PageMetadataFetched = true;
                entry.PageDescriptionFetched = true;
            }
            catch (Exception exception)
            {
                Core.Log?.Warning($"Could not inspect itch.io page for '{entry.Title}': {exception.Message}");
            }
        }

        await Task.WhenAll(entry.ScreenshotUrls.Take(ScreenshotLimit)
            .Where(url => !(TryReadImage(url)?.Length > 0))
            .Select(url => DownloadImageAsync(entry, url, "screenshot")));
    }

    private static IEnumerable<string> ExtractScreenshotUrls(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in ScreenshotAnchors.Matches(html))
        {
            Match href = HrefAttribute.Match(anchor.Value);
            string url = WebUtility.HtmlDecode(href.Groups["url"].Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) ||
                !string.Equals(parsed.Host, "img.itch.zone", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(url))
                continue;
            yield return url;
            if (seen.Count >= ScreenshotLimit) yield break;
        }
    }

    private static string ExtractPageDescription(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        int marker = html.IndexOf("formatted_description", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return string.Empty;
        int start = html.LastIndexOf("<div", marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        int end = html.IndexOf("more_information_toggle", marker, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = Math.Min(html.Length, start + 50000);
        string section = html.Substring(start, end - start);

        var paragraphs = new List<string>();
        foreach (Match block in DescriptionBlocks.Matches(section))
        {
            string text = CleanPageText(block.Groups["text"].Value);
            if (text.Length < 24 || IsPromotionalPageText(text) ||
                paragraphs.Contains(text, StringComparer.OrdinalIgnoreCase))
                continue;
            paragraphs.Add(text);
            if (string.Join(" ", paragraphs).Length >= 320) break;
        }

        return LimitCaseDescription(string.Join(" ", paragraphs), 200);
    }

    private static string CleanPageText(string html)
    {
        string text = BreakTags.Replace(html ?? string.Empty, " ");
        text = Tags.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static bool IsPromotionalPageText(string text)
    {
        string value = text.ToLowerInvariant();
        return value.Contains("wishlist") || value.Contains("follow me") ||
            value.Contains("follow us") || value.Contains("join our discord") ||
            value.Contains("contact:") || value.Contains("my email") ||
            value.StartsWith("credits", StringComparison.Ordinal);
    }

    private static string LimitCaseDescription(string value, int maxLength)
    {
        value = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
        if (value.Length <= maxLength) return value;
        int cut = value.LastIndexOf(' ', maxLength - 3);
        if (cut < maxLength / 2) cut = maxLength - 3;
        return value.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '-', '.') + "...";
    }

    private static async Task DownloadImageAsync(
        ItchCatalogEntry entry,
        string url,
        string kind)
    {
        try
        {
            byte[] image = await Client.GetByteArrayAsync(url);
            if (image.Length > 0) File.WriteAllBytes(ImagePath(url), image);
        }
        catch (Exception exception)
        {
            Core.Log?.Warning($"Could not download {kind} for '{entry.Title}': {exception.Message}");
        }
    }

    private async Task<List<ItchCatalogEntry>> DownloadCatalogueAsync()
    {
        IReadOnlyList<string> paths = ItchStandSettings.GetCataloguePaths();
        var pools = new List<Dictionary<string, ItchCatalogEntry>>(paths.Count);
        foreach (string path in paths)
        {
            var pool = new Dictionary<string, ItchCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            for (int offset = 1; offset <= ItchStandSettings.PageCount; offset += 3)
            {
                int firstPage = offset;
                string[] pages = await Task.WhenAll(
                    Enumerable.Range(firstPage, Math.Min(3, ItchStandSettings.PageCount - firstPage + 1))
                        .Select(page => Client.GetStringAsync(path + "?page=" + page)));
                foreach (string html in pages)
                    foreach (ItchCatalogEntry entry in ParseCataloguePage(html))
                        pool[entry.Id] = entry;
            }
            if (pool.Count == 0)
                throw new InvalidOperationException("itch.io returned no games for " + path + ".");
            pools.Add(pool);
        }

        IEnumerable<ItchCatalogEntry> matches = pools[0].Values;
        foreach (Dictionary<string, ItchCatalogEntry> pool in pools.Skip(1))
            matches = matches.Where(entry => pool.ContainsKey(entry.Id));

        Dictionary<string, ItchCatalogEntry> oldEntries = cache.Entries
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        List<ItchCatalogEntry> result = matches
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Url))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        foreach (ItchCatalogEntry entry in result)
            if (oldEntries.TryGetValue(entry.Id, out ItchCatalogEntry old))
                CarryCachedPageDetails(entry, old);
        return result;
    }

    private static IEnumerable<ItchCatalogEntry> ParseCataloguePage(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) yield break;
        foreach (Match match in CatalogueCells.Matches(html))
        {
            string cell = match.Value;
            Match thumb = ThumbAnchor.Match(cell);
            string url = WebUtility.HtmlDecode(HrefAttribute.Match(thumb.Value).Groups["url"].Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                !uri.Host.EndsWith(".itch.io", StringComparison.OrdinalIgnoreCase))
                continue;

            string title = ExtractClassText(cell, "game_title");
            string imageUrl = ExtractAttribute(cell, "data-lazy_src");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(imageUrl) ||
                imageUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return new ItchCatalogEntry
            {
                Id = url.TrimEnd('/'),
                Title = title,
                Url = url,
                ImageUrl = imageUrl,
                Creator = ExtractClassText(cell, "game_author"),
                Description = ExtractClassText(cell, "game_text"),
                Genre = ExtractClassText(cell, "game_genre"),
                Platforms = ExtractPlatforms(cell)
            };
        }
    }

    private static string ExtractClassText(string html, string className)
    {
        Match match = Regex.Match(
            html ?? string.Empty,
            $"<div\\b(?=[^>]*\\bclass=([\"'])[^\"']*\\b{Regex.Escape(className)}\\b[^\"']*\\1)[^>]*>(?<text>.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return CleanPageText(match.Groups["text"].Value);
    }

    private static string ExtractAttribute(string html, string attribute)
    {
        Match match = Regex.Match(
            html ?? string.Empty,
            $"\\b{Regex.Escape(attribute)}\\s*=\\s*([\"'])(?<value>.*?)\\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
    }

    private static string ExtractPlatforms(string cell)
    {
        var platforms = new List<string>();
        if (cell.IndexOf("web_flag", StringComparison.OrdinalIgnoreCase) >= 0) platforms.Add("Browser");
        AddPlatform(cell, platforms, "Download for Windows", "Windows");
        AddPlatform(cell, platforms, "Download for Linux", "Linux");
        AddPlatform(cell, platforms, "Download for macOS", "macOS");
        AddPlatform(cell, platforms, "Download for Android", "Android");
        AddPlatform(cell, platforms, "Download for iOS", "iOS");
        return string.Join(", ", platforms);
    }

    private static void AddPlatform(string cell, ICollection<string> platforms, string marker, string label)
    {
        if (cell.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) platforms.Add(label);
    }

    private static void CarryCachedPageDetails(ItchCatalogEntry entry, ItchCatalogEntry old)
    {
        entry.ScreenshotUrls = old.ScreenshotUrls ?? new List<string>();
        entry.PageMetadataFetched = old.PageMetadataFetched;
        entry.PageDescriptionFetched = old.PageDescriptionFetched;
        if (old.PageDescriptionFetched && !string.IsNullOrWhiteSpace(old.Description))
            entry.Description = old.Description;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Boxroom-ItchStand/1.0.0 (+https://itch.io)");
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

    private static string ImagePath(string url)
    {
        using SHA256 sha = SHA256.Create();
        string name = string.Concat(
            sha.ComputeHash(Encoding.UTF8.GetBytes(url)).Select(value => value.ToString("x2")));
        return Path.Combine(CoverRoot, name + ".img");
    }

    private static byte[] TryReadImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            string path = ImagePath(url);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<byte[]> ReadScreenshots(IEnumerable<string> urls) =>
        (urls ?? Array.Empty<string>())
            .Take(ScreenshotLimit)
            .Select(TryReadImage)
            .Where(bytes => bytes?.Length > 0)
            .ToList();

    private void SaveCache()
    {
        lock (cacheSaveLock)
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
}
