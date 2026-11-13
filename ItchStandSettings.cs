using MelonLoader;
using ModsPanel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Boxroom_ItchStand;

internal static class ItchStandSettings
{
    private const string OwnerId = "Rusty.BoxroomItchStand";

    private static readonly Choice[] Prices =
    {
        new("Free", "free"),
        new("Any price", ""),
        new("Paid", "paid"),
        new("On sale", "on-sale")
    };

    private static readonly Choice[] Genres =
    {
        new("Any genre", ""),
        new("Action", "genre-action"),
        new("Adventure", "genre-adventure"),
        new("Platformer", "genre-platformer"),
        new("Puzzle", "genre-puzzle"),
        new("Role-playing", "genre-rpg"),
        new("Shooter", "genre-shooter"),
        new("Simulation", "genre-simulation"),
        new("Sports", "genre-sports"),
        new("Strategy", "genre-strategy"),
        new("Visual novel", "genre-visual-novel"),
        new("Other", "genre-other")
    };

    private static readonly Choice[] Tags =
    {
        new("Any tag", ""),
        new("Horror", "tag-horror"),
        new("Cute", "tag-cute"),
        new("Creepy", "tag-creepy"),
        new("Romance", "tag-romance"),
        new("Survival", "tag-survival"),
        new("Card game", "tag-card-game"),
        new("Dating sim", "tag-dating-sim"),
        new("Educational", "tag-educational"),
        new("Fighting", "tag-fighting"),
        new("Interactive fiction", "tag-interactive-fiction"),
        new("Psychological horror", "tag-psychological-horror"),
        new("Racing", "tag-racing"),
        new("Rhythm", "tag-rhythm"),
        new("Single-player", "tag-singleplayer"),
        new("No AI", "tag-no-ai")
    };

    private static readonly Choice[] Platforms =
    {
        new("Any platform", ""),
        new("Windows", "platform-windows"),
        new("Browser", "platform-web"),
        new("Linux", "platform-linux"),
        new("macOS", "platform-osx"),
        new("Android", "platform-android"),
        new("iOS", "platform-ios")
    };

    private static MelonPreferences_Entry<int> price;
    private static MelonPreferences_Entry<int> genre;
    private static MelonPreferences_Entry<int> tag;
    private static MelonPreferences_Entry<int> platform;
    private static MelonPreferences_Entry<int> pages;
    private static MelonPreferences_Entry<string> customTags;

    internal static void Initialize()
    {
        MelonPreferences_Category preferences =
            MelonPreferences.CreateCategory("BoxroomItchStand", "The Itch Stand");
        price = preferences.CreateEntry("Price", 0, "Price filter; Free is the default");
        genre = preferences.CreateEntry("Genre", 0, "Optional itch.io genre");
        tag = preferences.CreateEntry("Tag", 0, "Optional common itch.io tag");
        platform = preferences.CreateEntry("Platform", 0, "Optional platform");
        pages = preferences.CreateEntry("CataloguePages", 5, "Pages scanned per filter");
        customTags = preferences.CreateEntry(
            "AdditionalTags", string.Empty, "Up to three extra itch.io tags, comma-separated");
        RegisterMenu();
    }

    internal static void Shutdown() => ModsPanelApi.UnregisterSection(OwnerId);

    internal static string Signature => string.Join("|", new[]
    {
        Slug(Prices, price), Slug(Genres, genre), Slug(Tags, tag),
        Slug(Platforms, platform), NormalizedCustomTags, PageCount.ToString()
    });

    internal static int PageCount => Math.Max(2, Math.Min(10, pages?.Value ?? 5));

    internal static IReadOnlyList<string> GetCataloguePaths()
    {
        var root = new List<string> { "https://itch.io/games" };
        string priceSlug = Slug(Prices, price);
        if (!string.IsNullOrEmpty(priceSlug)) root.Add(priceSlug);
        string basePath = string.Join("/", root);

        var filters = new List<string>();
        AddIfPresent(filters, Slug(Genres, genre));
        AddIfPresent(filters, Slug(Tags, tag));
        AddIfPresent(filters, Slug(Platforms, platform));
        foreach (string custom in ParseCustomTags()) AddIfPresent(filters, "tag-" + custom);

        return filters.Count == 0
            ? new[] { basePath }
            : filters.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(filter => basePath + "/" + filter)
                .ToArray();
    }

