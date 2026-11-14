# 开发工作记录

后续AI每轮追加记录，并同步 `DEVELOPMENT-HANDOFF.md` 和本地父容器的当前交接。记录区分已实现、已验证、待验证和明确不做；不能用模拟检查代替真实CPU、音频或设备验收。

## 2026-11-14晚：闲时内存/资源优化提交与 1.32 微软商店包

- 收口提交 31 项既有代码/检查改动及交接文档到 GitHub `origin/master`：涵盖 ADB 池化与 64 KiB POH 传输缓冲、轻量 USB 设备探测与闲时连接释放、D3D11 预编译着色器与 COM 闲时卸载、目录缓存与图标/封面按需缩放解码、8 秒闲时 OSD 窗口释放、取色器/主题字典按窗按需加载、`WM_DEVICECHANGE` USB 剪贴板防抖恢复及 Full 闲时无活动手机任务时的 LOH 压缩门禁（不裁剪 Full 全进程工作集）。
- 复用此前已完成的 `outputs/mem-verify-20261114-final` 定向离线验证及 `测试版/` 三版构建（Full x64 11,191,624 字节 `A4677986…FEAB87`、Lite x64 6,019,637 字节、Legacy x64 ZIP 982,241 字节）与 3 项 Full payload 检查，新增 `node docs/readme/generate.cjs --check`（10 份 README 一致）与 `git diff --check`。
- 基于最新 Full x64 发布目录 `测试版/Full-x64/` 通过 `packaging/Store/Build-Package.ps1` 构建并校验 `商店包/SonicRoute-v1.32.0.0-x64-unsigned.msix`（5,192,439 字节，SHA256 `D10814360EE75379E62F84921A87470F5BEF15D8364C0F2E89294C0ECA1D747D`）：MakePri/MakeAppx 成功、Store Identity/Version `1.32.0.0`/x64/未签名及包内 `SonicRoute.exe` SHA256 与最新 Full 一致。未修改产品代码、未打断活动进程或共享 ADB、未签名/安装/上传商店，未创建 GitHub Release/tag。

## 2026-11-14后续：默认输出自动跟随、备份与自研合入

- 修复手机整机/单应用选择“系统输出”后不跟随默认播放设备变化的问题：共享原生默认输出通知，只重绑电脑播放器，不重启捕获/解码或改OutputId偏好；保留音量、静音和智能音量。默认通知按实际Render/Multimedia角色过滤和去重，快速变化在原串行操作中合并，显式设备不跟随；最后默认输出会话停止即释放原生订阅，无后台轮询。
- 按用户要求，先完整备份外面的当前项目到父容器保留资料/当前项目-合并自研前-20261114-114522，872文件含Git/忽略文件，逐文件Hash核对；许可说明的随后更新也补进备份，早先版本另留本轮证据。再从共同基线82176d1按文件合入自研：52新增、66更新、23退役文件移除；保留外部Git、构建输出位置和历史许可说明，原自研保留不删除。并行产生的86874dd说明提交保留作为本次提交父基线。
- 本轮18项默认输出+9项原显式输出检查通过，合计27；检查编译0警告/0错误、Full x64 ReadyToRun构建成功（5条可空引用警告，无错误）。首轮夹具因CaptureState类型重名编译失败，补显式别名后通过，初始日志保留；未重跑此前59项、应用全套、Lite/Legacy/Store或真机/性能检查，没有切Windows真实默认输出或播放测试声音。
- 最终包outputs/system-output-merge-20261114/editions/SonicRoute-v1.32-x64.exe，11,117,896字节，5E2A7E…C9A20，版本仍1.32.0.0。PCM D01B47…561EA、普通运行包及活动自研v6保持；没有停止产品/共享ADB、启动新包或安装。用户要求提交并推送GitHub，提交范围包含自研合入与本轮修复，实际Hash/推送回读由58号和delivery-receipt记录；无Release/tag/Store发布。

## 2026-11-14：自研六项修复与Full本地包

- 用户明确要求修复56号审查四项bug，继续C#自实现、不增加外部依赖，并追加最大化不保存窗口/显示尺寸、退出整机恢复原应用接管。目标仅本自研树self-impl/stage1-4 /8891923，原128条WIP保留；维护线61bd13b、原PCM D01B47…561EA、普通运行包D8DCC9…7C83保持。
- 为ADB接收/关闭共用flow gate；USB失败保留offline、显式刷新重试，事件通知不反复重试；TLS后握手延续原上下文，并用Windows SCH_CREDENTIALS v5真正启用TLS1.3；窗口创建取消同步关闭意图，晚到HWND所属线程销毁。无新增库或外部运行程序。
- 复用一个现有Gemini只读核对最大化的WindowSize/DisplaySize保存路径，主代理实现双门禁和关闭后晚到帧保护，保留当次最大化适配。整机接管仅保留原应用意图及偏好，完全释放旧策略；退出后有序恢复，明确归还不复活，断开/停止全部/退出不恢复，无新闲时轮询或重复捕获。
- 36项修复检查+23项原设备音频检查通过，共59项。TLS1.3-only本机对端实际触发后握手分支并保持上下文；隐藏HWND/消息线程全部回收。检查编译0警告/0错误、Full x64 ReadyToRun发布通过；未扩跑应用矩阵、真机听音/USB无线、跨DPI或性能基准。测试夹具初始证书错误及旧凭据TLS1.3失败日志保留为历史证据，最终日志通过。
- 最终包父容器outputs/self-impl-fixes-20261114/editions/SonicRoute-v1.32-x64.exe，11,031,880字节、FA98C0…E06DD，版本1.32.0.0。运行中旧v5/PID27480保留，不覆盖、不自动启新版、不重启共享ADB。57号记录、根入口及delivery-receipt同步，未提交/推送/合并/Release/tag/Store；接手不重复已通过检查。

## 2026-11-12：1.32 与托盘默认播放子菜单重叠修复

- 开发版本升至1.32（数值版本1.32.0.0），生成AI索引并保留公开下载版本1.31。
- 通过真实菜单HWND复现长设备名使靠右的一级菜单扩展、子菜单停留旧坐标的问题；共享`ConvenienceMenus`在打开/尺寸变化后按最终布局更新锚点，保留原生弹出方向，关闭后派发回调直接退出，无新增后台轮询或定时器。
- 私有桌面12场景/72项通过，覆盖短名、20/21字母、10/11汉字、超长名及重新展开；已查看修复前后截图。10份README、版本元数据、diff检查及Full x64发布通过。定向构建791条既有文件警告，发布5条可空引用警告，无错误；未执行全套/音频切换/跨DPI/其他发行类型构建。
- 正常入口更新为1.32 Full，27,211,121字节、D8DCC9…7C83；BA7F2C旧入口已备份。运行中的Store1.31及ADB保持。PCM已提交参数Hash不变；开发基线7f424da。用户后续明确要求提交GitHub，本次提交收录1.32修复、定向检查和交接，复用已通过的验证，不重复构建或测试、不创建Release/tag或上传商店。准确提交号和推送结果由父容器55号记录和收据记录。

## 2026-11-10：GitHub仓库过时说明审查

