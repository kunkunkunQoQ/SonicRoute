# R10　WPF 1.36 到 Native AOT 1.37 的配置兼容性

基准为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。本页记录冻结源码清单、1.37 实际读写入口和补充存储验证。真实 Full/Lite 升级的输入、原始 WPF 文件哈希、首次加载与回退结果以交付目录 `r10-evidence/upgrade-report.md` 为准。下文的“夹具”仅表示合成数据的存储层实测。

## 配置根、进程和系统身份

| 启动方式 | 1.36 及 1.37 的数据与身份 | 检查范围 |
|---|---|---|
| 正式 Full、Lite，含便携解压 | 均使用 `%LOCALAPPDATA%\SonicRoute`。移动 EXE 不移动用户数据。正式 Full 互斥/消息后缀 `_Full`，Lite 后缀为空，本地命令版型为 Full 或 Lite。路径不再默认包含 NativeCandidate。 | 冻结 `SonicRoute.Core/AppDataRoot.cs`、`SonicRoute/AppRuntimeIdentity.cs`，Native `Protocol/CandidateEnvironment.Open`、`Program.Start`。真实用户目录未读取和改写。 |
| WPF / Native 直接隔离 | `SONICROUTE_DATA_DIRECTORY` 为专用绝对本地目录，`SONICROUTE_INSTANCE_ID` 为独立 ID。升级时进程依次使用同一隔离 ID，保证命令版型和执行历史子目录也一致。Native 可由单独的数据目录派生隔离 ID。 | 真实升级使用直接隔离。测试前清除上一组的两个 `SONICROUTE_CANDIDATE_*` 变量。 |
| 既有候选启动器 | 显式 `SONICROUTE_CANDIDATE_ROOT`、`SONICROUTE_CANDIDATE_ID` 解析成 `<root>/SonicRoute.NativeCandidate/<id>/<Full或Lite>/data`。候选包增加 Store 前缀。诊断控制通道仅在隔离实例开放。 | 路径重叠、受保护目录、重解析点由 `CandidateEnvironment` 检查。旧候选目录不会因正式版启动而移动、覆盖。 |
| 正式 Store / MSIX | 使用与发布版相同的逻辑应用数据路径，交由 Windows 按包身份虚拟化。冻结包身份为 `8B44F47E.SonicRoute`、Windows.FullTrustApplication，命令命名空间 Store、StoreLite；PhaseOne.Validation 包始终隔离。 | `packaging/Store/AppxManifest.xml`、`StoreInterop/NativePackageIdentity.cs`。实际重定向/启动任务/更新跨包验收须使用相同正式包身份，未安装包测试。 |
| 普通启动、主窗、面板、规则 | 启动显示策略不改变数据位置。相同版型先获得冻结互斥体；后运行实例用冻结激活窗口及注册消息唤醒主实例，冷启动转交有五秒上限。规则命令使用同版型、会话和当前用户的命名管道。 | `AppRuntimeIdentity`、`HostStartup.InitialView`、`Native/Product/Program.cs`、`Protocol/ProductInstance.cs`。Full/Lite 共用主设置，但不共用实例和执行历史。 |
| 开机启动、命令别名、更新 | 正式非打包使用冻结的 HKCU Run 值 `SonicRoute`，已存在的路径随 EXE 搬迁修复，尊重用户在系统删除的登记。Store 由 Windows StartupTask 管理。短命令文件使用 `%LOCALAPPDATA%/SonicRoute/Command`、原用户 PATH 逻辑。隔离运行不注册用户短命令，隔离非打包以 profile 私有 `autostart.enabled` 表示启动测试开关。更新工作进程沿用冻结的目标/包校验。 | `PortableStartupRegistration`、`RuleCommandLine.Registration`、`HostViews.RepairAutoStart`、`Protocol/ProductProtocol`。用户注册表、PATH、正式更新安装均未用于实测。 |

## 主配置字段矩阵

冻结 `SonicRoute.Core/ConfigService.cs::AppConfig` 定义字段、大小写、类型和构造默认值。两代产品均使用 `config.json`。1.37 采用 `PersistenceJsonContext`，使用原属性名，日期按 System.Text.Json 的 ISO 形式保存。普通编辑以 `ConfigService.TryCommit/TrySave` 提交；磁盘替换先保留 `.bak`。同一进程按快照版本排序。Full/Lite 同时更改共享主文件时仍沿用冻结的整份快照语义，未对双进程同时编辑作合并保证。

