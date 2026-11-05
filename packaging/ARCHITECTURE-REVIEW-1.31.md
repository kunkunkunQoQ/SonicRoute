# 1.31 架构精简与 Wiki 对照

日期：2026-11-05。源码基线 `c77e8d09066cab8b52611f7d58d0997c6391785b`；正式发行标签仍为 `v1.30`，本轮是 1.31 开发改动，不发布新版本或商店包。

## 实现与取舍

| 范围 | 原路径 | 本轮实现 |
|---|---|---|
| 版本继承 | 四个产品项目分别导入 Version.props；AndroidAudio 漏导入，程序集使用默认版本 | Directory.Build.props 统一导入；唯一版本入口升为 1.31，五个产品项目均派生 1.31.0.0 |
| 手机源查找 | 公共入口每次创建谓词并遍历 Sources；相对音量 / 静音会重复解析同一目标 | 注册表在成员增删时同时发布有序列表与身份索引；ApplicationAudio 每个操作解析一次源 |
| 全部静音 | 先列举 / 读取以判断目标静音状态，再列举 / 读取以决定哪些目标可写 | 同一次应用列表和状态快照供判断与写入复用；每个应用一次状态读取，写入仍覆盖该 PID 的全部会话 |

身份索引只在添加、替换、移除和停止源时重建，读端不持锁、不分配谓词；旧快照不会被原地修改。索引增加与活动源数量成正比的小量引用存储，不复制 PCM、图标或传输资源，不增加依赖、定时器或后台扫描。成员更新仍由原管理器负责，未引入另一套服务容器。

会话退出、未知静音、部分写入失败、取消请求和同一应用多会话语义保留。Full 的活动手机连接 / 音频 / 虚拟屏不随关窗清理；Lite / Legacy 的工作集策略没有改动。

源码入口：

- [统一版本](../Directory.Build.props)、[版本值](../build/Version.props)。
- [统一应用操作](../SonicRoute/ApplicationAudio.cs)。
- [手机源注册表](../SonicRoute.AndroidAudio/AndroidAudioManager.cs)。

## 与实际 Wiki 的差异

对照的是 2026-11-05 获取的[技术实现原文](https://raw.githubusercontent.com/wiki/kunkunkunQoQ/SonicRoute/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0.md)，不是网页工具返回的旧缓存。原 Wiki 仓库基线 `928bc8eee1315aeccae9069aaf21970693084bad`。总体分层、Full 手机能力、Opus/PCM 主路径和自动化枚举数量吻合，但以下细节不完全一致：

| 核对点 | 源码事实 / 修订依据 |
|---|---|
| AndroidAudio 依赖 | 没有 Core ProjectReference，仅链接 AtomicFile；直接声明 NAudio.Wasapi，NAudio.Core 是传递依赖。见 AndroidAudio.csproj。 |
| Core 依赖边界 | net48 条件依赖 System.Text.Json；不能将所有目标描述为没有外部包。见 Core.csproj。 |
| StoreInterop 引用范围 | Full / Lite 普通构建也引用，用于包身份和 WinRT StartupTask；Legacy 不引用。见主项目与 AppRuntimeIdentity。 |
| 原生组件边界 | 托管 COM 声明和托管 Opus 不代表整个 Full 没有原生 DLL；VirtualDisplayRuntime.files.json 列有 SDL / FFmpeg 组件。 |
| 应用路由参数 | AudioPolicyConfig 的委托与调用使用 PID，而非进程路径。见 SetDefaultEndPoint。 |
| 电平可见性 | 滚出视口停止峰值读取，保留会话订阅图，避免滚动时重复重建 COM；关闭采样才整体释放。见 AudioMeterService.UpdateVisibleTargets / Run。 |
| 虚拟屏状态 | Missing 显示恢复提示，并不立即关闭整个客户端；音频按 displayOwned 所有权释放。见 VirtualDisplayPresentation。 |
| IPC | 普通发行的 CommandEdition 为 Full / Lite / Legacy，商店为 Store；短命令是 sr.cmd + PowerShell，而非 sr.exe。请求和响应都有 4 字节长度前缀。 |
| 捕获与检测兜底 | PC 进程捕获存在活动时的回调不支持兜底；自动化和前台通知也保留条件恢复。没有 Windows 10 自动降级为全局回环的路径，不能写成全程没有定时等待。 |
| SmartVolume 参数 | 原文部分系数来自本地未提交调参；v1.30 正式标签的曲线仍为 volume × (0.5 + 0.5 × volume)。不得把本地参数当作正式算法。 |
| 性能与验证 | 移除缺少可追溯记录的整版内存 / 唤起数字及“纳秒级”等表述；ReadyToRun 与 Native AOT 区分。历史 118 项不扩展成新的真机或虚拟屏验收。 |

本轮用户后续授权同步优化 Wiki，因此“07-技术实现”已由 582 行精简至 170 行；其他 Wiki 页和 README 未改。Wiki 提交 `5bd32c90dcbf842ad26ed4576f0930e207a465aa` 已推送，远端 master 与精确 Git blob 核对通过，网页读取已返回新内容；另一个 HTTP 客户端仍命中旧缓存，该差异保留在本地记录。

## 必要验证

- Full + Legacy Release 解决方案构建成功，Lite Release 构建成功；仍有可空性和平台兼容警告。
- 五个产品项目的七个目标 / 版本组合：MSBuild 版本与实际生成文件版本均为 1.31.0.0；Full / Lite 条件引用保持隔离。
- `AudioPathChecks --application-audio`：net10、net48 各 6 项。实际编译公共入口、只替换 Windows COM 边界；两应用三会话读取各应用代表状态一次，写三会话，覆盖全静音、未知状态、部分失败、空列表及取消。
- `AndroidAudioChecks`：120 项离线断言，包含身份索引在模式替换、UID 变化、移除和 StopAll 后的正确性。
- `AndroidUiChecks --speaker-routing`：35 项离线 UI 断言，包含公共音量步进、限幅、静音切换、未知身份、路由控制保留与退出语义。
- 定向查找微基准：同一进程、两条模拟活动源、250,000 次操作，每条路径预热后测 5 轮。线性查找耗时中位数 12.709ms、当前线程临时分配 30,000,040 字节；索引查找 3.628ms、40 字节，五轮校验值均为 250,000。仅代表该查找路径，**不代表整版内存或 CPU 降幅**。

已有夹具落后于多设备实现，初次检查保留失败记录：目录夹具仍检查旧单文件路径；音量夹具用单应用键读取按设备保存的设置。现已改用设备目录文件与设备+包键，未改变生产缓存或持久化行为。音频路径夹具补齐已有 AppInfo 源文件依赖与公共入口命名空间。

本地原始记录位于父容器 `outputs/architecture-1.31-20261105/`。未连接手机，未进行新真机听音、原生 ARM64、商店安装或整版长时性能测试。

## 后续接手

- `AndroidPcmSource.cs` 接手前的调参原样保留，SHA-256 为 `D01B47E01F40558332EC4C6283F25D553D52BC8229F718C2A74669EC6BA561EA`；不能混入本轮架构提交或称为正式包参数。
- README 仍由另一位 AI 维护，本轮不执行生成器重写它；正式发行说明、v1.30 标签、运行版及商店包均保持原状。
- 发布 1.31 时再按干净的已提交源码构建、绑定收据、运行发行检查；不要将本轮普通编译结果冒充官方单 EXE 发布包。