- 复用现有子代理只读审查，主代理验证并修正README下载/历史状态、Full/.NET10反馈选项、CLI选择器、Full单EXE更新文档、当前CI检查、目标框架/Store/许可事实及Wiki当前使用/资源说明；历史发行档案和备份不改。
- 新增arm64LastRelease并统一生成10份README，按真实发布清单区分1.31 x64、1.30 ARM64和1.29 NET8。删除当前说明中无范围依据的固定内存/延迟保证，修复AI索引尾逗号；收尾子代理发现的Store/Lite误写已纠正。
- README生成一致性、91处下载引用/7个唯一实际资产及源码事实/相对链接/空白/范围检查通过，PCM/Full/MSIX Hash保持。不重建/扩跑应用测试、真机或性能，不改工作流/依赖/原生资源或发布资产。54号记录、最终提交收据和根入口同步。

## 2026-11-09：原PCM调参提交与技术Wiki核对

- 用户明确要求提交唯一剩余的AndroidPcmSource.cs调参，取代此前保留为未提交WIP的要求；内容仍为D01B47…561EA，未再修改参数，补齐现有构建已包含的源码。
- 对照代码修正Wiki技术页源码ref、整机主屏镜像/应用虚拟屏、整体/单包音频、声画设置、按需编码器目录、剪贴板生命周期、20ms/120fps边界和实际发布矩阵。明确v1.31标签旧PCM与Full发布资产中同一调参的来源差异，原标签/发布资产保持。
- 30个唯一源码链接及diff空白检查通过，PCM和Full/MSIX Hash不变；本次不重复构建/应用测试或启动/停止用户程序。父容器53号记录、最终提交收据和根交接同步；主仓库和Wiki分别提交推送，准确结果以收据回读为准。

## 2026-11-08：1.31 文档体系与 Wiki 补齐

- 核对并补齐 1.31 相关的发布文档与知识库：
  - 新建 `docs/release-v1.31.md`，记录 1.31 核心亮点、详细演进、验证范围与资产校验。
  - 更新 `docs/readme/shared.json` 与全部 9 份语言配置中的 `releaseSummary`，更新 `docs/readme/generate.cjs`，重新生成并校验 10 份 README 文件。
  - 同步推送 `SonicRoute.wiki`：更新 `09-版本历史.md`（追加 1.31 条目）、`08-版本相关.md`（下载矩阵）、`Home.md`（正式版标签与场景）、`01-使用指南.md`（主屏镜像、剪贴板同步、编码器与120fps）、`02-功能特性.md`、`07-技术实现.md` 与 `AI.md`。
- 边界：既有 `AndroidPcmSource.cs` SmartVolume 调参继续保留在本地，不纳入提交。

## 2026-11-08：1.31 微软商店包

- 用户要求基于另一AI最新改动构建商店版且少做检查。HEADffc58a0，复用50号Full x64/6457C1…9ED9，不重编译或重跑应用测试；原PCM调参D01B47…561EA保持，本次包包含该WIP。
- 既有Store脚本Full身份/运行时、MakePri/MakeAppx及最终MSIX清单/包内EXE Hash检查通过。交付父容器商店包/SonicRoute-v1.31.0.0-x64-unsigned.msix，12,074,907字节、Hash2816759C…411B6。
- 旧1.30包和正常运行入口保留，未停产品/ADB、签名、安装、上传商店或发布Release/tag。51号记录/机器回执/根入口同步；本轮仅交接文档未提交，商店实际安装升级未测。

## 2026-11-08：友好编码器名称、九语本地化与 24/32/50 Mbps / 120 fps

- 手机详情页音频/视频编码器选择器由裸 Codec ID（如 `c2.qti.avc.encoder`）改为按硬件/软件能力 + SoC 厂商前缀的九语友好说明并括号保留原 ID（如 `高通 · 硬件编码 (c2.qti.avc.encoder)`、`Android · 软件编码 (c2.android.avc.encoder)`）；按硬件优先于软件再于其他排序，保留精确 Codec ID 作为持久化值与 CLI 参数。`EncoderCatalog.java`（API>=29）输出 `"details"` `{hw,sw}` 能力，`AdbClient.ParseEncoders` 兼容无 `"details"` 旧响应，离线已保存配置按前缀规则兜底展示友好说明。
- 新增 `_streamLanguage` 跟踪以支持语言切换即时重建编码器下拉文案、`UpdateEncoderToolTip` 动态悬停完整说明、`EncoderOptionTemplate`（`TextTrimming="CharacterEllipsis"`、`Style="{x:Null}"`）保证窄布局省略截断且不覆盖下拉高亮前景色。画面码率移除 `2 Mbps`、增加 `24/32/50 Mbps`（预设 `1/4/6/8/12/16/24/32/50 Mbps`），帧率增加 `120 fps`（预设 `15/24/30/60/120 fps`）；第 3 列 `Auto` + `MinWidth="80"` 防止 520 DIP 窄布局截断，未增加后台轮询或定时器。
- 重建 `sonicroute-catalog.jar`（20,341 字节、Hash `3256BC6B...B03D`，保留 3 条 Java8 目标提示）并更新 `AndroidRuntime.files.json`。13+8 runtime 清单、九语 838 键（0 重复/占位符一致/HEAD 816 值不变）、`SerializationChecks`（net10）、`AndroidAudioChecks --stream-settings`（12 项）、真机（小米13）`--encoder-catalog`（只读未开流，实测返回 `c2.qti.avc.encoder=Hardware(1)` 等）、`AndroidUiChecks --panel-stream-settings`（29 项 + 520 DIP 浅/深 PNG 目视核验）及 `--settings-localization`（390 项）全部通过；Lite Release（0 警告）与 Legacy net48 Release（46 既有警告）编译通过；Full x64 ReadyToRun 单 EXE 发布成功（27,211,121 字节、Hash `6457C1DE...59ED9`）。
- 用户交付后要求推送 GitHub：本次将 44～50 号累计代码、运行时资产、检查与文档更新一并提交并推送（保留既有 `AndroidPcmSource.cs` SmartVolume 调参 `D01B47…561EA` 在本地工作区，不顺带提交），复用上述已通过检查与构建，不为提交重复构建/扩测；无 Release/tag/Store 或公共 README/Wiki 改动。交付时 `运行版/SonicRoute.exe` 正由运行中旧实例占用，按约定保持不动；新版位于父容器 `outputs/editions/SonicRoute-v1.31-x64.exe`。未测真实 120fps/50Mbps 推流视听、跨 DPI 或 ARM64，准确提交与远端状态见 Git 及父容器提交收据。

## 2026-11-08：面板布局、入场动画与两行手机声画设置

