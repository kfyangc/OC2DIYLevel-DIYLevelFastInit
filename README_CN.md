# DIYLevelFastInit

《Overcooked! 2》[OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) 的启动与内存优化补丁。需要 BepInEx 5 和 OC2DIYLevel 0.10.0。

[English README](README.md)

## 1.6.8 的行为

此前的异步加载消除了主菜单的长时间冻结，但全部 `info_*` 资源和所有关卡的四套人数配置仍然常驻。对于 32 位游戏，启动快并不等于内存占用低。

现在保留完整的轻量关卡目录，配方、音乐、截图等资源按需加载：

- **启动扫描**：先准备 `common*`，再逐个异步读取关卡集的 `info_*`。复制名称、UID、场景名、依赖名称及人数设置后，使用 `Unload(false)` 关闭包；清除临时完整信息引用后，在前端等待 Unity 回收无引用资源，再继续扫描。不再同时排队全部 info 包。
- **磁盘目录缓存**：缓存写入 `BepInEx/cache/DIYLevelFastInit/`，采用带版本号、长度上限和完整性检查的二进制格式，只包含字符串与数值，不依赖 Unity 的 JSON 序列化器。下次启动按路径、info 文件名、长度及修改时间校验，命中时无需加载对应 info 包。缓存损坏或无法写入时回退到扫描；可在游戏关闭后删除缓存以强制重建。
- **轻量场景目录**：保留全部关卡及原有位置映射，供菜单、街机和存档使用；打开存档选择界面不再生成全量配方配置。
- **目标关卡配置**：单机／房主确认存档后先让出一帧，异步读取目标关卡的 info 资源，再分帧准备该关卡的四套人数配置，显示准备进度；就绪后只继续一次原来的服务器加载流程，准备中合并重复请求。其他关卡不预热，重开同关可复用目录配置。远端客机在客户端邮箱分发进图消息之前，复制目标场景、加载状态和加载画面参数，异步准备资源并逐帧生成四套配置；就绪后只恢复一次原来的消息分发。等待期间继续处理其他网络消息，并显示 `client` 准备进度。原来的 `LoadLevelClientPatch` 保留同步时序，恢复时使用已经准备好的资源。重复进图请求合并；改选关卡、返回其他场景、断线或重连时，旧请求失效，正在进行的原生资源请求完成后再释放资源。
- **加载优先级**：单机／房主确认进入关卡、已有缩略图和目录请求结束后，临时把 Unity 后台加载优先级提高到 `High`，覆盖资源准备及目标场景加载；目标关卡或前端场景就绪、准备失败、插件销毁时恢复之前的值。如果其他加载器已改成不同的值，则保留其修改。加载期间可能牺牲帧率以缩短等待，并分别记录资源包加载与 info 资源实例化耗时；资源保留策略和磁盘缓存格式保持原状。
- **跨关卡集依赖**：读取场景和 info 的资源引用元数据，递归加载它们实际依赖的其他 info 包，修复只加载自身 info 时跨关卡集材质丢失的问题。依赖包只打开容器，不额外读取其 `LevelSetInfo` 或生成关卡配置；共享依赖保留到最后一个使用者退出。只读元数据解析内置在 DLL 中，无需 Python 或额外插件。
- **七池独立资源**：读取 `info_qichi` 前，先通过已安装的七池运行库（v10 接口）准备独立的 `qichi_assets`。等待已有异步请求，或复用已加载的包，并同步运行库的完成状态，避免反复重复加载。冷扫描、源图预览和单机／房主、客户端进图均覆盖；资源包仍由七池运行库持有，FastInit 不卸载。缺少运行库时记录提示；已安装的加载器接口不兼容或请求失败时，中止本次资源准备。
- **资源回收**：按场景是否仍在使用决定何时释放 info、配置和运行时 prefab。info 包解除引用并 `Unload(false)` 后，合并清理请求，在前端由 `Resources.UnloadUnusedAssets()` 回收无引用对象，保留仍被使用的材质和 Shader；不在 DIY 关卡或切图中主动启动这项清理。切图允许新旧资源短暂重叠；每次重开产生的场景运行时对象按场景实例回收。
- **提前生成缩略图**：内置对 OC2StageThumbnailPreview 1.0.0 的适配。目录加载完成后，自动逐个关卡集提前生成小图，写入 `BepInEx/cache/DIYLevelFastInit/thumbnails/`，无需先逐张浏览；屏幕显示缓存进度。每个关卡集生成完就释放源资源并等待回收。之后浏览直接读取硬盘上的小图，最大 640 × 360；内存最多保留最近 8 张，进入关卡时清空，硬盘最多 128 MiB，超限淘汰最旧条目。按 info/common 源文件指纹判断过期，校验图片尺寸、长度和校验和。后台生成会为游戏、切图和未缓存的前台预览暂停；已缓存的图片可直接读取，无需等待正在进行的预生成。缺失或无效缓存回退到异步读取源包。快速切换只显示最新选中的图片；排序插件仍使用原有的轻量 SO 对象。
- **选档去重**：拦截同帧重复选关、已打开或切图期间的重复选档请求，以及已确认存档的重复回调。只作用于 DIY DLC 15；取消选档后仍可重新选择。
- **热刷新**：保留“刷新关卡列表”，只更新有变化的目录；正在切图或关卡内不刷新。刷新过程中禁止选关。
- **异步请求**：15 秒未完成时记录等待提示，继续等待同一个请求；仅在请求已经完成且失败后同步重试，避免无法取消的旧请求与新请求争抢同一个包。

