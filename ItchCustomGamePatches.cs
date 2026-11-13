using HarmonyLib;
using SteamShelf;
using SteamShelf.PlayerTools;
using UnityEngine;

namespace Boxroom_ItchStand;

// BOXROOM launches native Custom Games as local executables. The itch stand's
// entries are genuine Custom Games too, but their launch target is the source
// page stored in LaunchArguments by ItchLibrary.
[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.LaunchGame))]
internal static class ItchLaunchPatch
{
    private static bool Prefix(BoxInspector __instance) => OpenItch(__instance);

    internal static bool OpenItch(BoxInspector inspector)
    {
        SteamGameData game = inspector?.heldInfo;
        if (!ItchLibrary.Instance.TryGetUrl(game, out string url)) return true;
        Application.OpenURL(url);
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
        if (!ItchLibrary.Instance.IsManagedGame(__instance?.heldInfo)) return true;
        __result = true;
        return false;
    }
}