- 按用户截图右移完整窗口应用列表滚动条6 DIP；手机固定应用标题/说明与列表共用白色PhoneCard。手机/额外面板复用已有短入场动画，隐藏/关闭停止动画；额外面板首次音量快照完成后显示，未读值/初始化事件不写入伪0%。
- 新增按手机保存的两行Opus编码器/码率、H.264编码器/码率/帧率；下次对应连接生效，默认保持128K/4M/30fps。编码器下拉首次按需查询MediaCodec并在当前设备页复用，取消随页面生命周期，无新闲时轮询/周期工作。整机主屏镜像与普通应用虚拟屏保持。
- 9库、14UI、一次真机目录只读查询、13runtime/九语824键和Full x64发布通过。修正夹具缺少using、报告字段误名和紧凑下拉对象标签；最终浅/深挂载窗口预览已查看。没有重复全套/native/Lite/Legacy、真实声画/听音/性能或跨DPI验收。
- 正常运行入口已更新BA7F2C…1611，27,190,641字节；旧文件备份。活动旧editions-next/PID19932及011ACAB2 Hash保持，未停产品/ADB、未自动开新版。原PCM Hash D01B47…561EA保持；helper改为724230…B022，server/native不变。49号记录/收据及根入口同步，累计44～49仍未提交/推送，无发布。

## 2026-11-08后续：按用户要求恢复整机主屏镜像

- 用户不再需要整机独立桌面；恢复display0/Flex=0/Mirror=1、等比缩放和仅手机名标题，保留整机音频、手机图标与原单应用虚拟屏。删除桌面UI/接口/任务监听、原生集成、专用检查及文案，手机行模板回原窗口。
- 保留D3D11与实际首帧，不等待桌面事件，不给主屏传副屏专用IME参数，忽略旧桌面分辨率。视频server恢复已校验原D92B5E，保留当前镜像客户端，不重建native或加入轮询；原音频server/helper和PCM调参不动。
- 25库、35UI、2包、Full构建通过；真机有界检查验证主屏实际内容、等比窗口和回收，不启动声音/保存截图。检查已正确区分内部镜像捕获端点和独立交互桌面；无新性能/听音或跨DPI验收，未扩大重复全套检查。
- 新产物editions-next，27,137,393字节、Hash011ACAB2...D114；交付检查时正常入口旧E57F45/PID46824仍运行，未覆盖/结束。48号记录和最新收据同步，原PCM Hash不变，仍未提交/推送或发布。

## 2026-11-08后续：无安装桌面白屏修复及真实画面验证

- 用户反馈独立桌面不能用，实查小米SDK36副屏HOME只有空FrameLayout。用户明确拒绝安装其他桌面；改为电脑端内置应用桌面，共用手机行模板、搜索、主题和目录缓存，选择应用只启动到原自有显示器，HOME回列表，不修改默认桌面或镜像主屏。
- 扩展原OwnedAppMonitor事件监听home/app，提取共享原生Owner定位，处理移动/大小/最小化/恢复/关闭和Alt+F4；关窗取消读取、解绑hook与任务监听，未增加轮询/周期扫描或另一路音频。新手机资源在Lite排除，Legacy显式Page列表不引用。
- 发现早期任务断言通过但Windows截图仍白：手机侧真实计算器正常，本机旧SDL direct3d未显示。整机子进程改为D3D11后最终Windows真实画面通过；不再用任务可见代替视觉证据。最终8项真机、27库、35UI、2包及native检查通过，Full发布/Lite编译通过、13固定组件/9语819键一致，旧816值保留。
- 正常入口已确认空闲、备份并更新：27,170,161字节、Hash E57F4598...BAEA，同产物在editions-next。替换时活动旧editions/441643包保留，最终复查已退出；未杀用户进程/停ADB/启动新版/装APK。原PCM WIP Hash不变，仍未提交/推送或发布。47号记录、根入口和机器收据同步；未新增听音、中文输入或整版性能结论。

## 2026-11-08后续：独立手机桌面、scrcpy图标和首帧就绪

- 用户确认改为独立虚拟桌面，主屏继续另用；设备入口走new-display/系统装饰/local IME，与应用屏相同自由调整自有虚拟屏，继续同一个整机音频来源。不会自动复制/迁移手机已有任务或切换默认桌面，应用多实例依系统/应用支持。
- Build-PhoneIcon从共享Geometry生成native图标，PE程序图标和设备窗口使用手机图标；PNG/ICO均有Hash与提取逐像素证据。改为首帧才就绪，新增无首帧限时错误和九语提示；不新增轮询/重连，复用既有30fps/4Mbps、像素预算和关闭所有权。
- 只读确认小米SDK36提供SecondaryDisplayLauncher，未实际创建桌面或接管新声画。24项库、35项UI、2项包及native自检、13组件清单与九语检查通过，Full单EXE发布成功；无新真机输入/焦点/关闭/性能结论，没有扩展全套测试或Lite/Legacy构建。
- 新测试包在editions固定槽，27,141,489字节、Hash441643...723E。活动普通版23EC2B保留，未停止任何产品/ADB、未自动启动新版。仍未提交/推送，原AndroidPcmSource.cs调参Hash不变；根入口、46号记录与phone-desktop收据同步，无发布操作。

## 2026-11-08后续：手机图标、整机标题栏与等比缩放

- 根据用户截图，整机卡片/音频来源/拖拽统一复用冻结手机轮廓图，底部选择器也引用同源Geometry；标题栏隐藏图标只保留手机名。整机窗口按当前画面比例约束八边、程序尺寸与最大化/恢复，旋转时更新比例，避免额外黑边；不改变手机分辨率。
- 复用既有native消息钩子和所属线程绘制，未新增轮询/定时器；普通VD自由缩放和IME保持。只重建客户端，新增ClientOnly模式复用原固定视频server；更新组件与嵌入Hash清单。
- 48项native镜像断言、旧自由缩放/IME、35项UI/图标、19项库参数及2项包检查通过，Full/Lite编译通过；没有真机/跨DPI/黑边可视验收或CPU/内存测量。编译期类型重名与测试required字段已修正，原生旧flex夹具保留一条未使用函数警告。
- 在正常入口空闲时更新为23EC2B...8154的Full单EXE，27,137,393字节；保留运行中的旧editions/E8F25包，未结束进程、启动新版或停止ADB。固定新产物在editions-next，旧普通版已备份。仍未提交/推送，原AndroidPcmSource.cs WIP Hash不变，无发布操作。根入口、45号记录和mirror-window收据同步。

## 2026-11-08：手机名称整机入口、整机画面及性能边界

- 用户要求应用列表顶部增加与应用同样式/操作的整机声音入口，并确认拖出后显示整个手机画面；后续指定入口直接显示底部设置的手机名称。实现复用现有卡片、拖拽/键盘、音量/静音/输出和返回；名称随改名/换机同步。
- 仅明确的保留设备身份启用无包过滤playback，普通UID解析失败不放宽范围。同手机整机/单应用先释放后切换，重复操作复用，其他手机保留；画面借用的音频按来源所有权释放。视频复用主屏display0与原30fps/4Mbps上限，不创建新的虚拟显示器或启动指定应用。手机回放轨道声明不可捕获并重建helper，避免全局回采路径；真机效果待验收。
- 不新增后台轮询、定时清理或持续重连；闲置新增行不创建ADB或扫描，单窗口缓存同一个设备行模型，复用原事件和可见刷新。没有整版性能测量，不把离线调用次数当作真实CPU/内存降低。
- 208项离线定向断言通过；九语旧816值未改、每语一条新说明，12份runtime与清单一致。Java/D8和Full x64单EXE构建通过；没有重复Lite/Legacy构建或旧670项全检，也未连接手机/人工听音/运行镜像。开发夹具的导入、媒体样本和闲置断言时机问题修正后通过，见父容器44号记录。
- 最新固定测试包27,133,297字节、SHA256 E8F25C...1C065，普通版仍活动且未替换，产品/共享ADB未停。本轮尚未提交/推送，保留原AndroidPcmSource.cs调参Hash D01B47...561EA；无发布操作。同步开发交接、根入口、44号记录和device-audio收据。

