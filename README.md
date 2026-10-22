# DIYLevelFastInit

Startup and memory optimization for the [OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) custom-level plugin in *Overcooked! 2*. Requires BepInEx 5 and OC2DIYLevel 0.10.0.

[中文说明](README_CN.md)

## Version 1.6.16

Fixes generated LZ4 caches being rejected and subsequent entries using the original file. Versions 1.6.13–1.6.15 inherited the source LZMA block's `Streamed=0x40` flag for independent LZ4/raw blocks. The game's native reader then invokes its unsupported streamed LZ4 callbacks, reports `Failed to decompress data for the AssetBundle`, and persists a rejection marker. New blocks contain only compression flags `0x02`/`0x00`; compressed payloads and resource bytes are preserved.

Cache keys now include format version 2, isolating old bundles and rejection markers automatically. After upgrading, allow the frontend to idle until `bundle file cache built`, then confirm entry logs report `info bundle source: info_dmm (LZ4 cache)`. Startup identifies `format 2`, and persistent rejection now logs its reason explicitly.

The observed 1.6.15 entry used the original file: 7,597 ms bundle loading, 78 ms selected info, 7,926 ms preparation, then 3,566 ms from scene handoff to arrival. That run did not exercise LZ4 and cannot measure its benefit. Evidence is under `../.verification/perf-1.6.15/`.

## Version 1.6.15

Fixes first-time LZ4 cache creation on the game's bundled Mono runtime. Missing files/paths can throw `IsolatedStorageException`, which is not an `IOException`; checking an absent `.bad` or `.meta` previously aborted the job and left only `.lock`. The source reader also used a `FileOptions` constructor overload missing from this runtime; it now uses the supported five-argument overload. Missing files now produce a normal cache miss, and compatibility handling also covers files disappearing after the existence check. Worker errors retain their type/stack, and startup logs show the enabled cache directory.

Completed entries live under `BepInEx/cache/DIYLevelFastInit/bundles/` as hash-named `.bundle`/`.meta` pairs, not `.lz4` files. Only supported `info_dmm` files are currently converted. Leave `Loading.EnableLz4BundleCache=true` and allow the frontend to idle after discovery/source-thumbnail work; entry/gameplay pauses conversion. `bundle file cache built` confirms publication, and `info bundle source: info_dmm (LZ4 cache)` confirms use. Existing `.lock` files can remain.

## Version 1.6.14

Entry, uncached previews and thumbnail prewarming now request the selected `LevelInfoSO` asset directly. The complete lightweight catalog and its stable SO identities remain unchanged. Each info container indexes asset basenames once per loaded set; only a unique match for the catalog object's name is used, and the loaded object's type, name and scene must agree. Missing, ambiguous or invalid addresses fall back to the original full `LevelSetInfo`. Same-target requests are shared; different levels have separate pending requests. Cancellation drains native requests before dropping their pins and cannot start an obsolete fallback.

For direct-address bundles, prewarming releases each level's source references and waits for safe cleanup before advancing. Legacy full-set fallback is retained for the rest of that warmup batch to avoid repeatedly loading the same graph. Switching within a set drops unused per-level references while retaining dependencies needed by active/pending scenes and the exact uncached preview. `Loading.EnableIndividualLevelAssets=false` plus restart restores full-set loading. Cold catalog discovery still loads `LevelSetInfo` when its scalar cache misses. The LZ4 cache below remains enabled independently.

## Version 1.6.13

Adds a disposable LZ4 file cache for the installed `info_dmm` layout. A miss immediately loads the original file and queues conversion; one low-priority managed worker runs only while foreground loading and gameplay are idle. The original map is never overwritten. Cache entries are verified against source/output SHA-256 hashes once per process in the background, published with validated metadata, and limited to 512 MiB of published data. Unity bundle requests and resident containers pin their cache files until completion/unload. Rejected copies fall back to the original source and remain disabled for that source fingerprint.

