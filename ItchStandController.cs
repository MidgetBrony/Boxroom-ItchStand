using MelonLoader;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.Save;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace Boxroom_ItchStand;

public sealed class ItchStandController : MonoBehaviour, IPlaceable
{
    private PlaceableSaveState saveState;
    private PlaceableMediaContainer[] containers = Array.Empty<PlaceableMediaContainer>();
    private bool initialized;
    private bool active;
    private int appliedRevision = -1;
    private Coroutine watchRoutine;
    private Coroutine refreshRoutine;

    public PlaceableSaveState SaveState => saveState;

    private void Awake() => containers = GetComponentsInChildren<PlaceableMediaContainer>(true);

    public void OnPlaced()
    {
        active = true;
        Initialize();
        ItchLibrary.Instance.OnLibraryReady -= OnLibraryReady;
        ItchLibrary.Instance.OnLibraryReady += OnLibraryReady;
        if (ItchLibrary.Instance.IsReady) Fill();
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
        ItchLibrary.Instance.OnLibraryReady -= OnLibraryReady;
        if (watchRoutine != null) StopCoroutine(watchRoutine);
        watchRoutine = null;
        if (refreshRoutine != null) StopCoroutine(refreshRoutine);
        refreshRoutine = null;
        Clear();
    }

    public void OnDeleted()
    {
        active = false;
        ItchLibrary.Instance.OnLibraryReady -= OnLibraryReady;
        if (watchRoutine != null) StopCoroutine(watchRoutine);
        watchRoutine = null;
        if (refreshRoutine != null) StopCoroutine(refreshRoutine);
        refreshRoutine = null;
        Clear();
    }

    private void OnDestroy()
    {
        ItchLibrary.Instance.OnLibraryReady -= OnLibraryReady;
    }

    public void RequestRefresh()
    {
        if (refreshRoutine != null || ItchLibrary.Instance.IsRefreshing) return;
        active = true;
        refreshRoutine = StartCoroutine(RefreshRoutine());
    }

    private IEnumerator RefreshRoutine()
    {
        TaskWait wait = new(ItchLibrary.Instance.RerollAsync());
        while (!wait.IsCompleted) yield return null;
        if (wait.Exception != null) Core.Log?.Error($"Itch Stand refresh failed: {wait.Exception.Message}");
        else if (appliedRevision != ItchLibrary.Instance.Revision) Fill();
        refreshRoutine = null;
    }

    private void OnLibraryReady(IReadOnlyList<IMediaItem> _)
    {
        if (active) Fill();
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

// The controller is added after the native rack prefab was deserialized, so it
// is not part of PlacementTag's original UnityEvent listener list. This relay
// activates it only after BOXROOM commits placement, never while it is a preview.
internal sealed class ItchStandPlacementRelay : MonoBehaviour
{
    private UnityEvent placedEvent;
    private UnityAction listener;

    private void Awake()
    {
        PlacementTag tag = GetComponent<PlacementTag>();
        ItchStandController controller = GetComponent<ItchStandController>();
        placedEvent = AccessTools.Field(typeof(PlacementTag), "onPlaced")?.GetValue(tag) as UnityEvent;
        if (placedEvent == null || controller == null) return;
        listener = controller.OnPlaced;
        placedEvent.AddListener(listener);
    }

    private void OnDestroy()
    {
        if (placedEvent != null && listener != null) placedEvent.RemoveListener(listener);
    }
}