## 2026-11-07：设置页崩溃修复，完整复核Gemini本地化

- 用户在交付后明确要求提交；本次纳入九语资源、本地化代码、设置/刷新修复、回归检查及交接，保留旧SmartVolume WIP在工作区，不顺带提交。复用已完成的670项检查和三版编译结果，不为提交重复构建或扩测；远端状态以Git及父容器提交收据为准。
- 用户反馈新测试包点击设置退出，随后要求完整检查Gemini修改。Windows1026异常与新增隔离设置BAML Loaded回归均复现同一只读L10n双向绑定错误；将新开源许可Run.Text绑定设为OneWay，翻译仍可实时更新。上轮仅静态简查及编译没有覆盖这个实际加载事件。
- 修复挂起手机详情页切换语言后缓存应用描述/操作名称未重刷；快速面板来源说明增加Subtitle变更通知，并只在语言/手机名称变化时刷新相关翻译。移除一处重复峰值绘制、旧虚拟屏字符串通知处理器及局部冗余空行。未改变音频接管、默认设备、虚拟屏归属、剪贴板协议或持久化语义。
- 逐组复核36份Gemini修改（原37份工作区差异排除旧SmartVolume WIP），九语各新增191个Phone资源、原625个键的值未改写。670项定向离线回归通过；Full/Lite/Legacy编译通过。Lite790条、Legacy46条警告保留，Full最后增量发布无警告输出，不能据此宣称全量无警告。没有手机连接、真实剪贴板操作、人工可见整窗点击、听音或整版性能验收。
- Full x64正常入口更新为27,125,105字节、SHA256 `BF7749876034BB8DB74DC1020FDC174CC9A006BD4CE431CFC916A82FB77CD2E7`，未自动启动。构建仍包含原有AndroidPcmSource.cs调参，Hash保持D01B47...561EA；无Release/tag/Store，README/Wiki不改。详见父容器42号记录和localization-review-receipt.json，构建时未提交状态由后续提交收据补充。

## 2026-11-06：每设备独立剪贴板同步，调整侧栏和底部留白，并提交

- 修正手机标题继承的额外底部Margin，与PC应用标题同高；应用页底部留白20→8，空操作状态折叠，增加内容可用高度。
- 手机详情底部增加独立默认关闭的剪贴板开关，复用现有深浅色开关。只同步文字，支持中文/表情/换行；单次UTF-8上限64KiB。开关保存在小型独立配置，只存启用设备的身份/连接信息，不存复制内容或历史。
- Windows复用隐藏激活HWND接收系统变化通知；Android使用ClipboardManager回调和既有ADB/runtime，一台启用手机一个事件连接。采用最新项覆盖队列、内容去重、跨端版本校验和取消令牌处理回传、快速复制与停用竞态，没有新轮询。关闭详情页保留启用同步，全部关闭/离线移除Windows监听，退出清理自身 helper，保护共享ADB和活动音频/虚拟屏。
- 23项同步夹具和60项UI/模拟连接检查通过；Java/D8与Full/Lite/Legacy编译通过，Legacy40条警告、无错误。夹具不读写真实剪贴板、不连真机，不代表真实设备或整版性能验收。只做本次必要验证；过程中修正App字段放置、元组类型、原子写入复用和外部主题资源动态查找，失败日志保留。
- 用户要求完成后提交且不过度检查：提交包含前轮尚未提交的手机UI工作，排除已有AndroidPcmSource.cs调参；不改README/Wiki、不发布Release/tag/Store。活动上一轮构建文件不动，新包使用固定editions-next暂存，再更新空闲的正常入口，具体身份和推送结果以父容器40号记录/收据为准。

## 2026-11-06：手机行共用普通应用 UI，应用选择独立成页，统一刷新

- 手机设备行复用普通应用的图标、双行文字、活动点及选中模板；按 ID 更新原行，添加设备独立入口保留。Lite/Legacy 不含手机新增模型和图形。
- 添加快速面板显示应用改为独占右侧的选择页，列表占用剩余高度，使用回收虚拟化；完成返回设备页。原生多选、右键批量添加、缓存和离开页面释放保留。
- 连接按钮放在地址框右边，移除两处局部刷新，功能统一到侧栏底部现有刷新。修复电脑应用列表清空选中项时把手机页面切走的问题。没有新后台轮询或缓存策略变更。
- 58 项定向 UI/模拟连接检查通过；Full/Lite/Legacy 编译通过，Legacy 有40条警告、无错误。浅深色和宽窄渲染已复核，300项目录仅生成可见行。初始 Binding 别名错误与隔离夹具默认退出行为已修正，日志保留；未扩大为全套性能、真机连接、扫码或听音验收。
- Full x64 构建固定复用父容器 `outputs/editions`，不创建新版目录、不停止正在运行的正常入口或共享 ADB、不自动替换/启动运行版。准确产物身份见构建回执与本轮交接。
- 本轮基于0192d84，尚未提交/推送，无 Release/tag/Store；不改 README/Wiki，不改已有 AndroidPcmSource.cs SmartVolume WIP。本地包包含该 WIP，后续正式发行须单独确认来源。记录见父容器39号文及 phone-pages-receipt.json。

## 2026-11-05：修核 README 失败，实施两项内存优化与在线许可入口

- 上次 70e8ce1 的失败由 README consistency 触发：1.31 版本与生成的 README.ai.md 不一致，在隔离旧快照中已复现。接手时另一 AI 的 bd4b880 已更新并有成功 CI；本轮保留其内容，当前 10 份 README 检查通过，未编辑 README/Wiki，也未重跑注定仍失败的旧提交。
- AndroidMediaSessionClient 的长驻 stderr 改为按需增长的固定上限环形尾缓冲；最多保存最近 16,384 字符，持续排空已有管道，保留结束诊断，不因无换行或运行时间增加而无限累计。既有其他长驻日志已有限额，本轮不重写它们，也不改变有超时的短命令返回值。
- 自动化刷新先准备一次候选再绑定全部消费者；手机元数据事件对同一原候选列表只合并一次，批后不持有额外缓存。保留控件独立选中项、源设备身份、元数据更新、未变化模型及原图标行为，没有新增后台任务。
- Full 关于页开源许可改用浏览器打开仓库内完整索引，索引链接到各完整许可文本；取消点击时的 Android runtime 初始化和额外本地 txt 导出。必要完整声明继续随 EXE 内嵌，Lite/Legacy 不显示该链接。
- 必要检查共 69 项通过，Full/Lite/net48 编译通过；Full 100 批×13消费者×40应用的局部候选绑定临时分配 12,573,640→1,284,040 字节，中位样本耗时 11.094→1.627ms，仅属于该夹具。Full 单 EXE 正常入口已更新，版本 1.31.0.0、26,842,481 字节、SHA256 `750AB038313061EFC86D46467BC25E22AD8389CE08E65E81ABEE1DED89D7B6F3`。
- 过程中修正测试夹具的只读图标注入、Legacy 参数和延迟设置控件初始化，初始失败均留本地记录，未为这些夹具问题改变产品逻辑。未测真机、听音、浏览器实际点击、整版长时内存/CPU、ARM64 和商店安装；未启动新版、未停止用户进程/共享 ADB、未发布 1.31。既有 SmartVolume WIP 保留且排除提交；本地包包含它，不能视为纯提交正式包。
- 本轮证据/收据见父容器 `integration-docs/38-MEMORY-FOLLOWUP-AND-ONLINE-LICENSE.md` 与 `outputs/memory-followup-20261105/`；下面上一轮“新的内存优化仅审查”现仅对未获执行的其他策略及取消的图标优化有效。