Cache location: `BepInEx/cache/DIYLevelFastInit/bundles/`. Set `Loading.EnableLz4BundleCache=false` in the plugin configuration and restart to disable it, or delete the cache while the game is closed. Only supported single-block LZMA `info_dmm` files are converted; small scene bundles and other packages retain their original loaders. Conversion uses bounded streaming buffers and preserves directory nodes, CAB names and uncompressed resource bytes. The first use can still load from the original file while conversion/validation is pending. A paused conversion retains a read handle on its source until it resumes or shuts down; Windows may prevent overwriting that source during this period. Version 1.6.13 addresses file decompression; version 1.6.14 adds the object-loading change above.

The cancellation/save-handshake fixes from 1.6.12 remain in place:

This revision fixes load-request ownership across cancellation and reconnection, following Team17's per-connection loading and readiness flow:

- A cancelled client preparation immediately stops filtering save-ready messages, even while its native resource request is still draining. Successful handoff retains the filter only for that exact mailbox, peer and connection until the target scene arrives.
- Server preparation checks its request generation and network context after waits and before handoff. Returning to the frontend, resetting the connection or changing roles invalidates the old request; it cannot resume later as an offline load.
- Switching from a confirmed DIY save choice to an official DLC save request clears the old confirmation and cancels obsolete entry permissions before the host waits for save-ready. The official countdown and a later DIY selection can proceed while cancelled resource work drains.

The source layout introduced in 1.6.11 is retained. Resource requests still drain before their pins are released, and the original kitchen-ready handshake remains responsible for starting play.

Asynchronous loading removed the main-menu freeze, but keeping every info bundle and prebuilding four configurations for every level still consumed the limited address space of the 32-bit game.

This version separates the complete level catalog from gameplay resources:

1. Prepare `common*`, then scan info bundles asynchronously **one at a time**. Copy names, UIDs, scene names, dependency names and player-count settings into lightweight SOs, then close each source bundle with `Unload(false)`. Clear temporary full-info references and wait for unused-asset collection in the frontend before scanning the next bundle.
2. Cache only scalar metadata in a versioned binary format with bounded lengths and structural validation under `BepInEx/cache/DIYLevelFastInit/`. Validate the directory, info filename, file length and modification time on reuse. Invalid or unwritable caches fall back to scanning. Delete the cache while the game is closed to force a rebuild.
3. Build a lightweight scene directory for menus and saves, retaining the complete level list and positional mapping. Opening the save dialog no longer builds recipe configurations for the entire collection.
4. For single-player and host entry, yield after confirming a save slot, load the selected info resources asynchronously, then prepare **the selected level's** four player-count configurations over separate frames. Show preparation progress and resume the original server load once ready; duplicate entry requests are ignored while preparation is pending. Replays can reuse directory configurations. For remote clients, defer the reliable `LevelLoadByName` message at the client mailbox before any level callback runs. Copy its scene, game states, loading-screen flag and dispatch envelope because the registry reuses payload objects. Load resources asynchronously, prepare one configuration per frame, show client progress, then dispatch that copy once. Other network traffic keeps flowing. The original client entry hook stays synchronous and consumes the ready resources during replay. Coalesce duplicate requests; invalidate old requests on target changes, other scene loads, disconnection and reconnection, draining native requests before release.
   Read scene and info metadata to follow transitive references to other sets' info bundles. Dependency-only bundles open their containers without loading their `LevelSetInfo` or building configurations; shared dependencies remain until their final user exits. This fixes missing cross-set material references while retaining on-demand loading. The reader is built into the DLL and needs no Python or extra plugin.
   For `info_qichi`, first prepare the separate `qichi_assets` dependency through the installed Qichi runtime (v10 API). Join its native request or reuse a resident bundle, and synchronize the runtime's loaded state to prevent repeated duplicate loads. This applies to cold discovery, source previews and both entry paths. Qichi retains ownership of this bundle; FastInit does not unload it. A missing Qichi runtime is reported, while an incompatible installed loader or failed request aborts that preparation.
   After active thumbnail/discovery requests drain, temporarily raise Unity's background loading priority to `High` through resource preparation and the target scene load. Restore the previous value on target/frontend scene arrival, preparation failure or plugin shutdown; preserve a different value set by another loader. This favors shorter entry waits at the cost of frame rate during loading. Log bundle loading and info-asset instantiation separately. The loading-state handshake is preserved.
