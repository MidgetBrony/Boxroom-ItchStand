using MelonLoader;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.Save;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Boxroom_ItchStand;

public sealed class ItchStandController : MonoBehaviour, IPlaceable
{
    private PlaceableSaveState saveState;
    private PlaceableMediaContainer[] containers = Array.Empty<PlaceableMediaContainer>();
    private bool initialized;
    private bool active;
    private int appliedRevision = -1;
    private Coroutine watchRoutine;

    public PlaceableSaveState SaveState => saveState;

    private void Awake() => containers = GetComponentsInChildren<PlaceableMediaContainer>(true);

    public void OnPlaced()
    {
        active = true;
        Initialize();
        if (watchRoutine != null) StopCoroutine(watchRoutine);
        watchRoutine = StartCoroutine(WatchSelection());
    }

    public void PopulateFromLoad(PlaceableSaveState state)
    {
        saveState = state;
        OnPlaced();
    }

    public void LinkSaveState(PlaceableSaveState state) => saveState = state;
    public void RefreshSaveStateReference(PlaceableSaveState state) => saveState = state;

    public void OnPickedUp()
    {
        active = false;
        if (watchRoutine != null) StopCoroutine(watchRoutine);
        watchRoutine = null;
        Clear();
    }

    public void OnDeleted()
    {
        active = false;
        if (watchRoutine != null) StopCoroutine(watchRoutine);
        watchRoutine = null;
        Clear();
    }

    public void RequestRefresh()
    {
        if (ItchLibrary.Instance.IsRefreshing) return;
        StartCoroutine(RefreshRoutine());
    }

    private IEnumerator RefreshRoutine()
    {
        TaskWait wait = new(ItchLibrary.Instance.RerollAsync());
        while (!wait.IsCompleted) yield return null;
        if (wait.Exception != null) Core.Log?.Error($"Itch Stand refresh failed: {wait.Exception.Message}");
    }

    private void Initialize()
    {
        if (initialized) return;
        containers = GetComponentsInChildren<PlaceableMediaContainer>(true);
        foreach (PlaceableMediaContainer container in containers)
        {
            container.Initialise();
            container.RestrictPlacements(MediaPlacementFlags.FaceOut, MediaPlacement.FaceOut);
        }
        initialized = true;
    }

    private IEnumerator WatchSelection()
    {
        while (active)
        {
            if (ItchLibrary.Instance.IsReady && appliedRevision != ItchLibrary.Instance.Revision)
                Fill();
            yield return new WaitForSeconds(0.25f);
        }
    }

    private void Fill()
    {
        Clear();
        Initialize();
        IReadOnlyList<IMediaItem> games = ItchLibrary.Instance.GetKnownItems();
        int gameIndex = 0;
        int[] nextSlots = new int[containers.Length];
        bool placedInPass = true;

        // Distribute cases like a shop display: one on every shelf, then make
        // another pass. BOXROOM returns these rows bottom-first, so filling a
        // complete row at a time leaves the upper shelves looking empty.
        while (gameIndex < games.Count && placedInPass)
        {
            placedInPass = false;
            for (int row = 0; row < containers.Length && gameIndex < games.Count; row++)
            {
                PlaceableMediaContainer container = containers[row];
                IMediaItem game = games[gameIndex];
                for (int slot = nextSlots[row]; slot < container.MaxGameCount; slot++)
                {
                    nextSlots[row] = slot + 1;
                    // CanPlaceAt also permits another face-out case at a deeper
                    // stack depth. The stand must use visible shelf cells only.
                    if (container.IsCellOccupied(slot)) continue;
                    if (!container.CanPlaceAt(slot, game, MediaPlacement.FaceOut)) continue;
                    container.PlaceItem(game, slot, MediaPlacement.FaceOut, playPlacedTween: false);
                    gameIndex++;
                    placedInPass = true;
                    break;
                }
            }
        }

        appliedRevision = ItchLibrary.Instance.Revision;
        Core.Log?.Msg($"Filled The Itch Stand with {gameIndex} cases.");
    }

    private void Clear()
    {
        if (containers == null) return;
        foreach (PlaceableMediaContainer container in containers)
            if (container != null) container.ClearAll();
        initialized = false;
    }

    private sealed class TaskWait
    {
        private readonly System.Threading.Tasks.Task task;
        public TaskWait(System.Threading.Tasks.Task task) => this.task = task;
        public bool IsCompleted => task.IsCompleted;
        public Exception Exception => task.Exception?.GetBaseException();
    }
}

public sealed class ItchRefreshButton : MonoBehaviour, IInteractable
{
    public void OnInteract() => GetComponentInParent<ItchStandController>()?.RequestRefresh();
    public void OnAlternateInteract() => OnInteract();
}

