# DIYLevelFastInit

Startup and memory optimization for the [OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) custom-level plugin in *Overcooked! 2*. Requires BepInEx 5 and OC2DIYLevel 0.10.0.

[中文说明](README_CN.md)

## Version 1.6.8

Asynchronous loading removed the main-menu freeze, but keeping every info bundle and prebuilding four configurations for every level still consumed the limited address space of the 32-bit game.

This version separates the complete level catalog from gameplay resources:

1. Prepare `common*`, then scan info bundles asynchronously **one at a time**. Copy names, UIDs, scene names, dependency names and player-count settings into lightweight SOs, then close each source bundle with `Unload(false)`. Clear temporary full-info references and wait for unused-asset collection in the frontend before scanning the next bundle.
2. Cache only scalar metadata in a versioned binary format with bounded lengths and structural validation under `BepInEx/cache/DIYLevelFastInit/`. Validate the directory, info filename, file length and modification time on reuse. Invalid or unwritable caches fall back to scanning. Delete the cache while the game is closed to force a rebuild.
3. Build a lightweight scene directory for menus and saves, retaining the complete level list and positional mapping. Opening the save dialog no longer builds recipe configurations for the entire collection.
4. For single-player and host entry, yield after confirming a save slot, load the selected info resources asynchronously, then prepare **the selected level's** four player-count configurations over separate frames. Show preparation progress and resume the original server load once ready; duplicate entry requests are ignored while preparation is pending. Replays can reuse directory configurations. For remote clients, defer the reliable `LevelLoadByName` message at the client mailbox before any level callback runs. Copy its scene, game states, loading-screen flag and dispatch envelope because the registry reuses payload objects. Load resources asynchronously, prepare one configuration per frame, show client progress, then dispatch that copy once. Other network traffic keeps flowing. The original client entry hook stays synchronous and consumes the ready resources during replay. Coalesce duplicate requests; invalidate old requests on target changes, other scene loads, disconnection and reconnection, draining native requests before release.
   Read scene and info metadata to follow transitive references to other sets' info bundles. Dependency-only bundles open their containers without loading their `LevelSetInfo` or building configurations; shared dependencies remain until their final user exits. This fixes missing cross-set material references while retaining on-demand loading. The reader is built into the DLL and needs no Python or extra plugin.
   For `info_qichi`, first prepare the separate `qichi_assets` dependency through the installed Qichi runtime (v10 API). Join its native request or reuse a resident bundle, and synchronize the runtime's loaded state to prevent repeated duplicate loads. This applies to cold discovery, source previews and both entry paths. Qichi retains ownership of this bundle; FastInit does not unload it. A missing Qichi runtime is reported, while an incompatible installed loader or failed request aborts that preparation.
   After active thumbnail/discovery requests drain, temporarily raise Unity's background loading priority to `High` through resource preparation and the target scene load. Restore the previous value on target/frontend scene arrival, preparation failure or plugin shutdown; preserve a different value set by another loader. This favors shorter entry waits at the cost of frame rate during loading. Log bundle loading and info-asset instantiation separately. Resource retention and disk-cache formats are unchanged.
5. Release unused info bundles, generated configurations and prefab clones after their scenes are no longer in use. Detach full-info references and use `Unload(false)` for info bundles, then coalesce `Resources.UnloadUnusedAssets()` requests in the frontend so assets still referenced by materials or scenes survive. Do not start this cleanup during DIY gameplay or transitions. Temporary overlap is allowed during transitions. Scene-instance objects created by a replay are collected independently of reusable directory configurations.
6. Adapt OC2StageThumbnailPreview 1.0.0 with persistent thumbnail caching. After catalog discovery, automatically generate thumbnails **before browsing**, one set at a time, and release each source graph before advancing. A progress badge shows completed disk entries. Generate images up to 640 × 360, write them asynchronously under `BepInEx/cache/DIYLevelFastInit/thumbnails/`, and read the small images directly when browsing. Keep at most eight decoded thumbnails in memory, clearing them on level entry; disk storage is capped at 128 MiB with oldest-entry eviction. Fingerprint source info/common files and validate image dimensions, payload bounds and checksums. Prewarming pauses for gameplay, transitions and uncached preview requests. Cache hits bypass in-flight prewarming; missing or invalid entries fall back to asynchronous source loading. Coalesce rapid focus changes and apply only the current image. The sorting plugin continues to receive stable lightweight SOs.
7. Guard DIY save selection against same-frame duplicate clicks, reopening an active dialog or reopening during entry, and repeated save-ready callbacks. Cancellation still allows a fresh selection. Retain incremental hot reload. Refresh is blocked during a level or transition; level selection is blocked during a refresh.
8. If an async request takes over 15 seconds, log the delay and keep waiting for that same request. Only a completed failed request may fall back to synchronous loading; Unity cannot cancel an unfinished request safely.