5. Release unused info bundles, generated configurations and prefab clones after their scenes are no longer in use. Detach full-info references and use `Unload(false)` for info bundles, then coalesce `Resources.UnloadUnusedAssets()` requests in the frontend so assets still referenced by materials or scenes survive. Do not start this cleanup during DIY gameplay or transitions. Temporary overlap is allowed during transitions. Scene-instance objects created by a replay are collected independently of reusable directory configurations.
6. Adapt OC2StageThumbnailPreview 1.0.0 with persistent thumbnail caching. After catalog discovery, automatically generate thumbnails **before browsing**, one level at a time when direct addresses are available, and release source graphs before advancing. A progress badge shows completed disk entries. Generate images up to 640 × 360, write them asynchronously under `BepInEx/cache/DIYLevelFastInit/thumbnails/`, and read the small images directly when browsing. Keep at most eight decoded thumbnails in memory, clearing them on level entry; disk storage is capped at 128 MiB with oldest-entry eviction. Fingerprint source info/common files and validate image dimensions, payload bounds and checksums. Prewarming pauses for gameplay, transitions and uncached preview requests. Cache hits bypass in-flight prewarming; missing or invalid entries fall back to asynchronous source loading. Coalesce rapid focus changes and apply only the current image. The sorting plugin continues to receive stable lightweight SOs.
7. Guard DIY save selection against duplicate clicks, reopening an active dialog or reopening during entry, and repeated save-ready callbacks. Keep a confirmed choice for that frontend, stop its client save countdown, and reuse the choice with a real save-ready reply if the host repeats its request; never send the old save state during entry. Single-player/host and client entry both close the save/wait UI before loading resources, yielding while existing save work prevents normal cleanup. Avoid restoring the dialog's invoker during entry. Remote clients acknowledge **starting** the load before preparing resources and suppress the duplicate start acknowledgement on replay. Kitchen-ready acknowledgement remains on the original scene path. New selections, connection reset and scene arrival clear the retained choice. Retain incremental hot reload. Refresh is blocked during a level or transition; level selection is blocked during a refresh.
8. If an async request takes over 15 seconds, log the delay and keep waiting for that same request. Only a completed failed request may fall back to synchronous loading; Unity cannot cancel an unfinished request safely.

Vanilla shared dependencies keep their original reference-counted lifetime. The plugin does not modify the executable, level files, original mod DLL or save format.

## Limits

Bundle containers are still opened together with the transitive info dependencies of the selected scene/set. Inside an addressable container, only the selected level object graph is requested; this does not split the bundle file or remove assets referenced by that level. Original LZMA files can still require whole-block decompression, and unsupported direct addresses retain full-set fallback. Common bundles and resources retained by other mods remain outside this plugin's cleanup scope.

The first catalog scan, thumbnail prewarming, and first level entry still have I/O, decompression and configuration costs. Prewarming temporarily loads selected source graphs (or the compatible full-set fallback), with one image capture per frame and unused-asset collection between direct levels or fallback batches. Until a thumbnail is cached, its preview may take time to appear. With the optional thumbnail plugin absent, thumbnail prewarming is skipped. Single-player/host and normal remote-client preparation await native loads, but each individual configuration is still generated on the main thread; an expensive single configuration or Unity scene activation can still cause a frame spike. The client gate covers the vanilla reliable `LevelLoadByName` path; other mods calling the original client preparation method directly retain its synchronous fallback. Wait for initial catalog discovery before relying on the full list or joining a custom game.

