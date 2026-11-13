using SteamShelf;
using SteamShelf.Media;
using SteamShelf.Placeables;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Boxroom_ItchStand;

// Deliberately not an IPlaceable. BOXROOM's native Shelf remains the only
// placeable/save owner; this component only places BOXROOM-registered Custom
// Games into the native rows.
public sealed class ItchStandController : MonoBehaviour
{
    private PlaceableMediaContainer[] rows = Array.Empty<PlaceableMediaContainer>();
    private bool active;
    private Coroutine refreshRoutine;
    private Coroutine fillRoutine;

    private void Awake() => rows = GetComponentsInChildren<PlaceableMediaContainer>(true);

    internal void OnNativeShelfReady()
    {
        if (!active)
        {
            active = true;
            ItchLibrary.Instance.OnLibraryReady -= OnLibraryReady;
            ItchLibrary.Instance.OnLibraryReady += OnLibraryReady;
        }

        if (ItchLibrary.Instance.IsReady) RequestFill();
    }

    internal void OnNativeShelfPickedUp()
    {
        active = false;
        ItchLibrary.Instance.OnLibraryReady -= OnLibraryReady;
        if (refreshRoutine != null) StopCoroutine(refreshRoutine);
        if (fillRoutine != null) StopCoroutine(fillRoutine);
        refreshRoutine = null;
        fillRoutine = null;
    }

    private void OnDestroy()
    {
        ItchLibrary.Instance.OnLibraryReady -= OnLibraryReady;
    }

    public void RequestRefresh()
    {
        if (refreshRoutine != null || ItchLibrary.Instance.IsRefreshing) return;
        refreshRoutine = StartCoroutine(RefreshRoutine());
    }

    private IEnumerator RefreshRoutine()
    {
        TaskWait wait = new(ItchLibrary.Instance.RerollAsync());
        while (!wait.IsCompleted) yield return null;
        if (wait.Exception != null)
            Core.Log?.Error($"Itch Stand refresh failed: {wait.Exception.Message}");
        refreshRoutine = null;
    }

    private void OnLibraryReady(IReadOnlyList<SteamGameData> _)
    {
        if (active) RequestFill();
    }

    private void RequestFill()
    {
        if (!active || !ItchLibrary.Instance.IsReady) return;
        if (fillRoutine != null) StopCoroutine(fillRoutine);
        fillRoutine = StartCoroutine(FillNativeShelfRoutine());
    }

    private IEnumerator FillNativeShelfRoutine()
    {
        rows = GetComponentsInChildren<PlaceableMediaContainer>(true)
            .OrderByDescending(row => transform.InverseTransformPoint(row.transform.position).y)
            .ToArray();
        foreach (PlaceableMediaContainer row in rows)
            row.ClearAll();

        // PlaceableMediaContainer.ClearAll uses Unity Destroy, so its old case
        // colliders remain until the end of this frame. Refilling immediately
        // makes CanPlaceAt reject one of those still-blocked cells.
        yield return null;
        if (!active)
        {
            fillRoutine = null;
            yield break;
        }

        rows = GetComponentsInChildren<PlaceableMediaContainer>(true)
            .OrderByDescending(row => transform.InverseTransformPoint(row.transform.position).y)
            .ToArray();
        foreach (PlaceableMediaContainer row in rows)
        {
            row.Initialise();
            row.RestrictPlacements(MediaPlacementFlags.FaceOut, MediaPlacement.FaceOut);
        }

        SteamGameData refreshGame = ItchLibrary.Instance.GetRefreshGame();
        bool refreshPlaced = refreshGame != null && rows.Length > 0 &&
            PlaceRefreshCase(rows[0], refreshGame);

        IReadOnlyList<SteamGameData> games = ItchLibrary.Instance.GetKnownGames();
        int gameIndex = 0;
        int[] nextSlots = new int[rows.Length];
        bool placedInPass = true;

        // Use BOXROOM's own rows and PlaceItem path. Distribute one visible
        // SteamGameData case per row on each pass.
        while (gameIndex < games.Count && placedInPass)
        {
            placedInPass = false;
            for (int rowIndex = 0; rowIndex < rows.Length && gameIndex < games.Count; rowIndex++)
            {
                PlaceableMediaContainer row = rows[rowIndex];
                SteamGameData game = games[gameIndex];
                for (int slot = nextSlots[rowIndex]; slot < row.MaxGameCount; slot++)
                {
                    nextSlots[rowIndex] = slot + 1;
                    if (row.IsCellOccupied(slot)) continue;
                    if (!row.CanPlaceAt(slot, game, MediaPlacement.FaceOut)) continue;
                    row.PlaceItem(game, slot, MediaPlacement.FaceOut, playPlacedTween: false);
                    gameIndex++;
                    placedInPass = true;
                    break;
                }
            }
        }

        Core.Log?.Msg(
            $"Filled native BOXROOM shelf with {gameIndex} itch.io games and refresh case={refreshPlaced}.");
        fillRoutine = null;
    }

    private static bool PlaceRefreshCase(PlaceableMediaContainer topRow, SteamGameData refreshGame)
    {
        int span = MediaFootprint.OccupancyCellsFor(
            eMediaType.SteamGame, MediaPlacement.FaceOut, ELayoutAxis.X);
        int caseCapacity = Math.Max(1, topRow.MaxGameCount / span);
        int centeredCase = caseCapacity / 2;
        int alignedSlot = centeredCase * span;
        if (alignedSlot + span > topRow.MaxGameCount)
            alignedSlot = Math.Max(0, topRow.MaxGameCount - span);

        // A face-out game occupies four narrow BOXROOM grid cells. Choosing
        // the geometrically nearest raw cell can split the row into unusable
        // fragments. Centre on a whole-case boundary so five cases still fit.
        if (topRow.IsCellOccupied(alignedSlot)) return false;
        topRow.PlaceItem(refreshGame, alignedSlot, MediaPlacement.FaceOut, playPlacedTween: false);
        return topRow.IsCellOccupied(alignedSlot);
    }

    private sealed class TaskWait
    {
        private readonly System.Threading.Tasks.Task task;
        public TaskWait(System.Threading.Tasks.Task task) => this.task = task;
        public bool IsCompleted => task.IsCompleted;
        public Exception Exception => task.Exception?.GetBaseException();
    }
}
