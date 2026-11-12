using BR_MediaAPI;
using HarmonyLib;
using SteamShelf;
using SteamShelf.Media;
using SteamShelf.PlayerTools;
using System;
using TMPro;
using UnityEngine;

namespace Boxroom_ItchStand;

internal static class NativeGameBoxVisuals
{
    internal static void Configure(MediaTypeDefinition definition)
    {
        definition.ShelfFactory = CreateShelfItem;
        definition.HeldPrefabFactory = null;
        definition.UseGenericInteractionLifecycle = false;
    }

    private static IShelfItem CreateShelfItem(Transform parent)
    {
        ShelfBox prefab = SteamLibrarySystem.GetConfig()?.ShelfBoxPrefab;
        if (prefab == null)
        {
            Core.Log?.Error("BOXROOM's native ShelfBox prefab is unavailable.");
            return null;
        }

        ShelfBox native = UnityEngine.Object.Instantiate(prefab, parent);
        native.gameObject.name = "itch.io Native GameBox Shelf Case";
        NativeGameBoxShelfItem adapter = native.gameObject.AddComponent<NativeGameBoxShelfItem>();
        adapter.Capture(native);
        SteamGameDataProvider provider = native.GetComponent<SteamGameDataProvider>();
        UnityEngine.Object.DestroyImmediate(native);
        if (provider != null) UnityEngine.Object.DestroyImmediate(provider);
        native = null;
        return adapter;
    }

    internal static void ApplyToNativeInspector(BoxInspector inspector, ItchGameItem item)
    {
        if (inspector?.Box == null || item == null) return;
        Component album = AccessTools.Field(typeof(BoxInspector), "albumBox")?.GetValue(inspector) as Component;
        Component video = AccessTools.Field(typeof(BoxInspector), "videoBox")?.GetValue(inspector) as Component;
        if (album != null) AccessTools.Method(album.GetType(), "SetBoxShowing")?.Invoke(album, new object[] { false });
        if (video != null) AccessTools.Method(video.GetType(), "SetBoxShowing")?.Invoke(video, new object[] { false });

        inspector.Box.SetBoxShowing(true);
        NativeGameBoxPresentation presentation = inspector.Box.GetComponent<NativeGameBoxPresentation>();
        if (presentation == null)
        {
            presentation = inspector.Box.gameObject.AddComponent<NativeGameBoxPresentation>();
            presentation.Capture(inspector.Box);
        }
        presentation.Apply(item);
        InspectUIAnchors anchors = inspector.Box.GetComponent<InspectUIAnchors>();
        if (anchors != null)
            AccessTools.Property(typeof(BoxInspector), nameof(BoxInspector.ActiveUIAnchors))?.SetValue(inspector, anchors);
        Core.Log?.Msg($"Applied '{item.DisplayName}' to BOXROOM's live inspector GameCase.");
    }
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.SetHeldMedia))]
[HarmonyPriority(Priority.Last)]
internal static class ItchNativeInspectorMediaPatch
{
    private static void Postfix(BoxInspector __instance, IMediaItem item)
    {
        if (item is ItchGameItem game) NativeGameBoxVisuals.ApplyToNativeInspector(__instance, game);
    }
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.OnToolActivated))]
[HarmonyPriority(Priority.Last)]
internal static class ItchNativeInspectorActivatedPatch
{
    private static void Postfix(BoxInspector __instance)
    {
        if (__instance?.heldMediaInfo is ItchGameItem game)
            NativeGameBoxVisuals.ApplyToNativeInspector(__instance, game);
    }
}

internal sealed class NativeGameBoxShelfItem : MonoBehaviour, IShelfItem
{
    private Renderer caseRenderer;
    private TextMeshPro spineLabel;
    private TextMeshPro coverLabel;
    private Texture2D nullCover;
    private Texture2D coverTexture;
    private ItchGameItem item;
    private bool fullDisplay = true;
    private bool coverHidden;

    public bool HasItemAndIsActive => item != null && fullDisplay && gameObject.activeInHierarchy;
    public IMediaItem ItemInfo => item;

    internal void Capture(ShelfBox native)
    {
        caseRenderer = native.rend;
        spineLabel = native.Text_Name;
        coverLabel = native.Text_NameCover;
        nullCover = native.nullCover;
        Clear();
    }

    public void SetItem(IMediaItem value, bool playTween = false)
    {
        if (value is not ItchGameItem game) { Clear(); return; }
        item = game;
        coverHidden = false;
        if (spineLabel != null) spineLabel.text = game.DisplayName;
        if (coverLabel != null) coverLabel.text = game.DisplayName;
        ApplyCover(game.CoverArtBytes);
        if (caseRenderer != null && caseRenderer.sharedMaterials.Length > 3 && ItchStandFactory.LogoTexture != null)
            MaterialHelpers.SetTexture(caseRenderer, 3, ItchStandFactory.LogoTexture);
    }

    public void SetItem(MediaRef mediaRef) => SetItem(ItchLibrary.Instance.GetItemSync(mediaRef));