The Qichi adapter delegates file reading and decoding to that runtime; its request startup can still block a frame. It covers the known `info_qichi` layout and does not discover arbitrary external packages belonging to other mods. Cold-start Qichi background rendering still needs gameplay verification.

## Install or roll back

Close the game, back up `BepInEx/plugins/DIYLevelFastInit.dll`, and replace it with the new DLL. To roll back, close the game and restore the backup. The metadata cache can be retained or deleted. Keep backup DLLs outside the plugins scan tree.

## Build

```powershell
dotnet build -c Release -p:GameDir="D:\Games\Steam Games\steamapps\common\Overcooked! 2"
```

Output: `bin/Release/DIYLevelFastInit.dll`. Targets .NET Framework 3.5 and C# 7.3. Keep the project's `NuGet.config`.

## Source layout

| Location | Responsibility |
| --- | --- |
| `FastInitPlugin.cs`, `Plugin/` | Plugin lifecycle, common initialization and progress display |
| `Catalog/` | Lightweight metadata, directory/cache handling, discovery and refresh |
| `Frontend/` | Menus, save selection and confirmed-choice reuse |
| `Networking/` | Copied client messages, connection validation and deferred entry |
| `Resources/` | Info bundles, configurations, entry, lifetime cleanup and dependency adapters |
| `Previews/` | Preview requests, thumbnail warmup and disk cache |
| `Diagnostics/`, `Infrastructure/` | Material auditing, coroutine host and async helpers |
| `BundleCodecs/` | LZMA decoder, streaming UnityFS repacker and LZ4 block encoder |

`FastInitPlugin` and `OnDemandResources` retain their type names through partial declarations. Shared fields and initializers remain in their core files in the original order. SDK default source inclusion compiles all files into the same plugin DLL.

## Verification

Version 1.6.16 passes the Release build, 157 cache-manager assertions, 39 old-Mono compatibility assertions and 25 format migration/rejection assertions. Thirteen byte-level checks against this machine's UnityPlayer confirm streamed routing and LZ4 stream callbacks that unconditionally fail. The actual DMM cache differs from the old output at exactly 1,231 flag bytes; every other file byte and the decompressed source hash are unchanged. Actual game Mono cold construction and reinitialized hash-validation/reuse pass, together with codec boundary/layout/corruption/cancellation checks. Results are under `../.verification/cache-1.6.16/`. Full corrected AssetBundle loading and speed gains remain unmeasured in the game; confirm an actual `LZ4 cache` entry and its phase timings.

Version 1.6.15 passes the Release build, 39 old-Mono file-exception compatibility assertions, the existing 156 cache-manager assertions and installed-runtime compatibility checks including 31 Harmony signatures. A separate x86 host loads the actual game `mono.dll`/corlib (runtime 2.0.50727.1433): the old manager reproduces the missing `.bad` failure; fixed production cache/codec sources build a fresh 110,259,016-byte DMM cache (26,007 ms for that isolated conversion), then validate and reuse it after manager reinitialization without reconversion. Records are under `../.verification/cache-1.6.15/`. This exercises actual Mono file/conversion behavior without launching the game or executing Unity native AssetBundle loading; conversion duration is not level-entry latency.

Version 1.6.14 passes 45 asset-index assertions (including ten real DMM object/address mappings), 22 production-loader scenarios, 42 preview/warmup assertions, 31 lifetime assertions and 18 background-cache eligibility assertions. The loader/consumer/lifetime native boundaries use fakes; the lifetime check compiles four verbatim production methods. Network, server and frontend regressions retain 59 assertions, 13 scenarios and 96 assertions respectively (including the final assembly-version check). Detailed sources, results and the final assembly manifest are under `../.verification/level-1.6.14/`. These checks establish managed request behavior and installed API compatibility; game-native load acceptance, actual time/memory savings and two-player Steam regression remain untested. No deployed game files were changed.