Vanilla shared dependencies keep their original reference-counted lifetime. The plugin does not modify the executable, level files, original mod DLL or save format.

## Limits

The resource loading unit is still **one info bundle plus transitive info dependencies of the selected scene/set**, not one individual level. A very large set may still require substantial memory. Finer granularity requires changes to level export and bundle layout. Common bundles and resources retained by other mods remain outside this plugin's cleanup scope.

The first catalog scan, thumbnail prewarming, and first level entry still have I/O, decompression and configuration costs. Prewarming loads full source info graphs temporarily, with one image capture per frame and unused-asset collection between sets. Until a thumbnail is cached, its preview may take time to appear. With the optional thumbnail plugin absent, thumbnail prewarming is skipped. Single-player/host and normal remote-client preparation await native loads, but each individual configuration is still generated on the main thread; an expensive single configuration or Unity scene activation can still cause a frame spike. The client gate covers the vanilla reliable `LevelLoadByName` path; other mods calling the original client preparation method directly retain its synchronous fallback. Wait for initial catalog discovery before relying on the full list or joining a custom game.

The Qichi adapter delegates file reading and decoding to that runtime; its request startup can still block a frame. It covers the known `info_qichi` layout and does not discover arbitrary external packages belonging to other mods. Cold-start Qichi background rendering still needs gameplay verification.

## Install or roll back

Close the game, back up `BepInEx/plugins/DIYLevelFastInit.dll`, and replace it with the new DLL. To roll back, close the game and restore the backup. The metadata cache can be retained or deleted. Keep backup DLLs outside the plugins scan tree.

## Build

```powershell
dotnet build -c Release -p:GameDir="D:\Games\Steam Games\steamapps\common\Overcooked! 2"
```

Output: `bin/Release/DIYLevelFastInit.dll`. Targets .NET Framework 3.5 and C# 7.3. Keep the project's `NuGet.config`.

## Verification

Relevant lines in `BepInEx/LogOutput.log`:

- `level set [...] ready (... metadata only)` and `catalog cache hit` for discovery and disk cache reuse.
- `lightweight scene directory: ... no recipe configs` for save-dialog behavior.
- `on-demand info loaded/released` for tracked info bundle counts; detached assets may remain until unused-asset collection.
- `info dependency graph` and `info dependency released` for selected-scene dependencies and shared bundle lifetimes.
- `Qichi dependency loading` / `Qichi dependency ready` for the separate runtime-owned background bundle. On a fresh process, readiness must precede `on-demand info loaded` and the Qichi scene load; the runtime should not repeatedly report a duplicate `Memory` bundle.
- `on-demand preview info loaded` and `thumbnail ready (... async ... ms elapsed)` for completed previews; elapsed time includes asynchronous waiting and does not measure main-thread stalls.
- `thumbnail prewarm complete: ... disk entries ready` for automatic disk generation; `thumbnail cache hit: ... (disk|memory, ... ms)` for browsing without a source info load.
- `on-demand config ready/released` and `scene runtime objects released` for generated object lifetimes.
- `async level preparation started`, `async level resources ready`, and `on-demand config ready (... async level entry ...)` for single-player/host preparation. Elapsed times include asynchronous waiting; individual configuration timings of at least 50 ms are also logged at Info level.
- `DIY client level message deferred/resumed`, `async client level preparation started`, `client level bundle ready` and `client level info asset ready` for client preparation and one replay; `DIY client level preparation cancelled` for stale requests.
- `duplicate DIY ... ignored` for selection/dialog/save-confirmation reentry guards.
- `level loading priority` / `level loading priority released` for the temporary priority scope; `level bundle ready` and `level info asset ready` for phase timing. Release logs include elapsed time since the original scene-load handoff when available.
- `scene material audit` and `scene material invalid` for a read-only check of shader references and support after DIY scene entry. The audit does not replace materials.

Exercise browsing, sorting, previews, save selection/read/write, entry, repeated restarts, transitions between sets, return to the menu, hot reload, and host/client multiplayer. Record process private memory across cycles; transitions may have temporary peaks. Compilation and hook signature checks do not replace gameplay testing.

Version 1.6.8 has offline checks for deferred-message snapshots, deduplication, one replay, cancellation, target replacement, reconnection and callback failure. Multiplayer DMM/Qichi/test entry and save-dialog cancellation/reselection still require gameplay regression; these checks do not establish that every network error is fixed.

## Disclaimer

Unofficial community patch, not affiliated with Team17 or gua248. Developed through interoperability analysis of OC2DIYLevel; contains none of its original source code.

The built-in LZMA decoder uses a public-domain LZMA SDK subset; see [third-party notices](THIRD_PARTY_NOTICES.md).
