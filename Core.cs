using HarmonyLib;
using MelonLoader;
using SteamShelf;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

[assembly: MelonInfo(typeof(Boxroom_ItchStand.Core), "The Itch Stand", "1.0.2", "Rusty", null)]
[assembly: MelonGame("NestedLoop", "BOXROOM")]
[assembly: MelonAdditionalDependencies("ModsPanel")]

namespace Boxroom_ItchStand;

public sealed class Core : MelonMod
{
    internal const string PlaceableId = "Boxroom_ItchStand_RentalRack";
    internal static PlaceableData Data;
    internal static PlaceableData Template;
    internal static MelonLogger.Instance Log;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        ItchStandSettings.Initialize();
        SteamLibrarySystem.OnLibraryReady += OnNativeGameLibraryReady;
        CreatePlaceableData();
        RegisterPlaceableDataLive();
        ItchLibrary.Instance.LoadCache();
        if (SteamLibrarySystem.GetConfig() != null)
            ItchLibrary.Instance.OnNativeGameLibraryReady();
        _ = ItchLibrary.Instance.EnsureDailySelectionAsync();
        LoggerInstance.Msg("The Itch Stand 1.0.2 initialized with fast paginated itch.io discovery.");
    }

    public override void OnDeinitializeMelon()
    {
        try
        {
            BoxInspector inspector = UnityEngine.Object.FindFirstObjectByType<BoxInspector>();
            if (inspector?.heldInfo is SteamGameData game && ItchLibrary.Instance.IsManagedGame(game))
                AccessTools.Method(typeof(BoxInspector), "EndView")?.Invoke(inspector, null);
        }
        catch (Exception exception)
        {
            Log?.Warning($"Could not close the active itch.io inspector during reload: {exception.Message}");
        }

        foreach (ItchStandController stand in Resources.FindObjectsOfTypeAll<ItchStandController>())
            if (stand != null && stand.gameObject.scene.IsValid())
                UnityEngine.Object.DestroyImmediate(stand.gameObject);

        SteamLibrarySystem.OnLibraryReady -= OnNativeGameLibraryReady;
        ItchStandSettings.Shutdown();
        ItchLibrary.Instance.Shutdown();
        ItchStandFactory.ReleaseResources();
        if (Data != null)
        {
            if (Singleton<PlaceableManager>.HasInstance())
            {
                PlaceableManager manager = Singleton<PlaceableManager>.Instance;
                manager.AllPlaceables?.Remove(Data);
                var modPlaceables = AccessTools.Field(typeof(PlaceableManager), "modsPlaceables")
                    ?.GetValue(manager) as List<PlaceableData>;
                modPlaceables?.Remove(Data);
            }
            UnityEngine.Object.DestroyImmediate(Data);
        }
        Data = null;
        Template = null;
        Log = null;
    }

    private static void OnNativeGameLibraryReady(IReadOnlyList<SteamGameData> _)
    {
        ItchLibrary.Instance.OnNativeGameLibraryReady();
    }

    private static void CreatePlaceableData()
    {
        Data = ScriptableObject.CreateInstance<PlaceableData>();
        Data.ID = PlaceableId;
        Data.DisplayName = "The Itch Stand";
        Data.DisplayDescription = "A daily selection of free games from itch.io. Press the red button to refresh.";
        Data.PlacementType = PlacementType.Prop;
        Data.ModScale = 1f;
        Data.IsLoadedFromMod = true;
        Data.SetToolType(EToolType.Placeable);
    }

    private static void RegisterPlaceableDataLive()
    {
        if (Data == null || !Singleton<PlaceableManager>.HasInstance()) return;
        PlaceableManager manager = Singleton<PlaceableManager>.Instance;
        if (manager.AllPlaceables != null &&
            manager.AllPlaceables.All(value => value == null || value.ID != PlaceableId))
            manager.AllPlaceables.Insert(0, Data);

        var modPlaceables = AccessTools.Field(typeof(PlaceableManager), "modsPlaceables")
            ?.GetValue(manager) as List<PlaceableData>;
        if (modPlaceables != null && modPlaceables.All(value => value == null || value.ID != PlaceableId))
            modPlaceables.Insert(0, Data);

        Log?.Msg("Registered The Itch Stand in the live placeables catalogue.");
    }

    internal static PlaceableData FindTemplate()
    {
        if (Template != null) return Template;
        if (!Singleton<PlaceableManager>.HasInstance()) return null;

        IEnumerable<PlaceableData> all = Singleton<PlaceableManager>.Instance.AllPlaceables
            .Where(value => value != null && value.ID != PlaceableId);

        Template = all.FirstOrDefault(value => Contains(value.ID, "Retail_GameStore_Video_01"))
            ?? all.FirstOrDefault(value => Contains(value.name, "Placeable_Retail_GameStore_Video_01"))
            ?? all.FirstOrDefault(value => Contains(value.DisplayName, "Video Rack"))
            ?? all.FirstOrDefault(value => Contains(value.ID, "GameStore_Video"));

        if (Template == null)
            Log?.Error("Could not find BOXROOM's native video-rack placeable.");
        return Template;
    }

    private static bool Contains(string value, string needle) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

[HarmonyPatch(typeof(ModsPlaceableLoader), nameof(ModsPlaceableLoader.LoadPlaceableData))]
internal static class LoadPlaceablePatch
{
    private static void Postfix(ref Task<List<PlaceableData>> __result) => __result = Append(__result);