    public void Clear()
    {
        item = null;
        coverHidden = false;
        ReleaseCover();
        if (caseRenderer != null && caseRenderer.sharedMaterials.Length > 1)
            MaterialHelpers.SetTexture(caseRenderer, 1, nullCover);
        if (spineLabel != null) spineLabel.text = string.Empty;
        if (coverLabel != null) coverLabel.text = string.Empty;
    }

    public void HideCoverArt()
    {
        coverHidden = true;
        if (caseRenderer != null && caseRenderer.sharedMaterials.Length > 1)
            MaterialHelpers.SetTexture(caseRenderer, 1, nullCover);
    }

    public void ShowCoverArt()
    {
        coverHidden = false;
        if (item != null) ApplyCover(item.CoverArtBytes);
    }

    public void SetFullDisplayActive(bool active)
    {
        fullDisplay = active;
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true)) renderer.enabled = active;
        foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true)) label.enabled = active;
    }

    public void SetHoveredDisplayActive(bool active) { }

    private void ApplyCover(byte[] bytes)
    {
        ReleaseCover();
        if (coverHidden || caseRenderer == null || bytes == null || bytes.Length == 0) return;
        coverTexture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!coverTexture.LoadImage(bytes)) { ReleaseCover(); return; }
        MaterialHelpers.SetTexture(caseRenderer, 1, coverTexture);
        if (coverLabel != null) coverLabel.text = string.Empty;
    }

    private void ReleaseCover()
    {
        if (coverTexture != null) Destroy(coverTexture);
        coverTexture = null;
    }

    private void OnDestroy() => ReleaseCover();
}

internal sealed class NativeGameBoxPresentation : MonoBehaviour
{
    private static readonly System.Reflection.FieldInfo BoxRendererField = AccessTools.Field(typeof(Box), "boxRenderer");
    private static readonly System.Reflection.FieldInfo DiskField = AccessTools.Field(typeof(Box), "disk");
    private static readonly System.Reflection.FieldInfo CartridgeField = AccessTools.Field(typeof(Box), "cartridge");
    private static readonly System.Reflection.FieldInfo NameField = AccessTools.Field(typeof(Box), "textName");
    private static readonly System.Reflection.FieldInfo DeveloperField = AccessTools.Field(typeof(Box), "textDev");
    private static readonly System.Reflection.FieldInfo CoverNameField = AccessTools.Field(typeof(Box), "textNameCover");
    private static readonly System.Reflection.FieldInfo DiskNameField = AccessTools.Field(typeof(Box), "textNameDisk");

    private Renderer caseRenderer;
    private Renderer diskRenderer;
    private Renderer cartridgeRenderer;
    private TextMeshPro nameLabel;
    private TextMeshPro developerLabel;
    private TextMeshPro coverNameLabel;
    private TextMeshPro diskNameLabel;
    private Texture2D coverTexture;
    internal void Capture(Box box)
    {
        if (box == null) return;
        caseRenderer = BoxRendererField?.GetValue(box) as Renderer;
        diskRenderer = DiskField?.GetValue(box) as Renderer;
        cartridgeRenderer = CartridgeField?.GetValue(box) as Renderer;
        nameLabel = NameField?.GetValue(box) as TextMeshPro;
        developerLabel = DeveloperField?.GetValue(box) as TextMeshPro;
        coverNameLabel = CoverNameField?.GetValue(box) as TextMeshPro;
        diskNameLabel = DiskNameField?.GetValue(box) as TextMeshPro;
    }

    internal void Apply(ItchGameItem item)
    {
        if (item == null) return;
        if (nameLabel != null) nameLabel.text = item.Title;
        if (developerLabel != null) developerLabel.text = item.Creator;
        if (coverNameLabel != null) coverNameLabel.text = item.Title;
        if (diskNameLabel != null) diskNameLabel.text = item.Title;
        if (item.CoverArtBytes == null || item.CoverArtBytes.Length == 0) return;

        ReleaseCover();
        coverTexture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!coverTexture.LoadImage(item.CoverArtBytes)) { ReleaseCover(); return; }
        if (caseRenderer != null && caseRenderer.sharedMaterials.Length > 1)
        {
            MaterialHelpers.SetTexture(caseRenderer, 1, coverTexture);
            if (caseRenderer.sharedMaterials.Length > 3 && ItchStandFactory.LogoTexture != null)
                MaterialHelpers.SetTexture(caseRenderer, 3, ItchStandFactory.LogoTexture);
        }
        if (diskRenderer != null && diskRenderer.sharedMaterials.Length > 2)
            MaterialHelpers.SetTexture(diskRenderer, 2, coverTexture);
        if (cartridgeRenderer != null && cartridgeRenderer.sharedMaterials.Length > 2)
            MaterialHelpers.SetTexture(cartridgeRenderer, 2, coverTexture);
        if (coverNameLabel != null) coverNameLabel.text = string.Empty;
        if (diskNameLabel != null) diskNameLabel.text = string.Empty;
    }

    private void ReleaseCover()
    {
        if (coverTexture != null) Destroy(coverTexture);
        coverTexture = null;
    }

    private void OnDestroy() => ReleaseCover();
}