## 2026-11-05：提交已完成的1.31开发工作，后续内存优化仅审查

- 用户要求提交已完成的版本/架构、手机输出延迟与拖拽返回提示；使用前轮实际构建与定向验证记录，提交前检查差异、资源清单和工作区边界，不重复全套测试。
- 既有SmartVolume曲线调参留在本地，不纳入提交。现有本地运行包包含这份WIP，发布状态仍为1.30，不能把源码推送称为正式发行。
- 用户明确新的内存优化先不执行，随后撤回图标优化。图标代码保留；其余内存机会仅只读审查，未执行资源释放、缓存调整、后台定时器或新构建。
- 本节记录提交范围；准确提交ID、远端回读及未提交文件以Git和父容器最新交接为准。

## 2026-11-05：手机输出切换与返回手机拖拽卡片，构建 Full 1.31

- 用户反馈切换后等待出声，授权蓝牙一起测；后续明确听音自行验收，并要求拖动手机应用时左侧手机面板也显示返回卡片，以及构建供测。
- 并行手机运行文件/转发建立，重叠捕获与手机渲染准备；起播门槛降低但不缩小抗抖容量，热切换暂停后一次建立最终路由再恢复。提前请求自己捕获服务器精确退出；校验真实设备、主面板控制、低延迟轨道和共享 ADB 保护保留。全部工作由操作触发，不增加闲时轮询。
- 复用手机面板原拖放外层与 PC 卡片标签，增加“返回手机播放 / 松开后，由手机控制声音”，立即显示、退出拖拽恢复列表，主题共用、放回复用既有返回操作。
- Full x64 单 EXE 已构建并更新父容器正常运行入口为1.31.0.0；未启动、未发布、未提交主源码，旧用户实例保留；旧正常运行版已备份。Lite/Legacy 本轮未构建。
- 必要验证：并行启动/失败清理11、输出语义9、设备/拖拽UI35项通过；三轮零PCM路由确认的扬声器中位数1967.7→1011.3ms、蓝牙835.5→711.7ms。完整捕获/实际设备/控制/回收9项允许静音通过。严格非静音检查因静音失败；用户自行听音，不把连接结果当作听音通过。中间一次蓝牙5秒超时仍留档，后续一轮通过，原因未定；没有整版长测结论。
- 详细证据/收据见本地 integration-docs/37-PHONE-OUTPUT-LATENCY.md 与 outputs/phone-route-latency-20261105/。既有 AndroidPcmSource.cs WIP Hash 保持，README/Wiki未新增修改。

## 2026-11-05：1.31 版本入口、公共音频路径和 Wiki 技术页

- 要求：开发版本升 1.31，精简架构中的重复步骤，对照技术 Wiki 与实际源码。用户最初要求只核对 Wiki，随后明确授权一起优化该页；README 继续交由另一 AI，本轮不改。
- 实现：统一版本导入并补齐 AndroidAudio；注册表发布有序列表 + 身份索引，控制操作只解析一次源；全部静音共享同一列表 / 状态快照，移除重复读取，不增加扫描、后台定时器或新依赖。具体取舍见 [架构审查](ARCHITECTURE-REVIEW-1.31.md)。
- 构建 / 验证：Full 与 Legacy 解决方案、Lite 构建通过；五项目七目标版本均为 1.31.0.0。net10/net48 批量操作各 6 项、Android 模块 120 项、路由与公共控制 UI 35 项通过。修正两组落后的多设备测试键 / 路径和音频路径夹具缺失依赖，保留初始失败记录，不改变生产缓存实现。
- 局部微基准：两条模拟源、25 万次查找、5 轮，线性查找中位数 12.709ms / 临时分配 30,000,040 字节，索引查找 3.628ms / 40 字节；没有整版 CPU / 内存或启动速度降幅结论。
- Wiki：07-技术实现由 582 行精简至 170 行，校正项目依赖、原生组件、PID 参数、IPC、虚拟屏 Missing、检测兜底、正式 SmartVolume 参数及性能证据边界；保留可追溯源码链接，区分正式 1.30 与开发 1.31。Wiki `5bd32c90dcbf842ad26ed4576f0930e207a465aa` 已推送，远端提交 / Git blob 与网页新内容核对通过；HTTP 原文的一个客户端仍命中旧缓存，保留记录而不将其当作源码推送失败。
- 保留边界：既有 AndroidPcmSource.cs 调参 Hash 未变；无真机控制、无整版长测、无 ARM64 / 商店安装验收；未更新运行包、未发布 1.31、未推送主源码开发改动。

## 2026-11-04：v1.30 正式发布与 Full 商店包

- 用户授权正式发布 1.30 并构建微软商店包，随后明确 Wiki/README 由其他 AI 更新、Lite/Legacy 不显示“开源许可”链接。本轮生成过的 README 与生成源已撤回，最终内容相对接手基线没有变化，Wiki 未改；独立发行说明保存于 `docs/release-v1.30.md`。
- 修正发布工作流中残留的 Full ZIP 路径、自动发行说明中的 ZIP 升级提示及相关测试/报告文案，使其符合现有单 EXE 布局，没有修改本轮产品功能。
- 发行标签 `v1.30` 固定绑定干净源码提交 `1079f743618981972dfb620ddae47ecec82ad2fd`。构建通过：Full/Lite x64、ARM64 和 Legacy x64；.NET 10.0.402 SDK；停止生成 NET8。开发工作区已有的 SmartVolume 曲线调参保留且排除于正式构建。
- 必要验证：发布流程 78、Full 17、Lite 13、Full 真实包暂存/回滚 3、NET10/net48 更新器各 23、实际工作进程 33 组通过。工作进程首次受工作目录非规范 ACL 影响，未能添加测试拒绝规则；原失败留档，转到规范 ACL 的新 TEMP 隔离目录重跑 33 组通过，未调整项目权限。以上是断言/场景组，不是整版性能结果。没有新增手机操作、原生 ARM64 或商店安装验收。
- GitHub 已发布稳定 Latest，7 个附件全部下载回读、SHA-256 与本地一致；上传一次网络超时后按既有流程重试成功。MSIX 不纳入便携版 7 个附件。
- Full x64 商店 MSIX 版本 `1.30.0.0`，沿用已有 Identity/Publisher，MakePri/MakeAppx 通过；包内 EXE 与正式 Full x64 资产相同。MSIX 为未签名交付，未安装、未上传或提交商店。正常运行入口已更新，旧测试进程与共享 ADB 保留，未启动新包。
- 发行与商店收据见本地父容器 `outputs/official-release-20261104/` 和 `integration-docs/35-RELEASE-1.30.md`。本记录属于发行后的文档提交，不改变 `v1.30` 标签与已校验资产。

