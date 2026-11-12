using BR_MediaAPI;
using HarmonyLib;
using SteamShelf;
using SteamShelf.Media;
using SteamShelf.PlayerTools;
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

        ShelfBox box = UnityEngine.Object.Instantiate(prefab, parent);
        box.gameObject.name = "itch.io Native Shelf GameBox";
        return box;
    }
}

// Native texture caches consume the supplied byte arrays. Restore them before
// BOXROOM binds an itch item again so shelf refreshes and repeat inspections
// keep their artwork without entering Steam's downloader.
[HarmonyPatch(typeof(SteamGameDataProvider), nameof(SteamGameDataProvider.SetGame), typeof(SteamGameData))]
internal static class ItchNativeGameDataPatch
{
    private static void Prefix(SteamGameData data)
    {
        if (data is ItchGameItem game) game.PrepareNativeAssets();
    }
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.LaunchGame))]
internal static class ItchLaunchPatch
{
    private static bool Prefix(BoxInspector __instance) => OpenItch(__instance);

    internal static bool OpenItch(BoxInspector inspector)
    {
        if (inspector?.heldInfo is not ItchGameItem game) return true;
        if (!string.IsNullOrWhiteSpace(game.Url)) Application.OpenURL(game.Url);
        return false;
    }
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.InstallGame))]
internal static class ItchInstallPatch
{
    private static bool Prefix(BoxInspector __instance) => ItchLaunchPatch.OpenItch(__instance);
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.IsHeldGameInstalled))]
internal static class ItchInstalledPatch
{
    private static bool Prefix(BoxInspector __instance, ref bool __result)
    {
        if (__instance?.heldInfo is not ItchGameItem) return true;
        __result = true;
        return false;
    }
}
