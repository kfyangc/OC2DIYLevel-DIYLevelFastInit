# AGENTS.md

BepInEx 5 plugin (net35, single file `FastInitPlugin.cs`) that patches
[OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) 0.10.0 for Overcooked! 2.
Version 1.6.7 keeps a complete lightweight level catalog while loading info resources
and building configurations only for the selected level. Async discovery, menu healing,
the progress indicator, and incremental hot reload remain supported.

## Build / deploy / verify

```powershell
dotnet build -c Release                      # uses default GameDir from csproj
dotnet build -c Release -p:GameDir="C:\Path\To\Overcooked! 2"
Copy-Item -LiteralPath bin/Release/DIYLevelFastInit.dll -Destination "<GameDir>/BepInEx/plugins/DIYLevelFastInit.dll" -Force
```

- Default game directory: `D:\Games\Steam Games\steamapps\common\Overcooked! 2`.
  Output: `bin/Release/DIYLevelFastInit.dll`. Keep the csproj version and `PluginVersion`
  in sync. Rebuild only this hook; `../OC2DIYLevel_src/` is reference source.
- Back up the installed DLL outside the plugins scan tree before deployment. The game
  locks the loaded DLL: coordinate shutdown with the user, copy only after it exits,
  compare source/destination SHA-256 hashes, then tell the user it is ready to restart.
  A failed copy must never be reported as an installed fix.
- No committed automated test suite. Verify in the game and inspect
  `BepInEx/LogOutput.log` (also
  `BepInEx/plugins/OC2DIYLevel/logs/logs_info_*/BepInExDebugLog.log`). Native Unity messages
  may appear only in `%USERPROFILE%/AppData/LocalLow/Team17/Overcooked2/output_log.txt`.
- Useful FastInit log lines:
  - `common bundles: ...`, `level set [...] ready (... metadata only)`,
    `FastInit complete: ...`, `level set menu rebuilt: ...`.
  - `catalog cache hit: ...`, `lightweight scene directory: ... no recipe configs`.
  - `on-demand info loaded/released` (resident bundle count),
    `on-demand config ready/released`, `scene runtime objects released`.
- Check cold discovery, warm-cache startup, browsing/sorting/previews, save selection,
  level entry, repeated restarts, switching sets, return to the frontend, hot reload,
  and host/client multiplayer. Keep completed checks distinct from untested paths.
  Build or Harmony signature checks alone do not verify Unity resource lifetimes.
- Useful isolated checks: metadata round trips with Unicode and all four player counts;
  rejecting stale fingerprints, invalid versions, truncated files, oversized lengths,
  and trailing data; resolving API references against the installed runtime; generating
  Harmony wrappers from the installed target signatures. Unity native method bodies
  cannot be exercised in an ordinary standalone CLR process.
- Verify the UnityFS metadata reader against an independent parser on real bundles.
  In the installed `young_v0.3/s_zhuzi_2`, floor material pointers reference the CAB
  inside `test_level/info_test_level`; `info_young` alone does not cover scene dependencies.
  Keep this cross-set regression distinct from unrelated rendering reports.
- For memory measurements, record process private bytes and virtual address space as
  well as working set, together with the scene and transition state. A small working
  set alone does not establish low 32-bit address-space usage. Cache file size is not
  a measurement of runtime memory savings.
- The machine has no default NuGet source; `NuGet.config` pins nuget.org. Don't remove it.

## Architecture

- Harmony prefix on `DIYLevelAssetBundleManager.Initialize` skips the original:
  `RunCheapInit` synchronously prepares `common`, static fields and DLC data, while
  `FastInitHost` loads the remaining `common*` dependencies in the background.
  `IsInitialized` (= `commonBundle != null`) must become true synchronously because the
  original consumer patches check it. Initialization is idempotent.
- Discovery consumes info bundles one at a time in directory order. `LoadInfoSet` first
  tries `LevelCatalogCache`; on a miss it asynchronously loads `LevelSetInfo`, extracts
  scalar metadata into independent lightweight SOs, clears temporary full-info references,
  closes the source with `Unload(false)`, and waits for safe unused-asset collection. Discovery pauses during DIY gameplay and transitions. Do not restore
  unbounded parallel prefetch or full-info retention.