## 2026-11-04：恢复轻量回收、手机扬声器选项

- 用户要求：Lite/Legacy按1.29方式回收；手机扬声器使用设备名；入站手机应用可切回其手机扬声器；完成后交接，并提交GitHub。
- 基线：`e8eadf5`（前一轮本地内存实现），当时远端master为 `1bb8cc2`。
- 实现：Lite/Legacy取消显式全代GC，使用v1.29单次原生请求并保留门控/限频；统一设备名；共用入站输出候选和异步返回手机路径，防止误把手机虚拟设备ID传入Windows播放器。完整界面、快速面板、改名和拖拽提示同步。PC原路由路径与Full活动服务所有权保留。
- 必要检查：Full13、Lite16、Legacy16、手机路由13，共58项通过。Full没有原生收缩入口，Lite/Legacy回调不增加全代收集；源手机/其他手机和邻接应用隔离通过。手机部分无真机访问，不宣称CPU降幅或声音验收。
- 构建：Full/Lite x64单EXE、Legacy13文件ZIP成功。Full发布完成后顶层旧包被用户打开，替换被文件占用挡住；保留运行进程，新Full固定在Full-x64目录。相应脚本补齐重命名EXE与目标资产的运行保护。Lite、Legacy已更新固定测试包。
- 测试修正：初次Startup隔离使用派生Application加载原BAML失败，改为复用现有测试资源加载器；一项改名检查揭示已展开设备选项文字未更新，修复后通过。失败过程保留在本地证据，不作发布验收。
- 未验证：用户实际CPU/重开体验、真机手机扬声器听音、长时与ARM64。普通运行版未覆盖，未发Release/tag/商店包；GitHub推送结果由最新Git远端和本地交接收据记录。

## 2026-11-04：手机应用流转到电脑主面板实时音量条（峰值电平条）显示

- 用户要求：让手机应用流转到电脑主面板（`QuickPanelModernWindow`“应用音量”列表）时，也能像电脑原生应用一样在音量滑块内实时显示音量条（`PART_PeakVisual` 峰值电平条）。
- 基线：`0772345`（本地 `codex/memory-reclaim-20261104` 与远端 `master` 同步）。
- 实现：
  - `SonicRoute.AndroidAudio/AndroidPcmSource.cs`：保留原始 `LastPeak` 供既有 SIMD/诊断检查使用；新增音量缩放前的实时电平峰值采样（`_meterUnreadPeak`、`_meterActivePeak`、`_lastWriteTick`、`_lastNonSilentWriteTick`、`_meterWaiting`）、`ReadMeterPeak()` 与静态事件 `PeakActivity`。仅当 `AudioMeterService` 处于等待状态（`_meterWaiting == 1`）且收到非静音 PCM 写入时通过 `Interlocked.Exchange` 触发单次唤醒，静音帧或连续活跃帧不重复唤醒。
  - `SonicRoute.Core/AudioMeterService.cs`：新增可选外部峰值读取委托 `externalReader` 与 `WakeExternal()`，允许负目标 ID（`pid < 0`）代表 Android 外部音频流，同时将正 PID（`pid > 0`）保留给 Windows COM `MeterGraph`。仅存在或增删 Android 行时不创建、不重建 Windows COM 会话枚举图；可见负 ID 与原生应用共用同一套 33ms 采样周期、`Attack = 0.65f` / `Release = 0.15f` 平滑、`0.001f` 量化、`PeaksUpdated` 快照发布，以及无活动且衰减归零后的 `Timeout.Infinite` 休眠。
  - `SonicRoute/QuickPanelModernWindow.xaml.cs` 与 `SonicRoute/QuickPanelModernWindow.AndroidAudio.cs`：提取公共 `UpdatePeakVisual` 方法供 PC `AppRow` 与手机 `AndroidRow` 复用；在 `CreateAndroidRow` 中绑定 `RowSliderStyle` 模板的 `PART_PeakVisual` 与 `ScaleTransform(0, 1)`，分配互不冲突的负数 `MeterId`，接入视口可见行裁剪（`_visibleAndroidMeterRows`）、滑块音量变更即时重算长度、静音/取消静音及总静音同步、面板关闭/禁用电平表时取消 `PeakActivity` 订阅并清零。`#else`（`!SONICROUTE_PHONE`）提供零开销空实现，保证 Lite 与 Legacy（`net48`）不引入任何手机组件。
- 必要检查：
  - `checks/AndroidUiChecks`（`--speaker-routing`）：26 项断言通过（含新增 13 项 Android 实时峰值电平条断言：负 `MeterId` 隔离、`PART_PeakVisual` 模板与 `Theme.AccentPeak` 绑定、静音/有声 PCM 唤醒合并、`AudioMeterService` 按需采样可见目标、滑块音量缩放、静音隐藏与移除采样目标、关闭电平表清理）。
  - `checks/AndroidAudioChecks`（`--phone-demand`）：21 项断言通过（SIMD/标量峰值语义与按需加载未受影响）。
  - `packaging/Tests/MonitoringChecks`（`net10.0-windows`）：33 项断言通过（`AudioMeterService` 事件驱动休眠/唤醒与 COM 会话图生命周期）。
  - `packaging/Tests/PanelChecks`（`--panel-performance`，覆盖 `Full`、`Lite`、`Legacy`/`net48`）：77 × 3 = 231 项断言通过。
- 构建：已构建 Full/Lite x64 ReadyToRun 单 EXE 与 Legacy/net48 13 文件 ZIP（未启用压缩）。构建时用户正运行 `测试版/Full-x64/SonicRoute.exe`（PID 10636），按规范未结束用户进程；新版 Full 已写入顶层 `测试版/SonicRoute-v1.30-x64.exe` 及 `outputs/android-peak-meter-20261104/editions/Full-x64/SonicRoute.exe`；Lite 与 Legacy 已直接更新至 `测试版/`；普通 `运行版/SonicRoute.exe` 未覆盖。
- 未验证：真机流转下快速面板实时峰值条的肉眼观感与听音验收、整版长时 CPU 占用与 ARM64。未发 Release/tag/商店包。

## 2026-11-04：入站手机应用显示源手机蓝牙设备、固定直显与默认智能音量

- 用户要求：
  1. 让快速面板主面板（`QuickPanelModernWindow`）和完整界面（`MainWindow`）中流转到电脑的手机应用，在输出设备列表中除源手机扬声器外也显示该源手机已连接的蓝牙输出设备，点击源手机扬声器或蓝牙设备均返回手机播放。
  2. 完整界面手机设备页（`PhoneDevicePage`）“快速面板显示应用”列表：只要固定（保留在列表中）就直接显示在快速手机面板（`PhoneAudioWindow`），取消固定则移除，无需额外显示开关。
  3. 将完整界面每个固定应用右侧的开关改为“智能音量”（`SmartVolume`），默认开启，开启时自动将手机流转音频控制在舒适响度范围（解决开 10% 音量仍过大的问题）。
