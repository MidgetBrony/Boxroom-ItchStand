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
        definition.HeldPrefabFactory = CreateInspectorBox;
        definition.Visuals = new MediaVisualDefinition
        {
            OnHeld = ApplyPresentation,
            OnInspect = ApplyPresentation
        };
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

    private static GameObject CreateInspectorBox()
    {
        Core.Log?.Msg("Creating native itch.io inspector GameBox visual.");
        BoxInspector inspector = UnityEngine.Object.FindFirstObjectByType<BoxInspector>();
        if (inspector?.Box == null)
        {
            Core.Log?.Error("BOXROOM's native inspector GameBox is unavailable.");
            return null;
        }

        GameObject clone = UnityEngine.Object.Instantiate(inspector.Box.gameObject);
        clone.name = "itch.io Native Inspector GameBox";
        clone.SetActive(true);
        Box box = clone.GetComponent<Box>();
        NativeGameBoxPresentation presentation = clone.AddComponent<NativeGameBoxPresentation>();
        presentation.Capture(box);
        SteamGameDataProvider provider = clone.GetComponent<SteamGameDataProvider>();
        if (box != null) UnityEngine.Object.DestroyImmediate(box);
        if (provider != null) UnityEngine.Object.DestroyImmediate(provider);
        return clone;
    }

    private static void ApplyPresentation(MediaVisualContext context)
    {
        if (context?.Visual == null || context.Item is not ItchGameItem item) return;
        Core.Log?.Msg($"Applying inspector GameBox presentation for '{item.DisplayName}'.");
        NativeGameBoxPresentation presentation = context.Visual.GetComponent<NativeGameBoxPresentation>();
        presentation?.Apply(item);
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