- `SetMetadata`, `LevelMetadata`, and `PlayerMetadata` contain only strings, numbers,
  and arrays. `LevelCatalogCache` writes versioned binary `.catalog` files under
  `Paths.CachePath/DIYLevelFastInit/`, named by a SHA-256 hash of the normalized directory
  path. The current schema is 2, with magic `0x32434944`. Validate the directory, info
  filename, length and UTC modification ticks; enforce bounded lengths and reject
  trailing data. Writes use a temporary file and read it back before replacement.
  Read/write failures fall back to discovery, so the cache remains disposable.
- Harmony **postfix** on `DIYLevelEntryUI.AddUI`: re-syncs the menu button list
  (`HealLevelSetButtons`), because the original builds it only once (snapshot guard).
- The prefix on `DIYLevelAssetBundleManager.SetupSceneDirectoryData` returns
  `SceneDirectoryCache.GetDirectory()`: all levels, four variants, scene names and star
  boundaries, initially without heavy `LevelConfig` objects. The cached list snapshot
  is order-sensitive. Retired directories are collected at the next blanket clear.
  The 1.5.x full-directory warm build and blanket-clear suppression have been removed.
- `OnDemandResources.ServerLoadPrefix` queues single-player/host preparation and skips
  the immediate `LoadLevelServer` body. Yield before native work, drain active preview
  and warmup requests, pin `_preparingBundles` across async container/info loads, then
  build one player-count configuration per frame. Only set `_directoryBuild` around
  each synchronous factory call, never across a yield. Replay `LoadLevelServer` once
  through a narrowly scoped `_resumeServerLoad` guard after everything is ready.
  Ignore duplicate server requests while `_serverPreparing`; finally paths release
  preparation pins/progress and clear the pending scene on failure.
- `BoostEntryPriority` saves `Application.backgroundLoadingPriority` and temporarily
  sets `High` after active preview/discovery native requests drain. Its scope covers
  server resource preparation through target scene arrival; restore on target/frontend
  `sceneLoaded`, failed/disposed preparation and plugin `OnDestroy`. Restore only while
  the current value is still `High`, preserving a different value set by another loader.
  Do not boost ordinary thumbnail warmup, browsing or client callbacks. Keep the scope
  separate from `_serverPreparing`, which ends at the original scene-load handoff.
  Bundle and info-asset phase timers plus handoff-to-scene timing support comparisons;
  priority changes do not guarantee instant entry and may reduce loading-time frame rate.
- `LoadPrefix` / `PrepareLevel` remain synchronous for `LoadLevelClientPatch`.
  OC2DIYLevel calls it from a prefix on `NetworkUtils.LevelLoadByName`; returning early
  would let the original network handler consume incomplete session/level settings.
  Both paths share session-directory validation and individual variant construction.
  Do not generalize the server's asynchronous deferral to the client void method.
  One expensive configuration and native scene activation can still cause a frame spike.
- Public `levelSetInfos` entries remain lightweight SOs with stable identities; full
  `LoadedSet.Info` objects stay private. The directory remains complete for save IDs,
  original dependency lookup, sorting and arcade selection. Never replace it with a
  selected-level-only list or populate its entries with full recipe/audio graphs.
- Resource ownership is split between reusable directory configurations (`Levels`,
  keyed by scene name) and objects created by each loaded scene instance (`SceneObjects`,
  keyed by `Scene`). SetupConfig, custom recipe/lookup factories and runtime prefab
  cloning are tracked. Scene-instance objects are destroyed on `sceneUnloaded`, including
  on same-level restarts. `isQuit:true` clears generated objects and directory references
  before allowing the original prefab clear to run; `isQuit:false` retains its original
  behavior. Track only objects created by these factories: SetupConfig clones the first
  round's recipe list, while other rounds may still reference shared assets.
- Info bundles stay loaded while a related scene is loaded, is the pending target, or
  is the current uncached preview set. `ReleaseUnused` clears catalog screenshot references and
  closes unneeded info bundles with `Unload(false)` after clearing full-info references.
  Do not force-destroy info assets with `Unload(true)`: other materials or scenes can
  still reference them. New and old resources may overlap during transitions.
  The optional `OC2StageThumbnailPreview.Patches.PreviewController.UpdatePreview(Image,
  LevelInfoSO)` adapter is installed in `Start`. Cached previews use private copied
  sprites; an uncached preview temporarily retains at most one source set.