| 范围 | 完整字段 | 语义与检查 |
|---|---|---|
| 启动、更新、注册 | `StartMinimized`、`StartPanelOnStart`、`AutoStart`、`AutoStartStore`、`AutoCheckUpdates`、`IgnoredUpdateVersion`、`LastUpdateCheckUtc` | 注册/系统实际状态与本地偏好分别读取。时间是 DateTime，缺省为 MinValue。 |
| 语言及主题 | `Language`、`ThemeMode`、`Accent`、`BackgroundOpacity` | 空语言由系统检测；旧语言代码、主题模式和颜色字符串沿用原解析逻辑。两版真实 WPF 样本包含非默认语言、颜色及透明度。 |
| 设备、应用身份与排序 | `HiddenOutputDevices`、`HiddenInputDevices`、`DeviceNames`、`AppNames`、`AppIcons`、`DisabledAutoSwitchApps`、`HiddenPanelApps`、`DefaultAppMode`、`FixedAppName`、`LastUsedAppName` | 名单是字符串列表，别名/图标是字典，集合缺省为空。应用和设备仍以各自稳定身份索引。应用列表本身依业务数据排序，冻结 AppConfig 没有单独保存显示列表顺序的字段。 |
| 主窗口及快捷面板 | `MainSidebarCollapsed`、`QuickPanelHeight`、`QuickPanelPosMode`、`QuickPanelCustomX`、`QuickPanelCustomY`、`QuickPanelShowAllOutputVolumes`、`PanelOutputVolumesRetainedOnly`、`QuickPanelShowPhoneAudio`、`ShowAppPeakMeter`、`PanelChangeSystemDefault`、`QuickPanelUseLegacyAppRows`、`QuickPanelCompactShowOutputName`、`QuickPanelShowOutputName`、`CollapseDeviceSections` | 面板高度默认 450、位置模式 default、坐标 -1；保持缩放、坐标和选项含义。主窗没有另一份持久化窗口尺寸文件。 |
| 输入、音量与热键 | `Hotkeys`、`VolumeStep`、`TrayWheelEverywhere`、`TrayWheelPaused` | 快捷键是动作名→快捷键字符串的字典，显式空值代表已清除的按键。音量步长缺省 4。实际系统音量与路由见独立状态表。 |
| 自动化与实验开关 | `FavoriteRuleIds`、`AutomationRuleOrder`、`ExecutionHistoryEnabled`、`ExecutionHistoryLimitMb`、`ExperimentalUnlocked`、`ExperimentalMode`、`BackgroundPollingFallbackEnabled`、`ExperimentalMic`、`MicInPanel` | 规则标识、收藏、顺序原样保存；动作正文和执行历史分别存储。实验状态和已解锁标志各自恢复。 |
| OSD | `OsdPosition`、`OsdOffsetX`、`OsdOffsetY`、`OsdCustomX`、`OsdCustomY`、`OsdCustomPhysicalX`、`OsdCustomPhysicalY`、`OsdWidth`、`OsdVolumeVisualEnabled`、`OsdFadeInMs`、`OsdFadeOutMs`、`OsdFontScale`、`MicMuteOsdPersistent`、`MicMuteOsdTrackInputMuted`、`MicMuteOsdDefaultInputPersistent` | 显式保存的默认麦克风常驻值优先；缺失时继续由原先两个兼容标志推导。物理自定义坐标保留可空语义。 |
| AirPlay／版型间保留值 | `AirPlayReceiverName`、`AirPlayVideoDecoder`、`AirPlayRequireCode` | 名称为字符串，解码选择为整数，要求配对码为布尔。Lite 保留同一模型可识别的字段，接收业务只在 Full 创建。 |

## 独立文件与系统状态矩阵

以下相对路径均从上述数据根开始。涉及移动设备的公开标识只在表内说明类型和归属，证据样本使用合成身份。

