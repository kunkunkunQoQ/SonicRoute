# SonicRoute 构建版本

普通构建默认为完整 SonicRoute；Lite 和 Legacy 不包含手机功能。本分支仅维护 .NET 10 和 .NET Framework 4.8，不再构建 NET8。

| 属性 / 工程 | 发行文件 | 手机功能 |
| --- | --- | --- |
| `SonicRouteEdition=Full`（默认） | `SonicRoute-v<版本>-x64.exe` / `-arm64.exe` | 手机面板、配对、音频、独立虚拟屏 |
| `SonicRouteEdition=Lite` | `SonicRoute-v<版本>-Lite-x64.exe` / `-Lite-arm64.exe` | 编译、资源、依赖和发行文件均排除 |
| `SonicRoute.Legacy` / .NET Framework 4.8 | `SonicRoute-v<版本>-Legacy-x64.zip` | 编译和资源均排除，保留原多文件兼容布局 |

Full 和 Lite 都是单个 EXE，依赖匹配架构的 .NET 10 Desktop Runtime。完整版本不增加 Full、Android、Phone 或 Lite 后缀。没有启用程序压缩或 WPF 裁剪；Release 保留 ReadyToRun，并统一关闭调试符号发布。

```powershell
./packaging/Build-Edition.ps1 -Edition Full
./packaging/Build-Edition.ps1 -Edition Lite
# ARM64 仍是实验支持；ADB / 视频工具作为独立 x64 进程运行。
./packaging/Build-Edition.ps1 -Edition Full -Architecture arm64
```

发布目录沿用 `outputs/editions/Full-x64`、`Lite-x64` 等固定路径；中间文件和发布目录按版本类型隔离，不逐版本另建产品目录。已有 Full 目录的旧文件保留，发行文件和更新清单只选择 EXE，不把它们打进去；Lite 拒绝混入旧手机目录。运行中的程序和链接目录不能覆盖。直接 publish 使用 win-x64 / win-arm64 profile 也得到内嵌组件的单文件程序，无需再复制 AndroidRuntime。

Full 的原生组件以未压缩资源内嵌于 AndroidAudio 程序集。`AndroidRuntimeBundle` 统一管理准备、校验和缓存：只有首次请求 ADB、音频/目录或视频功能时，才在工作线程准备对应组件；构造服务不读取完整组件，不释放文件。缓存以工具内容寻址，托管代码变化复用原缓存；新进程首次使用校验缓存，当前进程不重复扫描或写入，不增加闲时轮询。原生 EXE/DLL/JAR 需要实体文件才能执行，因此首次使用会生成应用私有缓存，这些缓存不属于分发文件。

缓存默认位于 `%LocalAppData%/SonicRoute/PhoneRuntime/<工具标识>/`；显式 SONICROUTE_DATA_DIRECTORY 隔离运行时，缓存也位于该数据目录下。运行中的旧工具与新工具不相互覆盖。音频服务仍单独校验固定 UID 隔离服务器，视频服务器不能替代它。修改原生工具后须同步仓库内二进制、VirtualDisplayRuntime.files.json 与 AndroidRuntime.files.json，再进行对应组件检查。

所有第三方许可原文一并内嵌，完整界面的设置页提供“许可与第三方声明”按需查看入口。无需随包携带许可证零散文件。Lite 与 Legacy 不引用 AndroidAudio、QRCoder、Concentus，也不包含手机页面的 BAML 或内嵌原生工具；电脑音频共享实现继续保留。System.Management 同时用于电脑进程事件，不能当作手机依赖删除。

Full / Lite / Legacy 的单实例、激活、命令和更新身份各自独立；普通配置位置不改变。仅显式 --android-lab 使用独立 LabData/实例。Full 更新清单仅允许 SonicRoute.exe 一个文件，按完整 EXE 校验、暂存和回滚；Lite 仍走单 EXE，Legacy 仍走其原 ZIP 白名单。Store 固定使用 Full x64 单 EXE，验证时拒绝 Lite，不自动发布。

验证按当次修改记录，编译和离线资源检查不能当作真机、ARM64 或 Store 验收。当前交付路径和范围见 Lab 的 outputs/current-full-build.json 与 AI_CONTEXT.md。