    private static void RegisterMenu()
    {
        ModsPanelApi.RegisterSection(OwnerId, "The Itch Stand", 118).Clear()
            .AddDropdown("price", "Price", () => Labels(Prices),
                () => Index(price, Prices.Length), value => Save(price, value, Prices.Length))
            .AddDropdown("genre", "Genre", () => Labels(Genres),
                () => Index(genre, Genres.Length), value => Save(genre, value, Genres.Length))
            .AddDropdown("tag", "Tag", () => Labels(Tags),
                () => Index(tag, Tags.Length), value => Save(tag, value, Tags.Length))
            .AddDropdown("platform", "Platform", () => Labels(Platforms),
                () => Index(platform, Platforms.Length), value => Save(platform, value, Platforms.Length))
            .AddText("additional-tags", "Additional tags", () => customTags?.Value ?? string.Empty,
                value => Save(customTags, NormalizeTagInput(value)), "horror, roguelike")
            .AddSlider("pages", "Catalogue pages per filter", () => PageCount,
                value => Save(pages, (int)Math.Round(value), 2, 10), 2f, 10f, true, "0 pages")
            .AddButton("apply", "Download catalogue and refill stand", "APPLY NOW", ApplyNow)
            .AddLabel("help",
                "Free is the default. Genre, platform and tags are combined. More pages produce a larger, more varied pool but take longer to refresh. The red refresh case also applies changed settings.");
    }

    private static async void ApplyNow()
    {
        if (ItchLibrary.Instance.IsRefreshing)
        {
            ModsUi.ShowToast("The Itch Stand is already refreshing.", 4f);
            return;
        }

        ModsUi.ShowToast("Downloading the itch.io catalogue...", 4f);
        try
        {
            int count = await ItchLibrary.Instance.ApplySettingsAsync();
            ModsUi.ShowToast($"Itch Stand catalogue updated: {count} matching games.", 6f);
        }
        catch (Exception exception)
        {
            Core.Log?.Error($"Could not apply Itch Stand settings: {exception}");
            ModsUi.ShowToast(exception.GetBaseException().Message, 7f);
        }
    }

    private static IReadOnlyList<string> Labels(IEnumerable<Choice> choices) =>
        choices.Select(choice => choice.Label).ToArray();

    private static string Slug(IReadOnlyList<Choice> choices, MelonPreferences_Entry<int> entry) =>
        choices[Index(entry, choices.Count)].Slug;

    private static int Index(MelonPreferences_Entry<int> entry, int count)
    {
        int value = entry?.Value ?? 0;
        return value >= 0 && value < count ? value : 0;
    }

    private static void Save(MelonPreferences_Entry<int> entry, int value, int count)
    {
        if (entry == null || value < 0 || value >= count) return;
        entry.Value = value;
        MelonPreferences.Save();
    }

    private static void Save(MelonPreferences_Entry<int> entry, int value, int minimum, int maximum)
    {
        if (entry == null) return;
        entry.Value = Math.Max(minimum, Math.Min(maximum, value));
        MelonPreferences.Save();
    }

    private static void Save(MelonPreferences_Entry<string> entry, string value)
    {
        if (entry == null) return;
        entry.Value = value ?? string.Empty;
        MelonPreferences.Save();
    }

    private static void AddIfPresent(ICollection<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
    }

    private static string NormalizedCustomTags => string.Join(",", ParseCustomTags());

    private static IEnumerable<string> ParseCustomTags() =>
        (customTags?.Value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeTag)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3);

    private static string NormalizeTagInput(string value) => string.Join(", ",
        (value ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeTag).Where(tagValue => tagValue.Length > 0).Take(3));

    private static string NormalizeTag(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.StartsWith("tag-", StringComparison.Ordinal)) normalized = normalized.Substring(4);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    private sealed class Choice
    {
        internal Choice(string label, string slug) { Label = label; Slug = slug; }
        internal string Label { get; }
        internal string Slug { get; }
    }
}