| 文件或状态 | 冻结读写入口 → Native 对应入口 | 数据语义、处理方式与证据 |
|---|---|---|
| `config.json`、`config.json.bak`、导入/导出文件 | `ConfigService`、`AppIconFiles` → 相同共享服务、`ConfigService.Recovery` | 1.37 在普通读写保留未知 JSON 键及子树。仅显式导出打包的 `AppIconAssets` 不跟随日常写盘，导入时由原资源导入逻辑验证。WPF 冻结程序能忽略这种额外键，回退后若继续使用旧程序保存，它可能丢弃它不认识的扩展。 |
| `Icons/*.png`、`Lang/*.json` | `AppIconFiles`，`L10n.ExternalLangDir`、`LanguageFiles` → 相同实现 | 只接受原名规则的受管图标，配置映射与导出载荷分离。九种语言内置，附加/覆盖语言外部文件使用原 code、native name、自定义名称字段，UTF-8。启动没有格式转换。 |
| `Automation/*.json`、历史 `AutoRules` | 冻结 `AutoRuleStore`、`Models/AutoRule` → 同名服务及 `StoredRuleEnum` | 包含 ID、启用、名称、热键、触发、计划和星期、上次执行、动作/首动作旧镜像、目标、音量、延迟、停止条件、OSD 文本、程序路径/打开方式/启动模式。保留规则、步骤及 launch item 扩展字段。接受合法枚举名称及数字，写回冻结的数值表示。单规则损坏不连带重置其他规则。若真正旧式内嵌集合须拆分，将原文件复制到 `config.json.pre-automation.bak`，独占迁移标记和规则 ID 供失败后续跑；已有正式规则目录优先。 |
| `ExecutionHistory/<CommandEdition>/recent.jsonl`、`previous.jsonl` | `AutoRuleService`、`ExecutionHistoryStore` → 共享实现 | UTF-8 逐行事件，含触发、步骤结果等。按版型、隔离 ID、用户配置限额轮换；保留旧行。原生历史 JSON 上下文此前只生成写入快速路径，导致读每条记录时抛 InvalidOperationException。现生成同一 DTO 的读写契约，保持原有文件内容与编码。 |
| `playback-outputs.json`、备份；Windows 音频路由 | `SavedPlaybackStore`、`LocalAppOutputMirrors` → 同一存储、`PcRoutingApi.MirrorOwnerName` | 只有多输出需要额外存储应用→Outputs/Preferred；保留每个应用的扩展字段。同根跨进程文件互斥下读回后合并应用级修改。正式进程镜像所有者锁恢复冻结 `Local\SonicRoute.AudioMirror.<pid>`。系统默认端点、实际端点/会话音量、静音及单路应用策略仍由 Windows 音频服务负责。夹具实测既有路由改动后保留未知字段。 |
| `android-phone.json` 及备份 | 冻结 `AndroidAudioHost.PhoneStorePath`、`AndroidPhoneStore` → `PhonePreview`、相同存储 | v1 单机、v2/v3 多机档案；选中 ID、设备身份和上次连接、固定应用、智能音量例外、接收目录、手动断开、归档/移除档案。读取已知格式，首次修改旧格式保留 pre-connection-management 备份。索引及档案扩展字段往返。旧 Native 候选若只有 `android-phones.json`，仅在正式文件缺失时复制并保留源。夹具只改合成离线档案。 |
| `android-audio.json` 及备份 | 冻结 `AndroidAudioHost`、`AndroidAudioSettingsStore` → `PhonePreview.Settings`、相同存储 | v1–v3，按设备/应用保存音量、输出、恢复多设备播放意愿、虚拟显示及窗口尺寸；按设备保存编解码器、音视频码率、FPS、延迟。新读写保留容器、应用设置、编码流设置扩展。档案绑定迁移有独立回退副本。实际声音/编解码待真机。 |
| `phone-app-catalog.json`、`phone-app-catalog.json.<SHA256>.json` | 冻结 `AndroidAudioHost`、`AndroidAppCatalogCache` → `PhoneDevicePageView`、同一缓存 | 以设备键哈希分隔已发现应用与缓存图标，仍可懒读单机旧缓存。固定应用和是否可管理归档于手机档案；目录缓存允许重新生成。 |
| `phone-clipboard.json` | 冻结 `PhoneClipboardSync.Saved/UpdateProfilePreferences` → `ClipboardPreferences`、`PhoneClipboard.Reconcile`、`PhonePreview.RestoreFull` | v1 EnabledDevices 和 ArchivedDevices。移除时收存稳定身份、同一物理设备重新登记时恢复；上限 32 台同时启用，几乎同时产生的编辑沿用一次合并保存。仅有未恢复归档时不为剪贴板发起发现。保存保留其他根属性；不支持版本/坏文件保留原件并反馈保存错误，不写剪贴板内容。夹具已实测归档、扩展和未知版本保护；离线回调实测删除时归档、相同序列号但硬件身份不同不恢复、原硬件身份重新登记后恢复。真机收发待验证。 |
| `android-file-receive-folder.txt`、传输及虚拟屏 | 冻结 `PhoneDevicePage.AndroidFiles`、`ReceiveDirectorySettings` → `PhoneDevicePageView`、共享类 | PC 收件目录为单独文本，安卓接收目录在对应档案。正在传输的队列和连接为运行时任务；退出后没有冻结的队列恢复文件。虚拟屏尺寸在 android-audio.json。 |
| 配对和接收器 | 冻结 `AdbKeyStore`、`ArbitratedAdbTransport`、`AirPlayReceiver` → 相同后端 | 正式 Direct ADB 保留 OS 用户的标准 ADB 密钥位置。显式隔离实例新生成的 Direct ADB 密钥位于隔离 data/adb-keys；已有运行的外部 ADB 服务仍有自身授权身份。AirPlay 码偏好来自主配置，接收器会话继续使用原密钥实现；不读、复制或输出真实私钥及 PIN。未接入真实设备。 |
| `PhoneRuntime/`、`Updates/`、`Logs/diagnostics.log`、短命令 | `AndroidRuntimeBundle`、`UpdateService/UpdateInstaller`、`ExceptionDiagnostics`、`RuleCommandLine` → 共享基础设施 | 运行库解包/校验、包下载、事务计划与上次更新结果、诊断日志均沿用职责；不将临时包/日志当作用户配置导入。原 `sr.cmd`、`sr-client.ps1` 和帮助文件仅在正式模式按需写入。 |