- The thumbnail prefix must only queue a request and return; never call `Resolve`,
  synchronous `LoadSet`, `LoadFromFile`, or `LoadAsset` from the menu callback.
  `PreviewRoutine` coalesces focus changes into the latest image/level request and
  starts work after yielding a frame. `LoadPreview` uses async bundle and info requests,
  pins `_previewBundles` until every native request finishes, and only applies an image
  if its request is still current and the menu hierarchy is active. Stale requests are
  drained and released rather than cancelled or allowed to overwrite a newer image.
  Drain queued unused-asset collection before starting the next preview, so sustained
  browsing cannot accumulate detached asset graphs behind an always-busy worker.
  `PendingInfos` lets actual level entry join the same native info request. Refresh
  cancels the preview selection and waits for its worker to drain before mutating sets.
- With the optional thumbnail adapter installed, `FinishLoading` queues sequential
  thumbnail prewarming over the complete catalog, without requiring menu visits.
  Pin `_warmBundles` while native requests run; keep full `LevelSetInfo` only in the
  current worker, write one copied image per frame, drain the bounded disk writer,
  release source references and wait for unused-asset collection before the next set.
  Pause for gameplay, transitions and uncached foreground previews. Refresh invalidates
  the warmup generation and waits for native requests to drain before changing entries.
  Cached foreground images bypass the warmup wait and cleanup queue.
  Preview waits for warmup, cleanup and discovery must check `PreviewIsCurrent` each
  frame. A new pending scene cancels that request; waiting unconditionally for cleanup
  would deadlock server preparation waiting for the preview worker to drain, because
  cleanup is deferred during transitions.
- `ThumbnailCache` persists versioned `.thumb` files containing a source stamp, bounded
  dimensions, a SHA-256 checksum and PNG data; empty payloads record absent screenshots.
  It fingerprints all info/common source files by path, length and UTC modification time.
  Invalidate the stamp on catalog completion/refresh; source changes conservatively
  invalidate all thumbnails. Header-only readiness checks avoid decoding during warmup;
  actual reads verify the full payload and fall back to async loading on corruption.
  GPU copies handle non-readable source textures and shrink to at most 640 x 360.
  Keep copied sprites private to the preview adapter: never assign them to public
  catalog SOs or scene-directory screenshots, which may outlive LRU eviction.
  Keep at most eight decoded images; clear them at level entry. The single disk writer
  has a four-item queue, atomic replacement and a 128 MiB oldest-entry eviction limit.
  Warmup observes writer backpressure. Workers never call Unity APIs off the main thread.
- `InfoBundleGraph` indexes serialized CAB names from info bundle directory tables,
  then reads only serialized metadata prefixes to find external CAB references in the
  selected scene and info files. Follow the transitive closure, including cycles.
  Do not assume a scene uses only its own set's info bundle, or rely solely on the
  info bundle's own dependency names: scene-only references can point to other sets.
  Sources are fingerprinted by path, length and UTC modification ticks; cached graph
  entries contain strings and block tables, never Unity assets or decompressed payloads.
- `InfoBundles` owns bundle containers; `LoadedSet.Required` pins each active/preview
  set's transitive dependencies. Dependency-only bundles do not load `LevelSetInfo`.
  `_catalogBundles` pins cold-discovery dependencies until extraction finishes.
  Shared dependencies are released only after every set and discovery pin is gone.
  `SceneDependencies` tracks scene-specific closures separately from the info closure;
  discard closures for departed scenes even when another scene in the same set remains.
  `PendingBundles` lets synchronous entry join the same native async request; never
  start a duplicate file load while that request is pending. Defer menu selection and
  thumbnail loading during a cold info request.
- `QichiResources` prepares `qichi_assets` before any `info_qichi` container is consumed,
  including cold discovery, source previews and both entry paths. Resolve the optional
  Qichi runtime v10 APIs by reflection, join its existing native request or reuse the
  resident bundle, then synchronize its request/loading/loaded fields and shared loader
  reference. Do not start a duplicate while a request is pending. Qichi owns this bundle;
  never add it to FastInit's info cleanup. File reading and decoding remain in Qichi's
  runtime. The adapter covers the known info filename, not arbitrary external packages.
  Verify a fresh-process `s_qichi_1_5` entry and its dungeon meshes/materials separately
  from the cross-set `s_zhuzi_2` info dependency regression.
- The reader targets UnityFS v6 / serialized-file versions 9–21 used by this Unity
  2017 game, with bounded uncompressed, LZ4 and LZMA prefix decoding. LZMA decoder
  code is an adapted public-domain SDK subset in the same source file; retain
  `THIRD_PARTY_NOTICES.md` and its attribution if changing it.