- 基线：`c6d418c`（本地 `codex/memory-reclaim-20261104` 与远端 `master` 同步）。
- 实现：
  - **源手机蓝牙 + 扬声器路由**：
    - `SonicRoute/AndroidAudioHost.cs`：新增 `OutputsForDevice(AndroidDevice device)`，基于 `GetDropZonesForDevice(device)` 返回该源手机的扬声器与全部已连接蓝牙输出设备（`AudioDeviceInfo`）；在 `AddAudioAsync` 启动入站流转时按 3 秒限频顺带触发一次非阻塞 `ProbeOutputsIfConnectedAsync([device])`，确保尚未打开快速手机面板时也能探测源手机蓝牙输出。
    - `SonicRoute/ApplicationAudio.cs`：`OutputsForApp` 对外部 Android 应用追加 `AndroidAudioHost.OutputsForDevice(source.Device)`（仍排除其他手机输出）；将 `IsSourceSpeaker` 扩展为 `IsSourcePhoneOutput`，使快速面板或完整界面选中该源手机的扬声器或蓝牙设备时均走异步 `ReturnAndroidToPhoneAsync` 返回手机，避免把手机虚拟设备 ID 传入 Windows WASAPI 播放器。
    - `SonicRoute/QuickPanelModernWindow.AndroidAudio.cs`：`RefreshAndroidRow` 檢測已展开的 `AndroidRow` 上源手机输出集合（`phoneOutputsChanged`）变化并即时刷新设备胶囊按钮。
  - **固定即显示在快速手机面板 & 默认开启“智能音量”**：
    - `SonicRoute.AndroidAudio/AndroidPhoneStore.cs`：`AndroidPhoneProfile.QuickPanelApps` 始终等于 `RetainedApps`（固定即显示，取消固定即移除）；将原 `HiddenQuickPanelApps` 替换为按禁用列表持久化的 `DisabledSmartVolumeApps`（JSON 字段 `DisabledSmartVolumePackages`），使所有新旧固定应用默认开启智能音量（`IsSmartVolumeEnabled(device, package) == true`），并提供 `SetSmartVolume(package, enabled)`。
    - `SonicRoute.AndroidAudio/AndroidModels.cs`、`AndroidPcmSource.cs`、`AndroidAudioSession.cs`、`AndroidAudioManager.cs`：`AndroidAudioState` 新增 `bool SmartVolume = true`；`AndroidPcmSource` 在 `Write` 中维护峰值包络 `_smartEnvelope`（快攻 `0.35f`、慢释 `0.04f`），在 `ComputeEffectiveGain()` 中当 `SmartVolume` 开启时结合感知音量曲线 `volume * (0.5f + 0.5f * volume)` 与动态响度范围因子 `Clamp(0.35f / Pow(Max(env, 0.02f), 0.70f), 0.20f, 1.40f)` 计算实际增益（例如满幅 `0.9` 信号在 10% 滑块音量下有效增益约 `0.021` 而非线性 `0.10`）；`AndroidAudioHost.OnPhoneStoreChanged` 实时调用 `Sources.SyncSmartVolume(Phone.IsSmartVolumeEnabled)` 同步活动会话。
    - `SonicRoute/PhoneDevicePage.xaml` 与 `PhoneDevicePage.xaml.cs`：将“快速面板显示应用”行右侧 `AutomationSwitch` 绑定改为 `SmartVolume`（`AutomationProperties.Name="智能音量"`、`ToolTip="智能音量"`），副标题更新为“固定在此处的应用会显示在快速手机面板中；右侧开关控制智能音量（默认开启，自动平衡音量范围）。”
- 必要检查：
  - `checks/AndroidUiChecks`：
    - `--speaker-routing`：29 项断言通过（源手机扬声器+蓝牙胶囊与下拉框展示、改名同步、点击蓝牙胶囊返回手机、SmartVolume 默认开启/10% 满幅信号衰减/实时开关同步、实时峰值条）。
    - `--device-actions`：31 项断言通过。
    - `--phone-layout`：72 项断言通过（含 `PhoneDevicePage` 智能音量开关默认开启、切换后保持 `QuickPanelApps` 显示等）。
    - `--phone-refinements`：60 项断言通过。
  - `checks/AndroidAudioChecks`：
    - `--phone-demand`：21 项断言通过（`new AndroidPcmSource()` 原始实例保持线性增益，SIMD/标量精确采样断言全过）。
    - `--device-actions`：39 项断言通过。
    - `--phone-library`：22 项断言通过（v1 迁移默认开启 SmartVolume、固定直显、按手机独立持久化 SmartVolume 状态）。
  - `packaging/Tests/PanelChecks`（`--panel-performance`，覆盖 `Full`、`Lite`、`Legacy`/`net48`）：77 × 3 = 231 项断言通过。
- 构建：已重新构建 Full/Lite x64 ReadyToRun 单 EXE 与 Legacy/net48 13 文件 ZIP（无运行进程占用，`测试版/Full-x64/SonicRoute.exe`、`测试版/SonicRoute-v1.30-x64.exe`、`测试版/Lite-x64/SonicRoute.exe`、`测试版/SonicRoute-v1.30-Lite-x64.exe`、`测试版/Legacy-x64/`、`测试版/SonicRoute-v1.30-Legacy-x64.zip` 全部更新到最新；普通 `运行版/SonicRoute.exe` 未覆盖）。
- 未验证：真机蓝牙连接下入站手机应用切回手机蓝牙的真机听音、真机 10% 智能音量主观听感、整版长时 CPU 占用与 ARM64。未发 Release/tag/商店包。


## 2026-11-04：明确手机输出并保留主面板接管

- 用户更正：电脑应用到手机、手机应用收到电脑后再选手机输出，两条路径的扬声器都不能误走手机默认蓝牙；选扬声器/蓝牙继续受主面板音量、静音、智能音量控制，退出才交还手机。拖回左侧手机面板复用退出。此要求取代上轮“点源手机输出即返回手机”的设计。
- 原因与实现：
  - `AudioOutputBridge.java`：普通 `setPreferredDevice` 被接受并不表示实际输出已切换；小米13有其他媒体在蓝牙播放时，深缓冲媒体输出仍会合并到蓝牙。为自己的 AudioTrack 请求低延迟输出、注册会话范围的 render 策略，按真实硬件 ID 验证；未验证时保持静音，路由回调和设备回调推进状态，限定失败超时；撤销盲选唯一蓝牙的兜底。未修改手机全局媒体策略。
  - `AndroidPcmOutput.cs`：提取两条路径共用的帮助进程/ADB 转发/PCM 连接，严格确认目标，忽略快速切换后滞后的旧路由通知；电脑进程保留原回环捕获和静音租约。入站手机应用通过同一接口渲染原 PCM 源的处理后数据，保留主面板控制；同手机扬声器/蓝牙切换复用连接、捕获与解码。
  - `AndroidAudioSession.cs`：手机输出不交给 WASAPI，不伪造 Windows PID；只有原生同时播放标记变化才重建捕获以消除默认设备上的未受控副本，保留源对象和控制偏好，切回 PC 恢复原同时播放偏好。输出错误纳入现有失败/清理流程。
  - `ApplicationAudio.cs`、`QuickPanelModernWindow.AndroidAudio.cs`：选择手机设备只改 renderer，退出与拖回手机面板共用原返回操作，不在输出选择时关闭虚拟屏。