## 读取、恢复和回退边界

- 缺失成员使用冻结构造默认，存在的有效标量、字典和数组按原字段名读回。显式 null 的字符串和列表按现有用途恢复为各自默认值；可空 OSD 坐标保持 null。主题等字符串不做全局大小写改写，规则名称枚举使用不区分大小写的解析供兼容输入。
- `TryRecoverFields` 走生成的属性类型元数据。语法完整但某个字段类型非法时，仅将该字段回退；列表、字典中可读取的文本成员继续保留。原始字节必须先写入并比对 `config.json.corrupt-fields-<SHA256>`，失败时暂停保存。损坏字段和恢复位置由启动提示显示。
- 整个 JSON 无法解析时，沿用隔离损坏文件、读取上一份有效备份的路径。成功隔离并找到可读备份会重新原子写入主文件，使下一次启动继续读到恢复值；不能安全留存原件时阻止普通保存。原子写入及备份使用 `AtomicFile`。全新的或有权限问题的用户实际目录未用于试验。
- 独立版本化文件按各自协议处理。手机档案/音频主文件和备份都无法读时会保护源文件；多路输出可回退到备份。兼容承诺覆盖冻结版本实际字段，以及表中已明确保留的扩展层级，未把任意未来版本的嵌套设备对象作为已验证契约。

## 验证索引

| 级别 | 证据及范围 |
|---|---|
| 冻结源码审查 | 上述存储入口直接对照冻结 SHA；主配置每个公开持久化属性均已归类。冻结中便携与 Full/Lite 均无另一个 exe 同目录配置发现逻辑。 |
| 自动化存储夹具 | `r10-evidence/compat-codec/check.ps1` 从全新合成数据目录运行，各读取阶段使用独立 pwsh 进程和 .NET 10 Full Release 产出的程序集。六个场景见 `result.json`，另有 `clipboard-check.json`。覆盖数值和集合坏值局部恢复、逐字节原件、中文扩展、正常保存及新进程重读、主文件损坏后备份重建和再次启动、旧规则迁移与枚举、路由、离线档案、手机音频与剪贴板。无真实业务、系统输入、手机传输结论。 |
| 冻结运行历史原件读回 | Lite 的历史目录 `R10d57c453c4c1647d4_Lite` 与启动 ID 对应，原样读取同一份 397 字节 UTF-8 recent.jsonl。修复前零行及 InvalidOperationException，修复后一行、LastError 为空，规则名、时间、动作、步骤数与原记录相符；文件 SHA-256 保持不变。见 `r10-evidence/compat-codec/history-wpf-lite.json`。 |
| Full 应用播放路由读回 | 新建静音进程，真实 Windows render 策略只读。用非流式合成手机和替代静音租约验证电脑、仅手机、系统默认与手机同时选中、退回仅手机、退回电脑，读到的选中项数依次为 1、1、2、1、1，两次独占各获取并释放一次租约。修复统一当前会话的读回，不改变已有播放保存/恢复格式；Windows 与实际手机混合播放和跨重启恢复仍待真机。证据 `r10-evidence/compat-codec/mixed-readback.json`。 |
| 正式升级与回退 | 两份冻结 WPF EXE 实际通过 UI 生成隔离原件，源文件清单及哈希在 `r10-evidence/frozen/{Full,Lite}/manifest.json`。产品系统输入、重启、回退数据、各项是否通过及未覆盖条件记录在 `r10-evidence/upgrade-report.md`。 |
| 待实机 | 正式 Store 安装/更新和系统启动任务、真实用户注册表及 PATH、外部 ADB/手机认证、物理设备恢复及剪贴板收发、AirPlay 配对、跨版本系统音频端点与双进程同时编辑，须相应环境及用户确认。 |