原版共享依赖仍由原版管理器及其引用计数管理。本补丁不强制清空公共包，也不修改游戏 EXE、关卡文件、原 mod DLL 或存档格式。

## 边界

重资源的最小加载单位仍然是一个 `info_*` 包及场景/关卡集的关联 info 依赖，不是单张关卡。超大的单个关卡集仍可能占用较多内存；若要进一步缩小单位，需要调整关卡导出和拆包方式。`common*` 以及其他 MOD 的常驻资源也不在本次优化的回收范围内。

第一次扫描、预生成缩略图，以及第一次进入关卡，仍有读取、解压和配置生成成本。预生成期间会暂时加载源 info 资源，每帧最多生成一张图片，关卡集之间等待无引用资源回收；尚未缓存的图片仍可能较慢显示。未安装缩略图插件时跳过预生成。单机／房主与正常联机客机准备期间异步等待资源加载，但每套配置仍在主线程生成，单套复杂配置或 Unity 场景激活仍可能造成个别慢帧。客机门控针对原版可靠的 `LevelLoadByName` 消息；直接调用原插件客户端准备方法的其他 MOD 仍走同步兜底。等待初始目录加载完成后再使用完整关卡列表和联机。

七池适配调用运行库自身的读文件和解码流程，启动请求时仍可能产生慢帧；仅覆盖已核对的 `info_qichi` 布局，不会自动发现其他 MOD 的任意独立资源包。冷启动后的七池背景显示仍需实机验证。

## 安装与回退

关闭游戏，备份已安装的 `BepInEx/plugins/DIYLevelFastInit.dll`，再用新 DLL 替换。回退时关闭游戏并恢复备份；目录缓存可保留，也可删除。不要让两个版本的 DLL 同时出现在 plugins 扫描范围内。

## 构建

```powershell
dotnet build -c Release -p:GameDir="D:\Games\Steam Games\steamapps\common\Overcooked! 2"
```

产物：`bin/Release/DIYLevelFastInit.dll`。目标为 .NET Framework 3.5 / C# 7.3；保留项目的 `NuGet.config`。

## 验证

查看 `BepInEx/LogOutput.log` 中的 `DIYLevel FastInit`：

- `level set [...] ready (... metadata only)`：扫描完成后只保留目录。
- `catalog cache hit: ...`：重启命中磁盘缓存。
- `lightweight scene directory: ... no recipe configs`：存档界面未全量生成配置。
- `on-demand info loaded/released`：当前跟踪的包数量，不能据此推断所有旧资源已立即回收。
- `info dependency graph`、`info dependency released`：目标场景需要的 info 包及共享依赖的释放。
- `Qichi dependency loading` / `Qichi dependency ready`：七池独立背景资源；新进程中应先就绪，再出现 `on-demand info loaded` 和七池场景加载，且不应持续报告重复的 `Memory` 包。
- `on-demand preview info loaded`、`thumbnail ready (... async ... ms elapsed)`：异步预览完成；耗时包含该次加载的异步等待时间，不代表主线程冻结时间。
- `thumbnail prewarm complete: ... disk entries ready`：提前生成的磁盘条目数量；`thumbnail cache hit: ... (disk|memory, ... ms)`：浏览命中缓存，没有读取源 info。
- `on-demand config ready/released`、`scene runtime objects released`：目标配置与每次场景实例的回收。
- `async level preparation started`、`async level resources ready`、`on-demand config ready (... async level entry ...)`：单机／房主异步准备；总耗时包含异步等待，单套配置耗时超过或等于 50 毫秒时还会输出 Info 计时。
- `DIY client level message deferred/resumed`、`async client level preparation started`、`client level bundle ready`、`client level info asset ready`：客机异步准备及一次恢复；`DIY client level preparation cancelled`：旧连接或旧目标失效。
- `duplicate DIY ... ignored`：重复选关、选档或存档确认被拦截。
- `level loading priority`、`level loading priority released`：临时提高与释放加载优先级；`level bundle ready`、`level info asset ready`：分阶段计时。优先级释放日志还会记录交给原始切图流程后的耗时。
- `scene material audit`、`scene material invalid`：进入 DIY 场景后对材质 Shader 引用与支持状态做一次只读检查；不会自动替换材质。

回归检查应覆盖：浏览和排序、缩略图、存档选择与读写、进入关卡、连续重开、跨关卡集切图、返回菜单、热刷新，以及房主和客户端联机。记录游戏进程的专用内存；场景切换有暂时峰值，不能仅凭一次读数判断泄漏。构建和签名检查不能替代实机测试。1.6.8 已做离线消息快照、去重、一次恢复、取消、替换目标、重连和回调失败检查；DMM、七池、test 的双方联机，以及选档取消后重选和重复弹窗仍需实机回归，不能据此承诺所有网络错误已经修复。

## 声明

社区第三方补丁，与 Team17、gua248 无关。通过对 OC2DIYLevel 插件的互操作分析开发，不含其原始代码。

内置 LZMA 解码器使用公开领域的 LZMA SDK 子集，见 [第三方说明](THIRD_PARTY_NOTICES.md)。
