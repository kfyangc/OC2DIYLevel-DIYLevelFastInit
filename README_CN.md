# DIYLevelFastInit

《Overcooked! 2》[OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) 的启动与内存优化补丁。需要 BepInEx 5 和 OC2DIYLevel 0.10.0。

[English README](README.md)

## 1.6.3 的行为

此前的异步加载消除了主菜单的长时间冻结，但全部 `info_*` 资源和所有关卡的四套人数配置仍然常驻。对于 32 位游戏，启动快并不等于内存占用低。

现在保留完整的轻量关卡目录，配方、音乐、截图等资源按需加载：

- **启动扫描**：先准备 `common*`，再逐个异步读取关卡集的 `info_*`。复制名称、UID、场景名、依赖名称及人数设置后，使用 `Unload(false)` 关闭包；清除临时完整信息引用后，在前端等待 Unity 回收无引用资源，再继续扫描。不再同时排队全部 info 包。
- **磁盘目录缓存**：缓存写入 `BepInEx/cache/DIYLevelFastInit/`，采用带版本号、长度上限和完整性检查的二进制格式，只包含字符串与数值，不依赖 Unity 的 JSON 序列化器。下次启动按路径、info 文件名、长度及修改时间校验，命中时无需加载对应 info 包。缓存损坏或无法写入时回退到扫描；可在游戏关闭后删除缓存以强制重建。
- **轻量场景目录**：保留全部关卡及原有位置映射，供菜单、街机和存档使用；打开存档选择界面不再生成全量配方配置。
- **目标关卡配置**：房主或客户端收到目标关卡后，准备该关卡的四套人数配置。其他关卡不预热。重开同关可复用目录配置。
- **跨关卡集依赖**：读取场景和 info 的资源引用元数据，递归加载它们实际依赖的其他 info 包，修复只加载自身 info 时跨关卡集材质丢失的问题。依赖包只打开容器，不额外读取其 `LevelSetInfo` 或生成关卡配置；共享依赖保留到最后一个使用者退出。只读元数据解析内置在 DLL 中，无需 Python 或额外插件。
- **资源回收**：按场景是否仍在使用决定何时释放 info、配置和运行时 prefab。info 包解除引用并 `Unload(false)` 后，合并清理请求，在前端由 `Resources.UnloadUnusedAssets()` 回收无引用对象，保留仍被使用的材质和 Shader；不在 DIY 关卡或切图中主动启动这项清理。切图允许新旧资源短暂重叠；每次重开产生的场景运行时对象按场景实例回收。
- **缩略图**：内置对 OC2StageThumbnailPreview 1.0.0 的异步适配。打开关卡集后菜单先显示，图片随后加载，避免在菜单回调中同步读取整个 info 造成数秒冻结。快速切换时合并请求，只显示最新选中的图片；旧请求等待完成后释放，不会覆盖新图片。仅保留一个预览关卡集，排序插件仍使用原有的轻量 SO 对象。
- **热刷新**：保留“刷新关卡列表”，只更新有变化的目录；正在切图或关卡内不刷新。刷新过程中禁止选关。
- **异步请求**：15 秒未完成时记录等待提示，继续等待同一个请求；仅在请求已经完成且失败后同步重试，避免无法取消的旧请求与新请求争抢同一个包。

原版共享依赖仍由原版管理器及其引用计数管理。本补丁不强制清空公共包，也不修改游戏 EXE、关卡文件、原 mod DLL 或存档格式。

## 边界

重资源的最小加载单位仍然是一个 `info_*` 包及场景/关卡集的关联 info 依赖，不是单张关卡。超大的单个关卡集仍可能占用较多内存；若要进一步缩小单位，需要调整关卡导出和拆包方式。`common*` 以及其他 MOD 的常驻资源也不在本次优化的回收范围内。

第一次扫描、第一次预览某个关卡集，以及第一次进入关卡，仍有读取、解压和配置生成成本。选关准备使用同步路径，因此单个大包首次选中时可能短暂停顿。等待初始目录加载完成后再使用完整关卡列表和联机。

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
- `on-demand preview info loaded`、`thumbnail ready (... async ... ms elapsed)`：异步预览完成；耗时包含该次加载的异步等待时间，不代表主线程冻结时间。
- `on-demand config ready/released`、`scene runtime objects released`：目标配置与每次场景实例的回收。
- `scene material audit`、`scene material invalid`：进入 DIY 场景后对材质 Shader 引用与支持状态做一次只读检查；不会自动替换材质。

回归检查应覆盖：浏览和排序、缩略图、存档选择与读写、进入关卡、连续重开、跨关卡集切图、返回菜单、热刷新，以及房主和客户端联机。记录游戏进程的专用内存；场景切换有暂时峰值，不能仅凭一次读数判断泄漏。构建和签名检查不能替代实机测试。

## 声明

社区第三方补丁，与 Team17、gua248 无关。通过对 OC2DIYLevel 插件的互操作分析开发，不含其原始代码。

内置 LZMA 解码器使用公开领域的 LZMA SDK 子集，见 [第三方说明](THIRD_PARTY_NOTICES.md)。
