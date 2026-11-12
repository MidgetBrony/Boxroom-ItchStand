# The Itch Stand

A BOXROOM 1.0 furniture mod that turns the native video-rental rack into a daily discovery stand for free itch.io games.

## Behaviour

- Uses BOXROOM's real `Shelf_Video` furniture and native GameBox cases.
- Recolours the stand to itch.io's official `#fa5c5c` brand colour.
- Adds an official white itch.io logo topper and a physical refresh button.
- Pulls the official `https://itch.io/games/price-free.xml` feed once per day and caches it locally.
- Chooses a deterministic daily random selection; the refresh button rerolls it immediately.
- Cases open directly in BOXROOM's inspector and return to the stand. They never enter inventory or placement mode.
- The inspector action opens the selected game's itch.io page.
- Supports BOXROOM Hot Reload and deploys its approval marker automatically. Use hot reload's save/reload-room option so placed stands are reconstructed from the room save.

Cache data is stored under `UserData/Boxroom-ItchStand`.

## Build

Copy `Directory.Build.user.props.example` to `Directory.Build.user.props`, set `GamePath`, then run:

```text
dotnet build -c Release -p:DeployToGame=false
```

Requires BR-MediaAPI 1.1.16 or newer.