- 性能：按 PCM 到达事件处理，缓冲复用；没有新增闲时扫描或周期清理。保持手机播放受电脑控制需要活动 PCM 上行约 192,000 B/s，低延迟轨道也有必要的活动开销，未宣称整版 CPU/内存降低。关闭只清理自己的帮助进程、端口和文件，不重启/结束共享 ADB。
- 保留原改动：接手已有 `AndroidPcmSource.cs` SmartVolume 调参，剔除新增 PCM 通知后内容与接手指纹一致。本次提交只纳入新增通知，不纳入已有调参；本地运行包包含该调参。
- 必要检查：`AndroidUiChecks --speaker-routing` 31、`AndroidAudioChecks --explicit-output` 9、`--phone-demand` 21、`--device-actions` 39、真机 `--explicit-output-live` 9、真机 `--pc-to-phone-live` 9，共 118 项通过。真实手机为小米13、波点音乐和已连接蓝牙；分别有真实非静音 Opus/PCM 及 Windows 进程回环音频，均验证扬声器→蓝牙→扬声器的实际类型，入站路径另验证静音与仍受控退出。
- 诊断过程边界：首次 native 静音探针成功不足以覆盖其他媒体正在蓝牙播放的场景；电脑到手机真机检查暴露共享深缓冲路由问题，低延迟输出修复后通过。滞后的路由回调还暴露检查需等待当前目标，已同时限定生产确认逻辑和检查条件。音乐暂停时的静音不能当作声音验证通过；最终真机检查明确播放前置条件后重新通过。早期探针/中间构建不作为最终交付证据。
- 构建：只更新固定 Full x64 ReadyToRun 单 EXE 入口，无程序压缩、无新版本目录。普通运行版和 Lite/Legacy 未动；Store 保持 Full。原运行的旧 Full 由用户从托盘退出后替换，未结束用户进程。
- 未测：人工听感、整版长期 CPU/内存、其他 Android 机型/版本及 ARM64。没有 Release/tag/Store 提交。本地证据及源码/包/远端提交的最终状态见父容器 `integration-docs/33-EXPLICIT-PHONE-OUTPUT.md` 和 `explicit-phone-output-receipt.json`。


## 2026-11-04：关于页开源许可文字链接

- 用户要求：将“许可与第三方声明”按钮改成“开源许可”文字链接，放在作者主页旁边。
- 实现：移除独立按钮，在原作者主页链接行 GitHub 后追加相同分隔符及 Hyperlink，复用原字体和 Theme.Accent 动态颜色。仅 Full 显示，加载时防止重复插入；点击继续按需读取原内嵌许可并打开文本，不在页面加载时读取许可内容或启动连接。
- 验证：Full x64 ReadyToRun 单 EXE 发布构建通过，检查 XAML 事件与 Hyperlink 类型；未扩展手机真机/性能测试，人工视觉与点击可在新版中确认。
- 交付：固定 `测试版/Full-x64/SonicRoute.exe` 已更新。顶层 `测试版/SonicRoute-v1.30-x64.exe` 正由用户运行，保留该文件与会话，未结束用户进程；当前新版入口以本地 34 号记录和收据为准。Lite/Legacy 和普通运行版未构建或替换，原有 SmartVolume 调参保留。
- 同步：本轮 UI 与交接随同提交到 GitHub；实际提交及文件 Hash 见父容器 `integration-docs/about-license-link-receipt.json`。没有 Release/tag/Store 发布。


## 2026-11-04：核对并补齐 v1.29 -> v1.30 发行说明与更新记录

- 用户要求：调用子代理核对 `v1.29` 到 `v1.30` 的 GitHub Releases、Wiki 更新记录与真实代码提交差异，并将遗漏的真实更新内容补充到发布说明与版本历史文档中。
- 核对结果（`v1.29` `18e11a6` -> `v1.30` `1079f74`，共 37 个提交、340 个文件变更）：
  - 原 `docs/release-v1.30.md`、GitHub Release `v1.30` 正文及 `SonicRoute.wiki/09-版本历史.md` 主要记录了 Full 手机集成改动，遗漏了 `3c0fcfc..29a46d8` 期间合入的桌面端功能与体验改进，以及 `README.ai.md` 中未同步的 `v1.30` 版本归档条目与 Store Full 打包说明。
- 补充内容：
  1. `docs/release-v1.30.md`、GitHub Release `v1.30` 在线正文（Release ID `433710996`）与 `SonicRoute.wiki/09-版本历史.md`：
     - 主题页新增快速面板应用行布局选项（紧凑单行 / 完整双行布局切换，以及紧凑/完整模式下输出设备名显示开关）、加大应用音量滑块垂直拖拽命中区域、修复滚动条显隐引起的宽度跳变。
     - 自动化新增 `SetCurrentAppInputMute`（37）与 `ToggleCurrentAppInputMute`（38）两项当前前台应用麦克风静音动作（共 39 种动作）、动作选择器按类别分组展示、执行历史多目标确认统计（“已确认 X / Y 个目标”）及一次性定时触发状态提示。
     - 概览页与应用页音量卡片静音按钮与状态标签对齐、精简冗余“当前：”前缀、移除主窗口与快速面板外层 `DropShadowEffect` 阴影、统一 9 语言“播放 / 录音”术语、Full 版关于页 GitHub 旁“开源许可”文字链接。
     - 配置导入前结构化预览确认弹窗（`MainWindow.ImportPreview.cs`）、高频滑块拖动合并写入器（`CoalescedVolumeWriter`）、快照异步配置落盘（`ConfigService.QueueSave`）、更新器下载与校验支持取消。
  2. `docs/readme/generate.cjs` 与 `README.ai.md`：修正 Store 打包说明（复用 NET10 Full x64 EXE），在 `VERSION_ARCHIVE_AND_CHANGELOG` 补充 `v1.30` 完整变更记录，并通过 `node docs/readme/generate.cjs --check` 校验。
  3. 修正 `测试版/README.md` 历史段落中残留的“点击源手机扬声器或蓝牙返回手机”旧表述，明确自 `ccc4254` 起切换至源手机输出继续受主面板控制、退出或拖回左侧手机面板才交还手机。
- 边界：仅更新文档与在线 Release 说明正文，不移动 `v1.30` 标签（`1079f743618981972dfb620ddae47ecec82ad2fd`），不改动已发布的 7 个 Release 附件，保留开发工作区未提交的 `SonicRoute.AndroidAudio/AndroidPcmSource.cs` SmartVolume 调参，不结束任何运行中进程或共享 ADB。