Version 1.6.13 passes the Release build and existing installed-runtime compatibility checks, including 31 Harmony wrappers. An isolated x86 conversion of the real DMM bundle preserves all 161,349,396 uncompressed bytes, node directories, and all 787 object records/external references checked independently with liblzma, python-lz4 and UnityPy. Output is 110,259,016 bytes in 1,231 blocks (original: 95,522,720 bytes); this is an on-disk format check, not an in-game latency or memory benchmark. The production cache manager passes 156 assertions, and info-loader integration passes 26 assertions with fake Unity/cache boundaries. Detailed converter, manager and integration checks are under `../.verification/cache-1.6.13/`. No game launch or deployment was performed; Unity 2017 native acceptance and host/client loading times remain to be measured.


Version 1.6.12 passed a Release build, 59 network transition assertions against the production gate/queue, 13 server preparation scenarios against the production iterators, and 95 frontend assertions using installed managed game types and the production request policies. The same network harness reproduces the cancelled-request save-ready failure with 1.6.11 sources; the server baseline fails all 11 cancellation scenarios while passing its two normal-entry scenarios. Existing cache, thumbnail, copied-message, save-selection and 31 Harmony wrapper checks also pass; the handoff check now keeps the save-state guard until target scene arrival.

Local reproduction sources and results are in `../.verification/network-1.6.12/`, `server-1.6.12/`, `frontend-1.6.12/`, and `fixes-1.6.12/`. External/native boundaries are stubbed in the network/server checks. Frontend checks exercise managed request policies and prefix branches, not Unity native dialogs. These results do not establish actual Steam connectivity, bundle lifetime correctness in Unity, or resolution of every Qichi client disconnection. The built DLL has not been deployed by these checks.

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
- `DIY save dialog opened/closed`, `DIY save choice confirmed`, `DIY server save acknowledgement received` and `DIY server/client save UI closed` for the choice, receipt and UI cleanup phases. Server receipts list each machine/slot state without account IDs. `DIY client loading acknowledged before resources` and `DIY client state sent` show the loading handoff. `DIY client disconnect path` records the game's disconnect caller chain for two minutes after a gated entry without changing the disconnect behaviour. `Qichi runtime request startup` measures the synchronous request startup before the native async wait.
- `level loading priority` / `level loading priority released` for the temporary priority scope; `level bundle ready` and `level info asset ready` for phase timing. Release logs include elapsed time since the original scene-load handoff when available.
- `scene material audit` and `scene material invalid` for a read-only check of shader references and support after DIY scene entry. The audit does not replace materials.

Exercise browsing, sorting, previews, save selection/read/write, entry, repeated restarts, transitions between sets, return to the menu, hot reload, and host/client multiplayer. Record process private memory across cycles; transitions may have temporary peaks. Compilation and hook signature checks do not replace gameplay testing.

Version 1.6.11 retains the offline checks for deferred-message ownership, one replay, an early start acknowledgement without a duplicate on replay, rejection of stale save states, preservation of the original kitchen-ready signal, cancellation, failed sends and reconnection. Confirmed-choice reuse, isolation between frontends/resets and yielding until save cleanup succeeds are also checked. All 31 patched signatures and the frontend field/method contracts resolve against the installed assemblies. The source split additionally preserves the tokenized declarations/bodies of all 23 top-level types and 410 members, including attributes, base types and field order, with only partial declarations and the version constant changed. DMM multiplayer entry was reported working with 1.6.9; the later native UI changes and multiplayer entry still require regression. Qichi client disconnection remains under investigation and requires the affected client's log.

## Disclaimer

Unofficial community patch, not affiliated with Team17 or gua248. Developed through interoperability analysis of OC2DIYLevel; contains none of its original source code.

The built-in LZMA decoder uses a public-domain LZMA SDK subset; see [third-party notices](THIRD_PARTY_NOTICES.md).