- Cleanup requests are coalesced by `RequestCleanup` / `CleanupRoutine`. After yielding
  a frame, wait until neither a transition nor DIY gameplay is active, then let
  `Resources.UnloadUnusedAssets()` reclaim unreferenced assets. Cold discovery waits
  for this cleanup before consuming the next source bundle. Also defer collection while
  a preview is loading and for 0.5 seconds after a focus change. Tracked bundle counts do
  not count detached assets awaiting collection.
- `AuditSceneMaterials` checks active renderers two frames after DIY scene entry. It
  logs material/shader names and support status, warning on empty mesh material slots,
  missing, unsupported, or
  internal-error shaders. This is diagnostic only; do not replace shaders or materials
  as a fallback without confirming the correct intended asset.
  Empty particle-renderer material slots can be intentional; do not report them as
  broken floor materials.
- Hot sync diffs `SetSnapshot`s, updates changed entries and restores directory order.
  It is blocked during DIY gameplay or transitions; `Syncing` blocks level selection
  during refresh. Sweep only DIY scene bundles through the original bundle manager.
  Do not force-unload vanilla dependencies or `common*`; their lifetime belongs to the
  original reference counts or other loaders.
- `FastInitHost.Guard` drives nested iterators and disposes them on failure, preserving
  cleanup/finally paths. Unity async requests cannot be cancelled: after 15 seconds,
  log and keep waiting for the same request. Only a completed failed request may fall
  back to synchronous loading; never race an unfinished request with a second load.
- Private statics of the target plugin are set via reflection (`AccessTools.Field`):
  `commonBundle`, `diyLevelCover`, `diyLevelGameSessionPrefab`, `configTemplateSO`,
  `Instance`, `levelSetSelectionMenu`, and method `AddLevelSetButton`. These names are the
  contract with the compiled OC2DIYLevel 0.10.0 — if that plugin updates, re-verify against
  its decompiled source at `C:\Users\yang\.cache\OC2DIYLevel-decompiled\OC2DIYLevel_src\`.

## Constraints

- Target framework net35 / C# 7.3: no ValueTuples, no `yield` inside try/catch.
- `levelSetInfos` order must remain directory order — the arcade mod maps level-set
  selection by list position.
- Plugin dir must be resolved from `typeof(DIYLevelAssetBundleManager).Assembly.Location`,
  never `GetExecutingAssembly()` (would point at this plugin's own directory).
- Keep `common*` bundles loaded before consuming `info_*` bundles (dependency resolution).
- The smallest heavy-resource loading unit is one info bundle, plus any transitive
  info dependencies of the selected scene/set, not one individual map. Common bundles
  and other mods' retained assets remain outside this
  plugin's cleanup scope. Preserve this distinction in memory claims.

## Code style

- Comments in English, minimal: one class-level `<summary>` plus single-line `//` notes
  only where the code can't speak (see current file).
- Chinese only in functional UI/localization strings. If any non-ASCII source is added,
  keep the UTF-8 BOM (csc otherwise reads the file as the system codepage).

## Build gotchas

- The `UnityEngine.dll` facade alone is not enough: module references
  (CoreModule, AssetBundleModule, ImageConversionModule, IMGUIModule, TextRenderingModule,
  UnityEngine.UI) are required. ImageConversionModule provides PNG encode/decode.
- The game has a global-namespace `UIUtils` that shadows `OC2DIYLevel.UIUtils` — fully
  qualify the latter.
- `[BepInDependency]` with the two-argument enum fails to resolve; use the single-argument
  form (defaults to hard dependency).
- Prefer `IOException` to `InvalidDataException`: the latter compiled successfully but
  produced `TypeLoadException` in the installed game's Mono runtime.
- Do not use `JsonUtility` for nested catalog metadata: the installed Unity serializer
  omitted the level arrays, including after making their types public. Keep the binary
  format and round-trip validation; no JSONSerializeModule reference is needed here.
- This game's `Scene` API has no public `handle` property. Use the `Scene` struct as
  the scene-instance dictionary key.

## Publishing rule

Commit each version independently after implementation and relevant checks, before
starting the next version. Include the version number in the commit message and keep
`PluginVersion` and the csproj version synchronized, preserving development history.

Creating the GitHub repo, pushing, or cutting releases requires the owner's explicit
review and approval first — never push on your own initiative.
