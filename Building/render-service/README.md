# Base Render Service

Turns bases published on the Rust server into real 3D thumbnails.

The Rust game server is headless — no GPU, and it never loads Rust's 3D models — so it
cannot render anything itself. This small service does the rendering instead:

```
Rust server (managed host)                This service (VPS)
   RandomGridSpawn plugin                 ┌──────────────────────────────┐
   player hits + PUBLISH   ──POST JSON──► │ rebuild base from .glb models│
                                          │ render in headless Chrome    │
   base card shows image  ◄──image URL─── │ save + serve the PNG         │
                                          └──────────────────────────────┘
```

**This folder must NOT be uploaded to the game server** — it runs on a machine where you
can run Node (a small VPS is plenty; no GPU needed).

## Setup

1. **Get the models.** Download Facepunch's glTF library and unpack it here, so the path
   `render-service/assets/prefabs/Building Core/...` exists:

   ```
   git clone --depth 1 https://github.com/Facepunch/RustRelay.Assets
   mv RustRelay.Assets/assets ./assets
   ```

   (Or put them anywhere and set `ASSETS_DIR` to that folder.)

2. **Install and run.**

   ```
   npm install
   PUBLIC_URL="http://your-vps-ip:8080" API_KEY="pick-a-secret" npm start
   ```

   On boot it prints how many models it indexed. `GET /health` confirms it's alive.

3. **Point the plugin at it.** In `oxide/config/RandomGridSpawn.json` on the game server:

   ```json
   {
     "Render service URL (blank = disabled)": "http://your-vps-ip:8080/",
     "Render service API key": "pick-a-secret",
     "Render published bases": true,
     "Render quicksaves and autosaves": false
   }
   ```

   Reload the plugin. New publishes now get real renders. To re-render bases saved
   earlier, run `gridspawn.render <CODE>` in the server console.

## Environment variables

| Variable     | Default                 | Purpose                                        |
|--------------|-------------------------|------------------------------------------------|
| `PORT`       | `8080`                  | Port to listen on                              |
| `ASSETS_DIR` | `./assets`              | Where the `.glb` model library lives           |
| `PUBLIC_URL` | `http://localhost:PORT` | How the game server reaches this service       |
| `API_KEY`    | *(none)*                | Shared secret; must match the plugin's config  |

## How a base is mapped to models

The plugin sends one entry per piece:

```json
{ "prefab": "assets/prefabs/building core/foundation/foundation.prefab",
  "pos": [1.5, 0, -1.5], "rot": [0, 90, 0], "grade": 2, "skin": 0 }
```

* Building blocks resolve to a per-grade model —
  `Building Core/foundation/foundation.stone.glb` for grade 2.
  Grades are `0 twig, 1 wood, 2 stone, 3 metal, 4 toptier`.
* Deployables just swap `.prefab` for `.glb` —
  `Deployable/Furnace/furnace.glb`.
* Anything that can't be matched is skipped and logged, so one unknown prefab never
  fails the whole render.

## Notes

* Rendering runs in headless Chrome with SwiftShader (software WebGL), so it works on a
  plain VPS with no graphics card. A base takes a few seconds.
* PNGs are written to `renders/<CODE>.png` and served from `/renders/<CODE>.png`.
* Images are transparent, so they sit on the card's own background.
* If you'd rather not host images at all: copy `renders/*.png` into the game server's
  `oxide/data/RandomGridSpawnRenders/` and run `gridspawn.renders` — the plugin reads
  that folder straight off disk and needs no URL.
