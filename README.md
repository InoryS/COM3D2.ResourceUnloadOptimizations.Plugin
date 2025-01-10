[English](#english) | [简体中文](#%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87)

# English
# COM3D2.ResourceUnloadOptimizations.Plugin
This plugin is only for COM3D2.

This plugin is a modified version of [BepInEx.ResourceUnloadOptimizations](https://github.com/BepInEx/BepInEx.Utility/tree/master/BepInEx.ResourceUnloadOptimizations)

Released under GNU GENERAL PUBLIC LICENSE Version 3.

Some code (monoPatcher) comes from [COM3D2.DCMMemoryOptimization](https://github.com/silver1145/) which in turn comes from [KK]MMDD (@Countd360).

Released under MIT.

### Compatibility

Tested with COM3D2 2.40.0, COM3D2 2.41.0

Not tested with COM3D2.5, Since 3.41.0 updated the unity engine, it will most likely not work.

But if you use 3.41.0 you probably don't need it, because Unity 2022 has incremental garbage collection.

### Why fork
Since we wanted to add some specific memory management logic in the COM3D2 environment, we forked the official library and made a lot of customization and hooks to meet the needs of using it in the specific game scenario of COM3D2.

### Function
This plugin aims to optimize the two "resource cleanup/garbage collection" operations GC.Collect() and Resources.UnloadUnusedAssets() that are frequently called in the game:

1. **Reduce unnecessary GC overhead and resource unloading**
  - Many games frequently trigger GC and resource unloading after scene loading or resource use, which can cause random freezes and frame rate jitters in the game.
  - This plugin uses Hook to determine whether the current system memory is still sufficient, and delays or skips unnecessary GC or resource unloading to keep the game running as smoothly as possible.

2. **GC can be completely turned off in specific scenes (such as dance scenes, DCM dance modules, etc.)**
  - When it is determined that the player wants to focus on performance in high-occupancy, high-frequency dance scenes, GC can be temporarily disabled completely, making the game's use of memory more relaxed, thereby reducing freezes.
  - If external memory pressure begins to increase, this plugin can also dynamically restore GC or perform a complete cleanup to avoid crashes caused by insufficient memory.

3. **Highly configurable**
  - All strategies in the plugin can be configured according to personal needs, including whether to perform GC immediately after the scene is unloaded, whether to perform GC regularly, the threshold used by the dance scene, etc., which can be adjusted by yourself.
```
Core idea: When there is sufficient remaining memory, the game will not trigger GC or resource unloading as much as possible to avoid frequent loading and unloading;
When the memory usage reaches the preset threshold or exits from the dance scene, the necessary full GC will be performed to balance memory usage and performance.
```

### Working principle
1. **Hook** `GC.Collect()`
  - When the game or third-party plugin calls GC.Collect(), this plugin will count or delay processing instead of executing it immediately.
  - If multiple calls are triggered repeatedly, this plugin will only perform an actual garbage collection operation once in a short period of time to avoid frequent GC.

2. **Hook** `Resources.UnloadUnusedAssets()`
  - Intercept Unity's call for resource unloading through native detour.
  - If the current system memory and paging file (PageFile) are both very sufficient, the real UnloadUnusedAssets() will be skipped or delayed.
  - If the current memory is tight, perform this operation at the right time to release resources.

3. **Enable/disable Mono GC level**
  - This plugin uses MonoPatcherHooks.gcSetStatusX(...) to conditionally enable or disable Mono GC during game operation.
  - In high-performance consumption scenarios such as dancing, if the relevant configuration is enabled, GC can be automatically disabled to avoid jamming during dancing.
  - At the same time, there will be timing or memory threshold detection. Once it is detected that the threshold is exceeded, GC will be enabled again and forced to recycle to prevent the game from crashing due to full memory.

4. **Memory and paging file status check**
  - The plugin obtains the current physical memory, total paging file and usage rate through MemoryInfo.GetCurrentStatus().
  - When it is detected that the system's physical memory usage or paging file usage exceeds the configured threshold, or the available space is less than the configured minimum value, trigger forced GC or perform resource unloading.
  - If there is still enough memory, skip these time-consuming operations and let the game continue to use memory as much as possible.

## Installation method
This plug-in is a replacement for `BepInEx.ResourceUnloadOptimizations.dll`, `COM3D2.DCMMemoryOptimization.dll`, `COM3D2.Placebo.Plugin.dll` and possible similar plug-ins. Please make sure to remove these plug-ins before installation.

1. Download and unzip this plug-in to the `COM3D2\BepInEx\plugins` folder. (Note that this plug-in depends on monoPatcher.dll, don't forget that)
2. After starting the game, the plug-in will automatically take effect and generate the corresponding configuration file in the `BepInEx\config` folder (default file name: COM3D2.ResourceUnloadOptimizations.Plugin.InoryS.cfg).

## Configuration item description
The following are the core configuration items provided by this plugin, which can be modified in the generated .cfg file:

1. **Garbage Collection**
  - `Enable Full GC OnScene Unload`
    - Default value: true
    - Effect: Perform a full garbage collection (GC.Collect) when the scene is unloaded. It helps to load the next scene, but it will slightly increase the loading time.
  - `GC After Dance`
    - Default value: true
    - Effect: Perform a full garbage collection after the dance or DCM dance ends. If you frequently exit the dance scene or the memory is tight, it is recommended to turn it on.
  - `Max Heap Size`
    - Default value: 20000 MB
    - Effect: The maximum amount of heap memory the game can use, there seems to be a hardcoded limit, the game will crash with a "Too many heap sections" message when heap size exceeds around 23500 MB. exceeding this value will trigger a full GC.
2. **Unload Throttling**
  - `Maximize Memory Usage`
    - Default value: true
    - Effect: If true, delay unloading resources and GC as much as possible, allowing the game to continue to occupy more memory when the physical memory and paging file are sufficient, thereby reducing random jams.
  - `Memory Threshold (PercentMemoryThreshold)`
    - Default value: 75 (unit: %)
    - Effect: When the physical memory usage is lower than this threshold, skip GC and UnloadUnusedAssets operations; otherwise, execute.
  - `Page File Free Threshold`
    - Default value: 0.4 (unit: ratio, 0~1)
    - Effect: The remaining ratio threshold of the paging file. If the current remaining ratio of the paging file is higher than this value, skip garbage collection; if it is lower than this value, force collection.
  - `Min Avail Page File Bytes`
    - Default value: 2GB
    - Effect: The lower limit of the available paging file capacity. If the available paging file is lower than this value, force GC.
3. **Dance Unload Throttling**
  - `Dance Maximize Memory Usage`
    - Default value: true
    - Effect: When in a dance scene (including DCM), if true, GC will be further relaxed or directly turned off to make the dance run more smoothly.
  - `Dance Memory Threshold`
    - Default value: 90 (unit: %)
    - Function: The physical memory threshold used in the dance scene, which can usually be set higher to allow the game to use more memory when dancing.
  - `Dance Page File Free Threshold`
    - Default value: 0.1
    - Function: The remaining ratio threshold of the paging file in the dance scene.
  - `Dance Min Avail Page File Bytes`
    - Default value: 2GB
    - Function: The minimum capacity of the available paging file in the dance scene.
4. **Periodic GC**
  - `Enable Periodic GC`
    - Default value: false
    - Function: Whether to enable periodic garbage collection.
  - `Periodic GC Interval`
    - Default value: 300 (unit: seconds)
    - Function: Set the time interval of periodic GC. If enabled, GC.Collect will be executed every this length of time.
5. **TEST**
  - `Disable Resource Unload`
    - Default value: false
    - Function: Test switch. If turned on, it will completely disable the Resources.UnloadUnusedAssets() call, causing the game to almost never unload resources. Danger: Very memory-intensive, crashes if insufficient memory occurs.

### Usage suggestions
1. **Memory and configuration**
  - By default, this plugin aims to "minimize lags". However, if your system does not have much available memory, it may cause the game to occupy too much memory and cause other problems (such as crashes, system lags, etc.). It is recommended to lower the threshold appropriately according to your computer configuration.
  - For environments with 32GB or more memory, consider raising the threshold appropriately to minimize GC in the game.

2. **Trade-offs of dance scene GC**
  - Although turning off dance scene GC can greatly reduce lags during dancing, if GC is not performed for a long time and scenes are switched frequently, it may cause the game memory to swell.
  - If you have a lot of MODs/plugins and the character costumes and props are complex, it is recommended to keep GCAfterDance true and do a recycling after the dance.

3. **Periodic GC function**
  - If you are worried that not turning on GC for a long time will cause memory expansion, you can turn on EnablePeriodicGC and set the Periodic GC Interval to a longer value (such as 5~10 minutes).
  - This will not only prevent loss of control during long-term games, but also reduce the lag caused by frequent GC.

4. **Disable Resource Unload Risk**
  - This option is only used when testing or troubleshooting lag. Because this will make the game almost never unload resources, the game may repeatedly call resources in some scenes, causing memory to surge.
  - If there is not enough physical memory/virtual memory, it is very likely to crash.

### Limitations and known issues
  - This plugin is not magic, it can only delay GC as much as possible and avoid lags by not performing GC during dancing, but it cannot really solve the lags caused by GC. And the premise of doing this is that you have more memory.
  - The plugin may not be compatible with all COM3D2 versions. If you encounter errors or conflicts with other plugins, please pay attention to whether there are plugins with the same functions or Hook conflicts (such as `BepInEx.ResourceUnloadOptimizations.dll` and `COM3D2.DCMMemoryOptimization.dll` and `COM3D2.Placebo.Plugin.dll`, etc.), try to uninstall or disable them before testing this plugin.
  - Due to the low-level memory management Hook, if there are unknown problems, the game may crash or memory leak, please use it with caution and make backups.
  - In some extreme scenarios, disabling GC will cause serious memory usage. It is recommended to set a reasonable threshold or enable periodic GC to avoid risks.
  - If you open the IMGUI window (the transparent black window of the plugin) with GC disabled (while dancing), you will see the heap size increase very quickly. You may want to install [OptimizeIMGUI](https://github.com/BepInEx/BepInEx.Utility) to mitigate this slightly. However, try not to open the IMGUI window with GC disabled. (One set of data is that when I open the DCM window during the 3-person pole dance, my final heap size will reach 18G, but if I don't open any IMGUI window, then I will only reach 3.6G)













<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>









# 简体中文
# COM3D2.ResourceUnloadOptimizations.Plugin
本插件仅适用于 COM3D2 使用。

本插件是基于 [BepInEx.ResourceUnloadOptimizations](https://github.com/BepInEx/BepInEx.Utility/tree/master/BepInEx.ResourceUnloadOptimizations) 的衍生修改版本

已在 GNU GENERAL PUBLIC LICENSE Version 3 协议下发布。

部分代码（monoPatcher） 来自 [COM3D2.DCMMemoryOptimization](https://github.com/silver1145/) 而该仓库的代码又来自 [KK]MMDD (@Countd360)。

已在 MIT 协议下发布。

### 兼容性

已在 COM3D2 2.40.0, COM3D2 2.41.0 中测试通过

未测试 COM3D2.5，由于 3.41.0 更新了 unity 引擎，它很可能不工作

但是如果你使用 3.41.0 你大概也不需要它，因为 unity 2022 拥有增量式垃圾回收

### 为什么分叉
由于想在 COM3D2 环境下加入一些特定的内存管理逻辑，因此对官方库进行了 Fork 并进行了大量定制和 Hook，以满足在 COM3D2 这一特定游戏场景下使用的需求。

### 功能
本插件旨在对游戏中频繁调用的 GC.Collect() 和 Resources.UnloadUnusedAssets() 这两大“资源清理/垃圾回收”操作进行优化：

1. **减少不必要的 GC 开销和资源卸载**
    - 很多游戏在场景加载或资源使用完后会频繁触发 GC 与资源卸载，从而导致游戏出现随机卡顿、帧数抖动等现象。
    - 本插件通过 Hook 并判断当前系统内存是否仍然充足，将不必要的 GC 或资源卸载延后或跳过，以尽可能保持游戏的顺畅运行。
  
2. **在特定场景（如舞蹈场景、DanceCameraMotion 舞蹈模块等）可完全关闭 GC**
    - 当判断玩家希望在高占用、高频率的舞蹈场景中集中性能时，可临时完全禁用 GC，使得游戏对内存的使用更加宽松，从而减少卡顿。
    - 如果外部内存压力开始变大，本插件也能动态恢复 GC 或执行一次完全清理，避免内存不足导致的崩溃。
   
3. **高度可配置**
    -  插件中的各项策略都可根据个人需求进行配置，包括是否在场景卸载后立刻进行 GC、是否定期执行 GC、舞蹈场景使用的阈值等，都可以自己调节。
```
核心思路：让游戏在有充足剩余内存时，尽可能不触发 GC 或资源卸载，避免频繁的加载和卸载；
当内存使用量达到预设阈值或从舞蹈场景退出时，再进行必要的完全 GC，以平衡内存占用和性能。
```

### 工作原理
1. **Hook** `GC.Collect()`
   - 当游戏或第三方插件调用 GC.Collect() 时，本插件会进行计数或延后处理，而不马上执行。
   - 如果重复触发多次调用，本插件会在短时间内只执行一次实际的垃圾回收操作，避免频繁 GC。

2. **Hook** `Resources.UnloadUnusedAssets()`
   - 通过原生 detour 的方式拦截了 Unity 对资源卸载的调用。
   - 如果当前系统内存及分页文件（PageFile）都非常宽裕，则会跳过或延迟真正的 UnloadUnusedAssets()。
   - 如果当前内存紧张，则在合适时机执行此操作，释放资源。

3. **Mono GC 层面的启用/禁用**
     - 本插件使用了 MonoPatcherHooks.gcSetStatusX(...) 来在游戏运行中有条件地启用或禁用 Mono GC。
     - 在舞蹈等高性能消耗场景，若启用了相关配置，可自动完全禁用 GC，以避免舞蹈过程中出现的卡顿。
     - 同时会有定时或内存阈值检测，一旦检测到超过阈值，就会再启用 GC 并强制回收，防止因内存爆满导致游戏崩溃。
       
4. **内存及分页文件状态检查**
    - 插件通过 MemoryInfo.GetCurrentStatus() 获取当前物理内存、分页文件总量及使用率。
    - 当检测到系统的物理内存使用率或分页文件使用率超过配置的阈值，或可用空间小于配置的最小值时，触发强制 GC 或执行资源卸载。
    - 若内存仍然充足，则跳过这些耗时操作，让游戏继续尽情使用内存。
  
## 安装方法
本插件是 `BepInEx.ResourceUnloadOptimizations.dll` 和 `COM3D2.DCMMemoryOptimization.dll` 和 `COM3D2.Placebo.Plugin.dll` 和可能的同类型插件的替代品，安装前请确保移除这些插件。

1. 下载并解压本插件到 `COM3D2\BepInEx\plugins` 文件夹。（注意本插件依赖 monoPatcher.dll 别防漏了）
2. 启动游戏后，插件会自动生效并在 `BepInEx\config` 文件夹下生成对应的配置文件（默认文件名：COM3D2.ResourceUnloadOptimizations.Plugin.InoryS.cfg）。


## 配置项说明
以下为本插件提供的核心配置项，均可在生成的 .cfg 文件中修改：

1. **Garbage Collection**
 - `Enable Full GC OnScene Unload`
   - 默认值：true
   - 作用：在场景卸载时执行一次完整的垃圾回收（GC.Collect）。有助于下一场景的加载，但会略微增加加载时间。
 - `GC After Dance`
   - 默认值：true
   - 作用：在舞蹈或 DCM 舞蹈结束后执行一次完整的垃圾回收。若频繁退出舞蹈场景或内存比较紧张时，建议开启。
 - `Max Heap Size`
   - 默认值：20000 MB
   - 效果：游戏可以使用的最大堆内存量，似乎有一个硬编码限制，当堆大小超过 23500 MB 左右时，游戏将崩溃并显示 “Too many heap sections”。超过此值将触发一次完整的垃圾回收。
2. **Unload Throttling**
 - `Maximize Memory Usage`
   - 默认值：true
   - 作用：如果为 true，尽可能延后卸载资源和 GC，允许游戏在物理内存及分页文件充足时继续占用更多内存，从而减少随机性卡顿。
 - `Memory Threshold (PercentMemoryThreshold)`
   - 默认值：75（单位：%）
   - 作用：当物理内存使用率低于该阈值时，跳过 GC 和 UnloadUnusedAssets 操作；反之则执行。
 - `Page File Free Threshold`
   - 默认值：0.4（单位：比率，0~1）
   - 作用：分页文件的剩余比率阈值。若当前分页文件剩余比例高于此值，则跳过垃圾回收；低于此值则会强制回收。
 - `Min Avail Page File Bytes`
   - 默认值：2GB
   - 作用：可用分页文件容量的下限值。若可用分页文件低于该值，则强制 GC。
3. **Dance Unload Throttling**
 - `Dance Maximize Memory Usage`
   - 默认值：true
   - 作用：当处于舞蹈场景（包括 DanceCameraMotion）时，若为 true，会进一步放宽或直接关闭 GC，让舞蹈运行得更流畅。
 - `Dance Memory Threshold`
   - 默认值：90（单位：%）
   - 作用：舞蹈场景下使用的物理内存阈值，通常可设置得高一些，使游戏在舞蹈时使用更多内存。
 - `Dance Page File Free Threshold`
   - 默认值：0.1
   - 作用：舞蹈场景下的分页文件剩余比率阈值。
 - `Dance Min Avail Page File Bytes`
   - 默认值：2GB
   - 作用：舞蹈场景下可用分页文件的最低容量。
4. **Periodic GC**
 - `Enable Periodic GC`
   - 默认值：false
   - 作用：是否启用周期性的垃圾回收。
 - `Periodic GC Interval`
   - 默认值：300（单位：秒）
   - 作用：设置周期性 GC 的时间间隔。如果启用，每隔该时长便会执行一次 GC.Collect。
5. **TEST**
 - `Disable Resource Unload`
   - 默认值：false
   - 作用：测试用开关，若开启会完全禁用 Resources.UnloadUnusedAssets() 调用，导致游戏几乎不卸载资源。危险：非常吃内存，若内存不足会发生崩溃。


### 使用建议
1. **内存与配置**
   - 本插件在默认情况下以“尽量减少卡顿”为主要目标。但如果你的系统可用内存本身不多，可能导致游戏占用过高而引起其他问题（如崩溃、系统卡顿等）。建议根据自己电脑配置适度调低阈值。
   - 对于 32GB 或以上内存的环境，可考虑适度提高阈值，让游戏尽量少做 GC。

2. **舞蹈场景 GC 的取舍**
   - 舞蹈场景 GC 关闭虽然能大幅减少舞蹈过程中的卡顿，但若长时间不进行 GC 并频繁切换场景，可能造成游戏内存膨胀。
   - 如果你的 MOD/插件很多且角色服装、道具复杂，建议保持 GCAfterDance 为 true，在舞蹈结束后做一次回收。

3. **定期 GC 功能**
   - 若担心长时间不开 GC 会导致内存膨胀，可以开启 EnablePeriodicGC，并把 Periodic GC Interval 适度设置得长一点（如 5~10 分钟）。
   - 这样既能在长时间游戏中不至于失控，又能减少频繁 GC 带来的卡顿。

4. **Disable Resource Unload 风险**
   - 该选项仅在测试或排查卡顿时使用。因为这会让游戏几乎不卸载资源，游戏可能在某些场景内反复调用资源从而导致内存暴涨。
   - 如果没有足够的物理内存/虚拟内存，就很有可能崩溃。


### 限制和已知问题
 - 这个插件不是魔法，它只能尽量延后 GC，和在舞蹈时不进行 GC 来避免卡顿，而不能真正解决 GC 带来的卡顿。而且这样做的前提是你有较多的内存。
 - 插件不一定对所有 COM3D2 版本兼容，若遇到报错或与其他插件产生冲突，请留意是否有相同功能或 Hook 冲突的插件（如  `BepInEx.ResourceUnloadOptimizations.dll` 和 `COM3D2.DCMMemoryOptimization.dll` 和 `COM3D2.Placebo.Plugin.dll` 等），尝试卸载或将其禁用后再测试本插件。
 - 由于进行了较底层的内存管理 Hook，如有未知问题可能导致游戏崩溃或内存泄漏，请谨慎使用并做好备份。
 - 在某些极端场景下，禁用 GC 会产生严重的内存占用，建议合理设置阈值或启用定期 GC 来规避风险。
 - 如果你在禁用GC（跳舞时）的情况下打开 IMGUI 窗口（插件的透明黑色窗口），那么你会发现堆大小快速提升。你也许想安装 [OptimizeIMGUI](https://github.com/BepInEx/BepInEx.Utility) 来轻微缓解此情况。尽管如此，请尽量不要在禁用 GC 时打开 IMGUI 窗口。（一组数据是当我在 3 人钢管舞过程中打开 DCM 窗口时，我最终的堆大小将达到 18G，但如果我不打开任何 IMGUI 窗口，那么我只会达到 3.6G）
