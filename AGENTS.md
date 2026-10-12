# AGENTS.md

BepInEx 5 plugin (net35, single file `FastInitPlugin.cs`) that patches the
[OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) plugin for Overcooked! 2:
replaces its synchronous full-bundle load at main-menu save load with an async background
coroutine, plus a self-healing level-set menu and a progress indicator.

## Build / deploy / verify

```bash
dotnet build -c Release                      # uses default GameDir from csproj
dotnet build -c Release -p:GameDir="C:\Path\To\Overcooked! 2"
cp bin/Release/DIYLevelFastInit.dll "<GameDir>/BepInEx/plugins/"
```

- No tests. Verify by launching the game and grepping BepInEx logs for `DIYLevel FastInit`
  (`BepInEx/LogOutput.log` and `BepInEx/plugins/OC2DIYLevel/logs/logs_info_*/BepInExDebugLog.log`).
  Key lines: `common bundles: ...`, `level set [name] ready (N total)`,
  `FastInit complete: N level sets available`, `level set menu rebuilt: N entries`.
- The machine has no default NuGet source; `NuGet.config` pins nuget.org. Don't remove it.

## Architecture

- Harmony **prefix** on `DIYLevelAssetBundleManager.Initialize` (skip original): cheap part
  sync (`RunCheapInit`), heavy part in a coroutine on `FastInitHost` (parallel prefetch of
  all `info_*` bundles, consumed in directory order, 15s timeout → sync fallback).
- Harmony **postfix** on `DIYLevelEntryUI.AddUI`: re-syncs the menu button list
  (`HealLevelSetButtons`), because the original builds it only once (snapshot guard).
- Harmony prefix+postfix on `DIYLevelAssetBundleManager.SetupSceneDirectoryData`
  (`SceneDirectoryCache`, since 1.5.0): serve a cached directory while the level-set
  list is unchanged; warm-built in the background. The cached configs reference
  `RuntimePrefabManager` clones (custom burger/pizza assembly `m_platingPrefab`), which
  every `StartEmptySession` destroys via `ClearAllRuntimePrefabs(isQuit:true)` right
  before rebuilding — so since 1.5.1 that clear is skipped while the cache is live
  (prefix on `ClearAllRuntimePrefabs`), clones are accounted via a postfix on
  `CloneAsInactivePrefab`, and superseded clones are destroyed at the next blanket-clear
  point (the old session is gone there). An epoch guard makes an in-flight warm discard
  its directory if a blanket clear ran mid-build.
- Private statics of the target plugin are set via reflection (`AccessTools.Field`):
  `commonBundle`, `diyLevelCover`, `diyLevelGameSessionPrefab`, `configTemplateSO`,
  `Instance`, `levelSetSelectionMenu`, and method `AddLevelSetButton`. These names are the
  contract with the compiled OC2DIYLevel 0.10.0 — if that plugin updates, re-verify against
  its decompiled source at `C:\Users\yang\.cache\OC2DIYLevel-decompiled\OC2DIYLevel_src\`.
- `IsInitialized` (= `commonBundle != null`) must become true synchronously; all consumer
  patches in the original check it.

## Constraints

- Target framework net35 / C# 7.3: no ValueTuples, no `yield` inside try/catch.
- `levelSetInfos` order must remain directory order — the arcade mod maps level-set
  selection by list position.
- Plugin dir must be resolved from `typeof(DIYLevelAssetBundleManager).Assembly.Location`,
  never `GetExecutingAssembly()` (would point at this plugin's own directory).
- Keep `common*` bundles loaded before consuming `info_*` bundles (dependency resolution).

## Code style

- Comments in English, minimal: one class-level `<summary>` plus single-line `//` notes
  only where the code can't speak (see current file).
- Chinese only in functional UI/localization strings. If any non-ASCII source is added,
  keep the UTF-8 BOM (csc otherwise reads the file as the system codepage).

## Build gotchas

- The `UnityEngine.dll` facade alone is not enough: module references
  (CoreModule, AssetBundleModule, IMGUIModule, TextRenderingModule, UnityEngine.UI) are required.
- The game has a global-namespace `UIUtils` that shadows `OC2DIYLevel.UIUtils` — fully
  qualify the latter.
- `[BepInDependency]` with the two-argument enum fails to resolve; use the single-argument
  form (defaults to hard dependency).

## Publishing rule

Creating the GitHub repo, pushing, or cutting releases requires the owner's explicit
review and approval first — never push on your own initiative.
