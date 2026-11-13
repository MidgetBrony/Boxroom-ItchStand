# The Itch Stand

A BOXROOM 1.0 furniture mod that turns the native video-rental rack into a configurable itch.io discovery stand.

## Behaviour

- Uses BOXROOM's real `Shelf_Video` furniture and native GameBox cases.
- Uses the edited FBX as the authority for the shelf geometry, matched material colours, and built-in itch.io topper logo.
- Scans multiple pages of itch.io's catalogue once per day and caches a much larger discovery pool locally.
- Adds Mod Settings for price (Free by default), genre, platform, common tags, up to three custom tags, and catalogue depth.
- Combines genre/platform/tag filters by intersecting itch.io catalogue results, then chooses twenty-nine games from the matching pool.
- Pulls up to three screenshots from each selected game's itch.io page and uses them on BOXROOM's native case back and interior postcards.
- Scrapes the full itch.io game-page description into concise back-cover copy; the short feed summary remains the fallback.
- Chooses a deterministic daily random selection; the refresh button rerolls it immediately.
- Rerolls show new catalogue covers and summaries first; richer descriptions and screenshots finish in the background without blocking the shelf refresh.
- Registers twenty-nine game slots plus a centred native "Refresh itch.io Games" case; activating that case updates the same slots and fills all six shelves without accumulating duplicates.
- Cases open directly in BOXROOM's inspector and return to the stand. They never enter inventory or placement mode.
- The inspector action opens the selected game's itch.io page.
- Supports BOXROOM Hot Reload and deploys its approval marker automatically. Use hot reload's save/reload-room option so placed stands are reconstructed from the room save.

Cache data is stored under `UserData/Boxroom-ItchStand`.

## Build

Copy `Directory.Build.user.props.example` to `Directory.Build.user.props`, set `GamePath`, then run:

```text
dotnet build -c Release -p:DeployToGame=false
```

Requires ModsPanel 2.4.2 or newer. No additional media framework is required.

