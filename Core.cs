using BR_MediaAPI;
using HarmonyLib;
using MelonLoader;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

[assembly: MelonInfo(typeof(Boxroom_ItchStand.Core), "The Itch Stand", "0.1.9", "Rusty", null)]
[assembly: MelonGame("NestedLoop", "BOXROOM")]
[assembly: MelonAdditionalDependencies("BR_MediaAPI")]

namespace Boxroom_ItchStand;

public sealed class Core : MelonMod
{
    internal const int MediaTypeId = 18101;
    internal const string PlaceableId = "Boxroom_ItchStand_RentalRack";
    internal static PlaceableData Data;
    internal static PlaceableData Template;
    internal static MelonLogger.Instance Log;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        RegisterMediaType();
        CreatePlaceableData();
        RegisterPlaceableDataLive();
        ItchLibrary.Instance.LoadCache();
        _ = ItchLibrary.Instance.EnsureDailySelectionAsync();
        LoggerInstance.Msg("The Itch Stand 0.1.9 initialized locally.");
    }

    public override void OnDeinitializeMelon()
    {
        try
        {
            BoxInspector inspector = UnityEngine.Object.FindFirstObjectByType<BoxInspector>();
            if (inspector?.heldMediaInfo is ItchGameItem)
                AccessTools.Method(typeof(BoxInspector), "EndView")?.Invoke(inspector, null);
        }
        catch (Exception exception)
        {
            Log?.Warning($"Could not close the active itch.io inspector during reload: {exception.Message}");
        }

        foreach (ItchStandController stand in Resources.FindObjectsOfTypeAll<ItchStandController>())
            if (stand != null && stand.gameObject.scene.IsValid())
                UnityEngine.Object.DestroyImmediate(stand.gameObject);

        ItchLibrary.Instance.Shutdown();
        MediaApi.Unregister("com.rusty.boxroom.itch-stand");
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

    private static void RegisterMediaType()
    {
        var definition = new MediaTypeDefinition
        {
            Id = MediaTypeId,
            Key = "com.rusty.boxroom.itch-stand",
            DisplayName = "itch.io Games",
            ModelType = typeof(ItchGameItem),
            Library = ItchLibrary.Instance,
            AllowOnShelves = true,
            UseGenericInteractionLifecycle = true,
            CreateUnplacedMediaBox = false,
            Inspect = new MediaInspectDefinition
            {
                PrimaryActionLabel = "View on itch.io",
                OnPrimaryAction = context =>
                {
                    if (context.Item is ItchGameItem game && Uri.TryCreate(game.Url, UriKind.Absolute, out _))
                        Application.OpenURL(game.Url);
                }
            }
        };

        // Itch games use BOXROOM's native GameBox shelf and inspector models.
        // No loose prefab is registered, so these cases can never become furniture.
        NativeGameBoxVisuals.Configure(definition);
        MediaApi.Register(definition);
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

// The native primary action normally removes a case and starts the placement
// tool. On this stand it invokes BOXROOM's own inspect-in-place path instead.
[HarmonyPatch(typeof(PlayerInteractionTool), "PickupOrPlaceFromCurrentMediaContainer")]
internal static class InspectOnlyStandPatch
{
    private static bool Prefix(
        PlayerInteractionTool __instance,
        PlaceableMediaContainer ___currentLookingAtContainer,
        IShelfItem ___currentLookingAtShelfItem)
    {
        if (___currentLookingAtContainer == null ||
            ___currentLookingAtContainer.GetComponentInParent<ItchStandController>() == null)
            return true;

        if (___currentLookingAtShelfItem?.ItemInfo is ItchGameItem game)
            ItchInspectRuntime.Open(__instance, game, "primary");
        return false;
    }
}

// BOXROOM exposes shelf inspection on the secondary/Open binding as well as on
// the primary pickup path. Both must use the same forced transition or one of
// the controls can silently remain on PlayerInteraction.
[HarmonyPatch(typeof(PlayerInteractionTool), "TryInspectLookedAtMediaInPlace")]
internal static class InspectOnlyStandSecondaryPatch
{
    private static bool Prefix(
        PlayerInteractionTool __instance,
        PlaceableMediaContainer ___currentLookingAtContainer,
        IShelfItem ___currentLookingAtShelfItem,
        ref bool __result)
    {
        if (___currentLookingAtContainer == null ||
            ___currentLookingAtContainer.GetComponentInParent<ItchStandController>() == null ||
            ___currentLookingAtShelfItem?.ItemInfo is not ItchGameItem game)
            return true;

        __result = ItchInspectRuntime.Open(__instance, game, "secondary");
        return false;
    }
}

internal static class ItchInspectRuntime
{
    private static readonly System.Reflection.FieldInfo Controller =
        AccessTools.Field(typeof(PlayerTool), "controller");

    internal static bool Open(PlayerInteractionTool interaction, ItchGameItem game, string route)
    {
        PlayerToolController controller = Controller?.GetValue(interaction) as PlayerToolController;
        BoxInspector inspector = controller?.GetToolClass<BoxInspector>();
        if (controller == null || inspector == null)
        {
            Core.Log?.Error($"Inspect ({route}) could not find BOXROOM's inspector tool.");
            return false;
        }

        try
        {
            inspector.BeginInspectInPlace(game, null);
            controller.SetActiveTool(EToolType.None, force: true);
            Core.Log?.Msg($"Inspect ({route}) opened '{game.DisplayName}' in place.");
            return true;
        }
        catch (Exception exception)
        {
            Core.Log?.Error($"Inspect ({route}) failed for '{game.DisplayName}': {exception}");
            return false;
        }
    }
}

