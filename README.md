# DIYLevelFastInit

Startup and memory optimization for the [OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) custom-level plugin in *Overcooked! 2*. Requires BepInEx 5 and OC2DIYLevel 0.10.0.

[中文说明](README_CN.md)

## Version 1.6.2

Asynchronous loading removed the main-menu freeze, but keeping every info bundle and prebuilding four configurations for every level still consumed the limited address space of the 32-bit game.

This version separates the complete level catalog from gameplay resources:

1. Prepare `common*`, then scan info bundles asynchronously **one at a time**. Copy names, UIDs, scene names, dependency names and player-count settings into lightweight SOs, then close each source bundle with `Unload(false)`. Clear temporary full-info references and wait for unused-asset collection in the frontend before scanning the next bundle.
2. Cache only scalar metadata in a versioned binary format with bounded lengths and structural validation under `BepInEx/cache/DIYLevelFastInit/`. Validate the directory, info filename, file length and modification time on reuse. Invalid or unwritable caches fall back to scanning. Delete the cache while the game is closed to force a rebuild.
3. Build a lightweight scene directory for menus and saves, retaining the complete level list and positional mapping. Opening the save dialog no longer builds recipe configurations for the entire collection.
4. Load the selected info bundle and build four player-count configurations for **the selected level** before the host or client uses it. Replays can reuse directory configurations.
   Read scene and info metadata to follow transitive references to other sets' info bundles. Dependency-only bundles open their containers without loading their `LevelSetInfo` or building configurations; shared dependencies remain until their final user exits. This fixes missing cross-set material references while retaining on-demand loading. The reader is built into the DLL and needs no Python or extra plugin.
5. Release unused info bundles, generated configurations and prefab clones after their scenes are no longer in use. Detach full-info references and use `Unload(false)` for info bundles, then coalesce `Resources.UnloadUnusedAssets()` requests in the frontend so assets still referenced by materials or scenes survive. Do not start this cleanup during DIY gameplay or transitions. Temporary overlap is allowed during transitions. Scene-instance objects created by a replay are collected independently of reusable directory configurations.
6. Adapt OC2StageThumbnailPreview 1.0.0 to load the focused level set's screenshots on demand. Only one preview set is retained. The sorting plugin continues to receive stable lightweight SOs.
7. Retain incremental hot reload. Refresh is blocked during a level or transition; level selection is blocked during a refresh.
8. If an async request takes over 15 seconds, log the delay and keep waiting for that same request. Only a completed failed request may fall back to synchronous loading; Unity cannot cancel an unfinished request safely.

Vanilla shared dependencies keep their original reference-counted lifetime. The plugin does not modify the executable, level files, original mod DLL or save format.

## Limits

The resource loading unit is still **one info bundle plus transitive info dependencies of the selected scene/set**, not one individual level. A very large set may still require substantial memory. Finer granularity requires changes to level export and bundle layout. Common bundles and resources retained by other mods remain outside this plugin's cleanup scope.

The first catalog scan, first preview of a set, and first level entry still have I/O, decompression and configuration costs. Selected-set preparation uses a synchronous path and can briefly stall for a large bundle. Wait for initial catalog discovery before relying on the full list or joining a custom game.

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
- `on-demand config ready/released` and `scene runtime objects released` for generated object lifetimes.
- `scene material audit` and `scene material invalid` for a read-only check of shader references and support after DIY scene entry. The audit does not replace materials.

Exercise browsing, sorting, previews, save selection/read/write, entry, repeated restarts, transitions between sets, return to the menu, hot reload, and host/client multiplayer. Record process private memory across cycles; transitions may have temporary peaks. Compilation and hook signature checks do not replace gameplay testing.

## Disclaimer

Unofficial community patch, not affiliated with Team17 or gua248. Developed through interoperability analysis of OC2DIYLevel; contains none of its original source code.

The built-in LZMA decoder uses a public-domain LZMA SDK subset; see [third-party notices](THIRD_PARTY_NOTICES.md).