    private static async Task<List<PlaceableData>> Append(Task<List<PlaceableData>> original)
    {
        List<PlaceableData> result = await original ?? new List<PlaceableData>();
        if (Core.Data != null && result.All(value => value == null || value.ID != Core.PlaceableId))
            result.Add(Core.Data);
        return result;
    }
}

[HarmonyPatch(typeof(PlaceableManager), nameof(PlaceableManager.GetDataByID))]
internal static class ResolvePlaceablePatch
{
    private static void Postfix(string id, ref PlaceableData __result)
    {
        if (__result == null && id == Core.PlaceableId) __result = Core.Data;
    }
}

[HarmonyPatch(typeof(PlaceableManager), nameof(PlaceableManager.InstantiatePlaceableAsync))]
internal static class InstantiatePlaceablePatch
{
    private static bool Prefix(PlaceableData data, ref Task<GameObject> __result)
    {
        if (data == null || data.ID != Core.PlaceableId) return true;
        __result = ItchStandFactory.InstantiateAsync();
        return false;
    }
}

[HarmonyPatch(typeof(PlaceableManager), nameof(PlaceableManager.GetSpriteAsync))]
internal static class PlaceableSpritePatch
{
    private static bool Prefix(PlaceableData data, ref Task<Sprite> __result)
    {
        if (data == null || data.ID != Core.PlaceableId) return true;
        PlaceableData template = Core.FindTemplate();
        __result = template == null
            ? Task.FromResult<Sprite>(null)
            : Singleton<PlaceableManager>.Instance.GetSpriteAsync(template);
        return false;
    }
}

// Follow BOXROOM's native shelf lifecycle exactly. ObjectPlacementTool calls
// Shelf.OnPlaced for a new rack; RoomDataManager calls PopulateFromLoad for a
// restored rack. Shelf remains the sole IPlaceable and save-state owner.
[HarmonyPatch(typeof(Shelf), nameof(Shelf.OnPlaced))]
internal static class ItchStandPlacedPatch
{
    private static void Postfix(Shelf __instance)
    {
        if (__instance?.GetComponent<PlacementTag>()?.PlaceableData?.ID != Core.PlaceableId) return;
        __instance.GetComponent<ItchStandController>()?.OnNativeShelfReady();
    }
}

[HarmonyPatch(typeof(Shelf), nameof(Shelf.PopulateFromLoad))]
internal static class ItchStandLoadedPatch
{
    private static void Postfix(Shelf __instance)
    {
        if (__instance?.GetComponent<PlacementTag>()?.PlaceableData?.ID != Core.PlaceableId) return;
        __instance.GetComponent<ItchStandController>()?.OnNativeShelfReady();
    }
}

// Shelf is BOXROOM's actual IPlaceable for the cloned rack. Mirror its pickup
// lifecycle so the Itch controller can refill when the same rack is placed again.
[HarmonyPatch(typeof(Shelf), nameof(Shelf.OnPickedUp))]
internal static class ItchStandPickedUpPatch
{
    private static void Postfix(Shelf __instance)
    {
        if (__instance?.GetComponent<PlacementTag>()?.PlaceableData?.ID != Core.PlaceableId) return;
        __instance.GetComponent<ItchStandController>()?.OnNativeShelfPickedUp();
    }
}

// The native primary action normally removes a case and starts the placement
// tool. On this stand it invokes BOXROOM's own inspect-in-place path instead.
[HarmonyPatch(typeof(PlayerInteractionTool), "PickupOrPlaceFromCurrentMediaContainer")]
internal static class InspectOnlyStandPatch
{
    private static readonly System.Reflection.MethodInfo InspectInPlace =
        AccessTools.Method(typeof(PlayerInteractionTool), "TryInspectLookedAtMediaInPlace");

    private static bool Prefix(
        PlayerInteractionTool __instance,
        PlaceableMediaContainer ___currentLookingAtContainer,
        IShelfItem ___currentLookingAtShelfItem)
    {
        if (___currentLookingAtContainer == null ||
            ___currentLookingAtContainer.GetComponentInParent<ItchStandController>() == null)
            return true;

        ItchStandController controller =
            ___currentLookingAtContainer.GetComponentInParent<ItchStandController>();
        if (___currentLookingAtShelfItem?.ItemInfo is SteamGameData refreshGame &&
            ItchLibrary.Instance.IsRefreshGame(refreshGame))
        {
            controller.RequestRefresh();
            return false;
        }

        // Use BOXROOM's own TryInspectLookedAtMediaInPlace path. Returning
        // false prevents PickupItem/PlaceItem from ever running on this shelf.
        if (___currentLookingAtShelfItem?.ItemInfo is SteamGameData game &&
            ItchLibrary.Instance.IsManagedGame(game))
            InspectInPlace?.Invoke(__instance, null);
        return false;
    }
}

// BOXROOM also exposes inspect-in-place on its alternate interaction. The
// refresh case should reroll from either binding and should never open the
// ordinary game inspector.
[HarmonyPatch(typeof(PlayerInteractionTool), "TryInspectLookedAtMediaInPlace")]
internal static class RefreshCaseAlternateInteractionPatch
{
    private static bool Prefix(
        PlaceableMediaContainer ___currentLookingAtContainer,
        IShelfItem ___currentLookingAtShelfItem,
        ref bool __result)
    {
        ItchStandController controller =
            ___currentLookingAtContainer?.GetComponentInParent<ItchStandController>();
        if (controller == null ||
            ___currentLookingAtShelfItem?.ItemInfo is not SteamGameData game ||
            !ItchLibrary.Instance.IsRefreshGame(game))
            return true;

        controller.RequestRefresh();
        __result = true;
        return false;
    }
}

