# 开发交接

更新：2026-11-08。提交、远端、运行包及本地未提交内容以实际 Git 状态和父容器最新收据为准。

## 当前交付：1.31 文档体系与 Wiki 同步

- 新增 `docs/release-v1.31.md` 完整发版文档。
- 更新 `docs/readme/shared.json`（v1.31 / 2026-11-08 / 下一版 v1.32）、`generate.cjs` 中的 AI 机器规范与 9 种语言的 `releaseSummary`；重新生成全套 10 份 README 并通过 `node docs/readme/generate.cjs --check` 校验。
- 同步推送 `SonicRoute.wiki` 仓库：补齐 `09-版本历史.md` 1.31 条目，更新 `08-版本相关.md`、`Home.md`、`01-使用指南.md`（整机主屏镜像、双向剪贴板同步、编码器画质/120fps、拖拽返回提示）、`02-功能特性.md`、`07-技术实现.md` 与 `AI.md`。
- 保留本地 `AndroidPcmSource.cs` SmartVolume WIP（`D01B47…561EA`）。

## 历史交付：1.31 微软商店包

- 用户要求基于50号最新代码构建商店版、少做检查。源码HEADffc58a0；复用最新Full x64 EXE（6457C1…9ED9），未重新编译或重跑应用测试。原PCM调参D01B47…561EA完整保留并包含在本地Full/MSIX中。
- 现有Store打包脚本的Full身份/运行时校验、MakePri/MakeAppx和最终MSIX身份/1.31.0.0/x64/包内EXE Hash对照通过。正常入口/旧商店包/用户产品和ADB保持，未安装、签名、提交商店或发布。
- 父容器`商店包/SonicRoute-v1.31.0.0-x64-unsigned.msix`，12,074,907字节，SHA256 `2816759C241C11C5C639B0D0C23A13095A29C3CFAB92766B093A0DEA576411B6`；同目录版本化构建信息。51号记录、store-1.31-package回执和根入口已同步；证据在outputs/store-package-20261108。本轮文档未提交/推送。

## 历史交付：友好编码器名称、九语本地化与 24/32/50 Mbps / 120 fps

- 将`PhoneDevicePage` Opus/H.264 编码器选择器从裸 Codec ID 改为“厂商 + 硬件/软件编码 (原ID)”的九语友好说明，`SelectedValue` 与持久化仍用精确 ID；`EncoderCatalog.java`（API>=29）新增 `"details"`（`hw`/`sw`）输出，`AdbClient.ParseEncoders` 解析并向后兼容旧 JSON，离线已保存配置按前缀识别高通/联发科/三星 Exynos/海思/紫光展锐/瑞芯微/晶晨/Google/Android；按硬件优先于软件再于其他排序。
- 语言切换通过 `_streamLanguage` 触发重刷实现九语实时更新；`EncoderOptionTemplate`（`TextTrimming="CharacterEllipsis"`、`Style="{x:Null}"`）适配窄边框省略显示且保留高亮前景色，`UpdateEncoderToolTip` 动态呈现完整说明。画面码率移除 `2 Mbps`、增加 `24/32/50 Mbps`（预设 `1/4/6/8/12/16/24/32/50 Mbps`），帧率增加 `120 fps`（预设 `15/24/30/60/120 fps`），第 3 列 `Auto` + `MinWidth="80"` 确保 520 DIP 下不截断；未新增轮询或定时器，默认值维持自动/128K/4M/30fps。
- 校验全过：helper `sonicroute-catalog.jar` 重建（20,341 字节、Hash `3256BC6B...B03D`）与 `AndroidRuntime.files.json` 同步；13+8 运行时文件、九语 838 键（0 重复/占位符一致/HEAD 816 值不变）、`SerializationChecks`（net10）、`AndroidAudioChecks --stream-settings`（12 项）、真机（小米13）`--encoder-catalog`（只读未开流，实测返回 `c2.qti.avc.encoder -> Hardware` 等）、`AndroidUiChecks --panel-stream-settings`（29 项 + 520 DIP 浅/深图目视核验）及 `--settings-localization`（390 项）通过；Lite Release（0 警告）与 Legacy net48 Release（46 既有警告）编译通过；Full x64 ReadyToRun 单 EXE 发布成功（`27,211,121` 字节，SHA256 `6457C1DE82CF86363E0E5FFC9F727F710750DE668EB067649AA211AE49959ED9`）。
- 交付与提交状态：用户要求推送 GitHub，本次提交纳入 44～50 号全部源码、运行时资产、检查与交接更新（既有 `AndroidPcmSource.cs` SmartVolume 调参 `D01B47…561EA` 保留在本地工作区，不纳入提交），复用既有检查与构建结果；无 Release/tag/Store 或 README/Wiki 改动，准确提交与远端状态以实际 Git 及父容器提交收据为准。构建包 `outputs/editions/SonicRoute-v1.31-x64.exe` 已生成；交付时 `运行版/SonicRoute.exe` 正由运行中旧实例占用，按约定保持未覆盖、未结束进程。未测真实 120fps/50Mbps 推流视听、展开弹窗截图或跨 DPI。完整本地细节见父容器 `integration-docs/50-FRIENDLY-ENCODERS-AND-120FPS.md` 与 `friendly-encoders-receipt.json`。

## 历史交付：面板布局、入场动画与手机声画设置

- 完整窗口应用列表滚动条右移6 DIP；手机应用固定卡片包含标题、数量、说明和列表。复用原模板/PhoneCard，不另造列表交互。
- 额外面板等初次原生音量快照后显示，未读保持“—”、真实0%保留；更新/禁用Slider不因初始化排队写音量。手机和额外面板复用PanelEntranceMotion，关闭/隐藏清除动画时钟，定位不变。
- 手机详情顶部两行：Opus编码器/码率，H.264编码器/码率/帧率；默认自动/128K/4M/30fps。按物理手机保存到既有android-audio.json（读v1/v2/v3、写v3），下次对应连接生效，不重启活动流。整机display0与普通应用屏都读取视频偏好；不恢复独立桌面。
- EncoderCatalog与AdbClient.EncodersAsync仅在首次打开下拉时查询，12秒期限，当前设备页复用；挂起/切换取消并只删除自有临时JAR。编码器名/数字验证，配置后台按需加载，复用有限保存合并；无新增闲时轮询/定时器。
- 9库、14UI、一次真机只读编码器查询、13runtime/九语824键和Full x64发布通过；浅/深520-DIP预览已查看。未做本轮声画接管/听音、性能/跨DPI或重复全套/native/Lite/Legacy。编译夹具小错误与选项显示问题已修正，初始日志留在父容器证据目录。
- 正常入口已更新，同包outputs/editions/SonicRoute-v1.31-x64.exe：27,190,641字节、Hash BA7F2CA5CFEFCA5428644B8A8202C529823AC3A57FEEBEDCB169D8C067A31611。旧正常入口先备份；活动旧editions-next/PID19932保持011ACAB2…D114，退出旧托盘程序后打开正常入口，未自动启动/停止用户程序或ADB。
- helper724230…B022（20,223字节），音视频server/native保持48号版本；原PCM调参D01B47…561EA完整保留并包含在本地包。HEAD26523e9，44～49未提交/推送，无发布。最新父容器49号记录、panel-stream-settings收据和根入口已同步；接手不重跑已通过检查。

## 历史交付：整机入口恢复手机主屏镜像

- 用户撤回整机独立桌面：拖出顶部手机名称卡片恢复display0主屏镜像和整机声音接管，Flex=0/Mirror=1、等比窗口、标题栏只显示手机名。手机名称/图标/拖拽和既有普通单应用虚拟屏保留。
- 删除桌面应用选择UI/接口/任务事件/原生桌面监听集成/专用检查入口与文案；行模板回到手机面板，移除共享字典。保留原状态提示事件定位、D3D11 GPU与首帧就绪；无桌面事件等待、无副屏IME参数，忽略旧桌面尺寸，不改手机主屏分辨率。无新轮询或目录读取。
- 视频server恢复经Hash校验的原D92B5E版本（100,749字节），native EXE仍D5847E（含既有镜像48项钩子/PE手机图标）；音频server/helper不变。不要按47号video-client历史构建回执把22CA3F桌面server复制回来，以当前runtime两份清单为准。
- 25库、35UI、2包和Full x64发布通过；有界真机检查确认主屏真实内容、等比调整、无独立交互桌面及精确回收，无新增声音/截图保存。Android内部镜像Surface端点不等于独立桌面；早期统计所有scrcpy端点的过严断言已纠正。未新增性能/听音/跨DPI证据，不重复全套/native/Lite/Legacy。
- 最新包`outputs/editions-next/SonicRoute-v1.31-x64.exe`，27,137,393字节、Hash `011ACAB21F5467BAA317ED0657774E2966C585A964563FE3BFA3CF6C1146D114`。交付检查时正常入口仍活动（PID46824、旧E57F45），未覆盖/结束；退出托盘旧版后使用新包。未自动启动产品、装APK或重启ADB。
- HEAD26523e9，44～48累计功能未提交/推送；原PCM调参保持D01B47...561EA并包含在包中，九语817键/旧816值不变。父容器48号记录、restore-phone-mirror收据和根入口已同步，下节47号为历史。无发布操作。

## 历史交付：独立桌面白屏修复，不安装其他桌面

- 用户实测白屏，拒绝安装其他launcher。小米SDK36的SecondaryDisplayLauncher实际空内容；整机入口现在用电脑端内置应用桌面，双击/Enter把应用开到同一个自有虚拟屏，鼠标中键/HOME返回。无APK/默认桌面修改，不回退主屏镜像；物理屏继续使用另一应用。它是应用入口，不克隆OEM小组件，同应用多实例仍依Android/应用策略。
- 复用OwnedAppMonitor的ITaskStackListener/100ms事件合并，按自有显示器识别home/app；列表只在HOME显示，应用可见后隐藏，失败不提前隐藏。原单包监听保持。电脑端共用PhoneAppListResources、PhoneAppItem、ModernTextBox、目录缓存和主题；原状态窗定位提取到VirtualDisplayOverlayWindow，处理Owner移动/大小/最小化/恢复/关闭，Alt+F4结束自有显示器。无新增轮询，关窗取消读取/启动、解绑并清空展示引用。
- 本机手机截图已有计算器但旧SDL direct3d窗口仍白，整机子进程改用GPU D3D11，普通应用屏参数不改。Startup除首帧还须收到可用桌面事件；沿用25秒期限，不后台重试。整机音频路径/30fps/4Mbps/像素预算沿用。
- 最终8项真机检查包含157个应用、搜索、同一显示器启动计算器、Windows实际内容、HOME、最小化/恢复、Alt+F4和回收；27库、35UI、2包及native检查通过。Full发布、Lite仅编译通过，13份runtime和九语819键检查通过，HEAD旧816个键值不变。未扩大为听音/中文输入/跨DPI/多手机/整版性能证据；早期只任务通过且Windows仍白的截图已明确不作为最终验收。
- 正常入口已备份更新为Full x64单EXE，27,170,161字节、SHA256 `E57F459800C08EE0D2CC6E37478585DCFDCDDD4BB7B1AC592C05F5A220A0BAEA`；同Hash在父容器outputs/editions-next。替换时旧editions/441643包仍运行且保留原状，最终复查已退出，无SonicRoute/scrcpy活动进程，代理未关闭它；现在可开正常入口。未自动启动新产品、重启ADB或安装任何APK；诊断临时远端JAR已清理。
- HEAD26523e9；44～47功能均未提交/推送，原AndroidPcmSource.cs保持D01B47...561EA并包含在新包。完整依据、源码边界、手动步骤及收据见父容器47号记录和desktop-white-screen-receipt.json；下节46号为历史。无Release/tag/Store或公共README/Wiki发布。

## 历史交付：独立手机桌面与scrcpy手机图标

- 用户明确选择整机入口打开独立手机虚拟桌面，手机主屏继续另用。设备入口改用new-display、系统装饰与local IME，不指定应用start-app；Flex=1/Mirror=0，像应用屏调整自有虚拟屏，整机声音仍复用原单一接管路径。旧主屏镜像/只等比方案已被取代，未自动回退主屏。应用多实例仍受系统/应用启动模式限制。
- 新Build-PhoneIcon脚本从既有Phone Geometry生成多尺寸ICO及透明外部背景PNG，原生构建自动生成并替换scrcpy PE图标；设备桌面/无应用图标的native窗口使用固定phone-icon.png，真实应用图标保持。UI/拖拽仍使用缓存矢量，运行时不重复绘制；新增PNG仅2,546字节，随视频组件按需准备。
- Native首帧日志仅一次；设备桌面只有收到首帧才Ready/Active，Texture大小本身不算就绪。25秒无帧限时失败清理，追加DesktopNoFrame错误（旧58项编号不变）和九语提示。未增加轮询或持续重连，桌面/音频按原所有权释放。
- 24项库定向、35项UI、2项包、native自检与PE图标逐像素比对通过；Full构建及13份runtime/九语校验通过。只读查询小米SDK36的HOME/SECONDARY_HOME，确认提供SecondaryDisplayLauncher；没有创建显示器、启动应用/新产品、听音或测量性能，不能据此称真机桌面已验收。
- 新固定测试包`outputs/editions/SonicRoute-v1.31-x64.exe`，27,141,489字节，SHA256 `44164377EAAAD3024C9A8D23C390B368702179E97648C04D2D6AF6587E46723E`。普通入口仍活动且保留23EC2B旧包，未覆盖/结束或停止ADB；用户退出后可开测试包。
- HEAD26523e9；44～46功能均未提交/推送，原AndroidPcmSource.cs WIP保持D01B47...561EA，新包包含它。无Release/tag/Store或README/Wiki操作。完整方案、依据、手动步骤和边界见父容器46号记录及phone-desktop收据；下节45号为历史。

## 当前交付：共享手机图标、纯名称标题栏、整机等比窗口

- 整机卡片、主面板及拖拽预览统一使用缓存/冻结的`AutomationIcons.Phone`轮廓图，去掉整机卡片的彩色底和♫，底部选择器也引用同源Geometry；不光栅化、不读写图标文件、不增加监听。普通应用图标保留原自定义/PNG流程，新代码Full条件隔离。
- 整机启动设`SONICROUTE_PHONE_MIRROR=1`，复用native钩子约束八边/角及程序尺寸为当前画面比例，按当前工作区等比最大化/恢复、旋转更新比例；标题栏只保留手机名。整机不发Android尺寸包、不走跨线程resize绘制，不新增定时器；单应用VD继续自由缩放。全屏快捷键在镜像模式改为等比最大化/恢复。
- native客户端单独重建，`Build-VideoClient.ps1 -ClientOnly`复用原固定视频server，清单同步；48项隐藏HWND镜像断言、旧自由缩放/IME自检、35项UI/图标、19项库参数、2项包检查通过。Full发布和Lite必要编译通过；无新真机/跨DPI/声画/整机性能验收，没重跑旧670项或全部模块/路由。
- 最新入口为父容器`运行版/SonicRoute.exe`，与`outputs/editions-next/SonicRoute-v1.31-x64.exe`同Hash `23EC2B45B5C6751DAF76AD7C8CBC92B444FA78FC64DFC3901B1DE9E50BFE8154`，27,137,393字节。正常入口空闲后已替换并备份；用户运行中的旧editions/E8F25包保留，未结束/覆盖或自动启动程序，ADB未停。
- 44/45号功能均仍未提交，HEAD26523e9；原AndroidPcmSource.cs调参保持D01B47...561EA，新包包含它。无Release/tag/Store、README/Wiki改动；细节、手动步骤、边界见父容器45号记录和mirror-window-receipt.json。下节44号构建状态为历史。

## 当前交付：手机名称整机音频入口与手机主屏镜像

- 手机应用列表顶部增加设备音频项，直接复用底部选择器的自定义手机名、现有卡片、搜索/排序、拖拽/Enter/Esc/返回，以及接管行的音量、静音、输出与退出。九语只各增加一条说明，原816个资源值不变；改名/换机原地更新。
- 拖出设备项时复用固定视频客户端镜像主屏display0，无new-display/start-app/虚拟屏分辨率调整；声音复用单一托管playback捕获，覆盖手机及虚拟屏内应用允许捕获的声音。普通应用保持UID隔离；明确的设备保留身份才省略包过滤，UID失败不回退整机。
- 同手机整机/单应用互斥；启动新范围前等待冲突及正在释放的旧策略，重复操作复用连接，其他手机独立。画面音频借用增加发起包校验，关闭普通应用屏幕不能释放借用的整机声音。手机回放轨道标记不可再捕获，helper jar与清单同步更新。
- 性能：新行不创建ADB、不扫描；复用事件、缓存、可见界面刷新和原生命周期，不增加轮询。画面按需启动，沿用30fps/4Mbps上限；视频客户端不另播声音。不声称无活动开销或整版性能提升。
- 208项定向离线检查通过（新库19、新UI31、库模块120、既有路由38），含浅深色、九语、改名/换机、快速策略释放与跨手机隔离。Java/D8、Full x64 ReadyToRun发布和12份组件清单校验通过；未重跑Lite/Legacy或670项旧检查。无新真机声音、镜像运行、回采/蓝牙或CPU/内存/延迟结论。
- 固定测试包为父容器`outputs/editions/SonicRoute-v1.31-x64.exe`，27,133,297字节，SHA256 `E8F25C70C2BFA9A86E88416C4F3C626A7E74409E65722DEBB74338FB70A1C065`。普通运行版仍在运行，原文件Hash BF7749...2E7保持；未停止产品或ADB、未替换运行版、未自动启动新包。
- HEAD仍26523e9，本轮功能尚未提交/推送。原AndroidPcmSource.cs WIP保留Hash D01B47...561EA，测试包包含它和当前改动；无Release/tag/Store或README/Wiki操作。完整记录和手动步骤见父容器44号记录及device-audio-receipt.json。43号交接为此前基线，不能再称仅一份文件未提交。

## 当前交付：设置页崩溃修复与Gemini本地化完整复核

- 用户随后授权提交本轮已完成的九语资源、本地化接入、设置崩溃修复及回归检查；只提交明确审查的文件，既有AndroidPcmSource.cs SmartVolume WIP保留，不纳入提交。复用下述检查和运行包，本次提交不重复构建/扩测，不修改README/Wiki或创建Release/tag/Store；最终提交及远端状态以Git和父容器提交收据为准。
- 2026-11-07 用户反馈点击设置立即退出，并要求完整检查Gemini改动。Windows应用事件1026及隔离的真实设置页BAML Loaded事件均确认：新开源许可Run.Text绑定默认TwoWay，无法写回只读L10n索引器。已显式改成OneWay，保留实时翻译。
- 同时修复挂起手机详情页后切换语言、再进入时缓存应用描述/操作名称不刷新的问题；AppItem新增带变更检测的来源标签通知，快速面板字幕绑定不再只通知Label。手机行仅在语言或手机名称变化时刷新相关翻译文案，删除重复峰值绘制及废弃的虚拟屏字符串通知处理器。没有新增轮询、连接重启或工作集裁剪，旧SmartVolume WIP Hash保持不变。
- 审查原工作区37份差异中的36份Gemini文件，排除接手前AndroidPcmSource.cs WIP。九语各新增191个Phone键，原有译文语义未改；此前已通过的键/占位符校验继续适用，所有XAML翻译Run显式单向绑定。新增设置页回归覆盖真实加载事件、重复加载、浅深色/九语、手机页挂起恢复、来源绑定、58种错误标识、虚拟屏状态及关闭后解绑。
- 本轮定向离线检查670项通过：设置与本地化390、手机页面60、路由38、剪贴板23、库设备操作39、库模块120；Full/Lite/Legacy编译通过。Lite790、Legacy46条警告，未治理无关/生成代码警告；最后Full增量发布日志无警告输出，不代表干净全量构建零警告。没有新真机、可见整窗人工点击、扫码/听音或整版性能结论。
- 最新正常入口仍为父容器 `运行版/SonicRoute.exe`，Full x64 ReadyToRun单EXE /1.31.0.0 /27,125,105字节，SHA256 `BF7749876034BB8DB74DC1020FDC174CC9A006BD4CE431CFC916A82FB77CD2E7`。命名产物固定复用 `outputs/editions/SonicRoute-v1.31-x64.exe`。未启动、未停止共享ADB；本地包包含本轮本地化及仍未提交的既有SmartVolume调参，不能当作纯提交发行包。根入口及42号记录、`integration-docs/localization-review-receipt.json`和`outputs/localization-review-20261107/`记录构建时状态，后续提交以提交收据为准，以下简查包Hash已过时。

## 前轮开发：手机功能九国语言本地化与实时语言切换

- 2026-11-07 主代理按用户“简单检查并构建测试”要求复核：九语各816键、191个 `Phone.*` 键（含58个错误标识）、无重复/缺失键及占位符不一致，静态引用齐全；删除虚拟屏状态窗恢复按钮的两处重复赋值。Full x64 ReadyToRun 单EXE发布成功，27,125,105字节、SHA256 `684E8087C76DA7548CCA85CD7DAE8E742ED93077E89E92C3386945D3CD1CDB9A`，已更新父容器 `运行版/SonicRoute.exe`，未启动。构建无错误，日志有790条警告输出（含生成代码可空性与平台兼容性）；未顺带清理无关警告，也未重复下面前一位AI报告的UI/三版检查。当前未提交/推送，本地包包含现有SmartVolume WIP；未连接手机、无新增运行时语言切换或性能结论。收据在父容器 `outputs/phone-l10n-20261107/delivery-receipt.json`，当前交接见 `integration-docs/41-PHONE-LOCALIZATION-TEST-BUILD.md`。
- 补齐手机相关功能在全部九种内置语言（`zh-CN`、`zh-TW`、`en-US`、`ja-JP`、`ko-KR`、`fr-FR`、`de-DE`、`es-ES`、`ru-RU`）下的本地化资源与实时切换更新，复用现有 `L10n.Instance`（XAML `Binding [Key]` 与代码 `L10n.T`），不新增翻译框架、轮询或后台定时器。
- `SonicRoute.AndroidAudio` 新增稳定错误标识 `AndroidErrorCode`（实际58项，前一位AI报告的62项已更正）与 `AndroidError.WithCode` / `TryGetCode` 及 `AndroidVirtualDisplayManager.FailedError` 事件，不反向依赖 WPF 或 `SonicRoute`；展示层由 `AndroidErrorTranslation` 按 `Phone.Err.*` 键翻译。`PhoneClipboardSync` 内部连接状态改为稳定枚举 `SessionState`，不再依赖中文状态文案判断逻辑。
- 已打开的界面（`MainWindow` 手机侧栏与开源许可链接、外部手机音频 `AppItem.SourceLabel`、`PhoneDevicePage` 设备详情/配对/应用目录/剪贴板/操作状态、`PhoneAudioWindow` 媒体卡片/分类描述/延迟提示/双向拖拽区域、`QuickPanelModernWindow` 手机音频开关/拖拽层/应用行输出与退出、`VirtualDisplayStatusWindow` 独立屏幕状态窗）均订阅 `L10n.Instance.PropertyChanged` 并在语言切换时原地刷新，无需重新连接手机；窗口或页面挂起/关闭即解绑，不持有已关闭界面引用。
- 保持应用名、自定义手机名、歌名、歌手、包名、地址与协议字段原值；紧凑按钮采用适配窄布局的简洁译文。Full/Lite/Legacy 条件编译隔离保持不变，用户配置与 `%LocalAppData%\SonicRoute\Lang` 外置语言文件未修改。既有 `SonicRoute.AndroidAudio/AndroidPcmSource.cs` 未提交改动完整保留。
- 必要验证完成：九语共 816 键齐全、0 重复键、占位符一致；Full、Lite（`-p:SonicRouteEdition=Lite`）、Legacy（`net48`）均 0 错误编译通过；`AndroidUiChecks`（`--phone-clipboard` 23 项、`--device-actions` 35 项、`--speaker-routing` 38 项、`--phone-pages` 60 项）及 `zh-CN` / `ja-JP` / `en-US` 实时切换与九语紧凑布局抽查全部通过。未启动全套性能测试或真机连接流程。

## 本次提交：按设备剪贴板同步、界面留白与前轮手机页面

- 手机侧栏标题明确覆盖 SectionTitle 的底部 Margin，与“有音频的应用”保持同样高度；应用页内容区底部留白20→8，空操作状态不再占位。其他页面原留白保留。
- 手机详情最底部新增“剪贴板同步”，复用 AutomationSwitch 的动态主题样式。每台设备默认关闭、独立保存，开启后电脑与手机双向同步文字；多台启用设备共享最新文字，关闭某台不影响其他设备。开关写入独立小文件 `phone-clipboard.json`，不把复制内容、历史或日志存盘。
- 只在启用且连接成功时注册 Windows 剪贴板变化通知，复用原激活消息 HWND；Android 使用 ClipboardManager 回调。每台启用手机仅一个专属事件连接、共享既有 ADB 服务与 runtime；没有后台剪贴板轮询。最后一台关闭/离线后移除 Windows 监听，关闭详情页保留必要同步连接，退出程序才整体释放。
- C#和Android端发送均只留最新待发内容，支持中文/表情/换行，UTF-8上限64KiB。使用去重和本机/手机剪贴板版本检查，抑制回传环路，避免等待中的旧写入覆盖新复制；停用取消待写入。连接失败不后台循环重连，复用设备刷新/重连事件恢复。
- 启动仅检查小开关文件；无文件不加载同步服务，没有启用设备不扫描手机。启用状态启动恢复可复用设备缓存，必要时一次现有设备扫描。移除设备同步清除对应开关，名字/端口变化仍按硬件身份识别。
- 必要验证完成：23项同步生命周期/竞态夹具、60项界面与模拟连接检查通过，Java/D8编译及Full/Lite/Legacy编译通过；Lite/Legacy排除新手机文件和资源。没有真机互复制、真实系统剪贴板或整版CPU/内存验收，不把夹具通过当作听音/性能结论。
- 本次提交同时包含下方前轮未提交的设备行、独立选择页和统一刷新工作。README/Wiki保持，原 AndroidPcmSource.cs SmartVolume WIP 排除提交、Hash不变；本地包包含该WIP。无Release/tag/Store提交。
- 活动的上一轮 `outputs/editions/SonicRoute-v1.31-x64.exe` 受运行占用保护，保持不动。新Full使用固定 `outputs/editions-next` 暂存位置，再更新空闲正常入口 `运行版/SonicRoute.exe`；准确身份、替换结果和推送回读见父容器40号记录及 `phone-clipboard-receipt.json`。

## 前轮交付：手机设备行、独立应用选择页和统一刷新

- 手机设备改用普通应用同一 `AppsCardItemTemplate` 和 `RoundedListBoxItem`，图标尺寸、双行文字、活动点和选中样式共用；轻量设备行模型按 ID 复用，不制造音频 PID，不引入全量重建或新的订阅/轮询。手机专用图标与模型仍由 Full 编译条件排除于 Lite/Legacy。
- 添加快速面板显示应用进入独占右侧的选择页，完成返回原设备页并恢复滚动位置；保留原生 Ctrl/Shift/Ctrl+A、多选与主题右键菜单。列表使用剩余高度和回收虚拟化，进入继续用已有目录缓存，退出释放目录行、选择和菜单。
- 直接连接的按钮移到输入框右侧，取消局部设备刷新和目录刷新按钮。侧栏底部刷新按当前页面刷新设备或目录；复用原扫描、取消、缓存与命令服务。电脑应用列表重建不再因空选中项隐藏手机页。
- 58 项定向模拟 UI 检查通过，覆盖共享 PC 活动/静音点、双机改名/选中、四种连接流程、统一刷新、目录导航/批选/释放、浅深色与宽窄窗口。300 项目录在夹具中仅创建可见容器；不是整版内存/CPU或速度测量。未访问真实手机或展示正常产品窗口。
- Full/Lite/Legacy 编译通过；Legacy 有40条警告，无错误。本轮 Full x64 单 EXE 固定输出在父容器 `outputs/editions/SonicRoute-v1.31-x64.exe`，准确构建回执见 `outputs/editions/Full-x64-receipt.json`。正在运行的正常入口保持原文件，不停止用户进程或共享 ADB。
- 当时尚未提交/推送，现随上述剪贴板改动纳入本次提交，基线 HEAD 为 `0192d84`；无新 Release/tag/Store，README/Wiki未改。既有 `AndroidPcmSource.cs` SmartVolume WIP 未动，Hash 见父容器收据；本地构建包含它，不能当成纯提交正式发行包。下方日志限额与候选批次说明属于前轮，仍有效。
- 本轮完整记录见父容器 `integration-docs/39-PHONE-PAGES-AND-REFRESH.md`、`integration-docs/phone-pages-receipt.json` 与 `outputs/phone-pages-20261106/`。若用户退出旧实例后要求替换，再校验新包与目标进程占用；本轮未自动替换运行版。

## 当前交付：有界手机日志、批次候选复用与在线许可

- 用户本轮明确执行两项优化；图标优化仍取消。媒体会话 helper 的 stderr 从累计整段输出改为持续排空、仅保留最近 16,384 个 UTF-16 字符（尾部正文最多约 32 KiB），按实际输出增长，长时间运行和无换行日志不再无限累计。没有新增轮询或清理定时器，不改变活动连接和工作集策略。
- 自动化一次刷新共用一次过滤后的应用候选；手机事件更新按同一原候选列表复用合并结果，仅当前批次有效。每个下拉独立选择，按身份保留不同手机应用，不依赖可变元数据对象的引用长期缓存。图标模型和加载逻辑未改。
- Full 关于页“开源许可”改为浏览器打开 [完整许可索引](AndroidLab/THIRD-PARTY-NOTICES.md)，不再为点击入口准备 ADB 或额外导出本地文本。依赖要求的完整许可副本仍内嵌在单 EXE；Lite/Legacy 仍无该入口和手机资源。
- 上次红色任务是 [70e8ce1 的 README consistency](https://github.com/kunkunkunQoQ/SonicRoute/actions/runs/42021504857)，并非 C# 编译失败。隔离复现确认版本升到 1.31 后 README.ai.md 未同步；另一 AI 后续 bd4b880 已修好且 [CI 通过](https://github.com/kunkunkunQoQ/SonicRoute/actions/runs/42022456282)。本轮保留该提交，未修改 README/Wiki；当前 10 份 README 一致性检查通过。
- Full/Lite/Legacy 编译及定向检查通过：共同候选/许可 UI 分别 9/8/7 项，尾日志 7 项，手机路由与候选 UI 38 项，共 69 项。未展示窗口、未访问真机。40 应用、13 消费者、100 批的本地微基准，Full 临时分配 12,573,640→1,284,040 字节；不是整版内存、CPU 或加载速度结论。
- 已构建 Full x64 / 1.31.0.0 ReadyToRun 单 EXE，更新父容器正常运行版；SHA-256 `750AB038313061EFC86D46467BC25E22AD8389CE08E65E81ABEE1DED89D7B6F3`，26,842,481 字节。保留正在运行的 Store 1.30 和共享 ADB，未启动新包。既有 AndroidPcmSource.cs SmartVolume 调参仍未提交、Hash 未变，本地包包含该 WIP；无新 Release/tag/Store 提交，未重发 Lite/Legacy 运行包。
- 本地完整交接、失败检查记录与提交/推送收据见父容器 `integration-docs/38-MEMORY-FOLLOWUP-AND-ONLINE-LICENSE.md`、`integration-docs/memory-followup-receipt.json` 和 `outputs/memory-followup-20261105/`。下文“仅审查”为上一轮历史状态，本轮仅执行上述两项。

## 本轮提交：1.31 已完成改动；新的内存优化仅审查

- 用户于 2026-11-05 要求提交当前已完成工作：统一1.31版本入口、公共音频路径优化、手机输出切换与拖拽返回提示及其定向检查；沿用上一轮构建和测试证据，不为提交重复整套构建。
- 仅提交本轮/上一轮明确完成的文件。接手前 AndroidPcmSource.cs 的 SmartVolume 曲线调参继续留在本地，未纳入本次代码提交；现有本地运行包包含该WIP，不能当作本次纯提交发行包。
- 用户随后撤回手机图标优化；图标实现保持现状。其他内存机会仅做只读审查，不实施、不构建或变更回收策略，性能结论仍需单独测量。README/Wiki本轮未改，未创建Release/tag或商店提交。
- 本节为最新状态，下面“尚未提交”的描述保留为相应构建/检查时的历史记录；最终提交及远端状态以Git和父容器当前交接收据为准。

## 当前交付：1.31 手机切换与拖拽返回提示

- Full x64 单 EXE 已按用户要求构建并更新父容器运行版，版本 1.31.0.0；源代码及上一轮架构改动均未提交/推送，正式 Release/tag/Store 仍为 1.30。未重建 Lite/Legacy、未启动新版或结束用户旧实例。
- 播放端独立传输和端口建立并行、捕获与手机渲染准备重叠；失败路径等待迟到资源再清理。播放起步门槛为一块20ms PCM，保留缓冲容量、低延迟轨道及实际设备匹配校验；切换时暂停并丢弃旧缓冲，在最终路由就绪后恢复。捕获退出提前复用精确 PID/scid 验证后的终止请求，不结束共享 ADB。无新增闲时轮询。
- 手机面板在整个手机应用拖拽期间显示“返回手机播放 / 松开后，由手机控制声音”，复用不透明外层、主题和卡片标签方法，取消/结束恢复列表；放回调用既有 ReturnAndroidToPhoneAsync。
- 离线启动生命周期 11、输出语义 9、拖拽/设备 UI 35 项通过；小米13及蓝牙 3轮零 PCM 路由确认，中位数首次721.3ms、蓝牙711.7ms、扬声器1011.3ms（修改前1000.6/835.5/1967.7ms），不是声学起音或通用性能承诺。最后一次真实捕获/实际输出/控制/回收9项检查允许静音通过。依据 [AudioTrack 起播接口](https://developer.android.com/reference/android/media/AudioTrack#setStartThresholdInFrames(int))。
- 已知边界：严格音乐检查收到静音；用户明确自行听音。中间一次蓝牙确认超过5秒，随后诊断一轮通过，原因未确定、没有放宽门槛，仍需用户反馈复现。详细本地记录为父容器 integration-docs/37-PHONE-OUTPUT-LATENCY.md。源 SmartVolume WIP 保留、未归入本轮优化。

## 当前开发：1.31 架构精简与 Wiki 校对

- 源码版本已升 1.31，统一由 Directory.Build.props 导入 Version.props，手机音频程序集也继承 1.31.0.0。正式 Release / 标签 / 普通运行版仍为 1.30，本轮没有新发行或商店构建。
- ApplicationAudio 的批量静音复用一次目标与状态快照；手机相对音量 / 静音只解析一次目标。AndroidAudioManager 的有序快照与身份索引在成员变化时一起发布，避免每次操作创建谓词并线性扫描；不增加后台轮询。
- Full / Lite / Legacy 构建通过；七个项目目标组合实际文件版本均为 1.31.0.0。批量操作 net10/net48 各 6 项、Android 模块 120 项、手机路由与公共控制 UI 35 项离线检查通过。两源查找微基准从 12.709ms / 30,000,040 字节临时分配降至 3.628ms / 40 字节（250,000 次，5 轮中位数）；不是整版内存或 CPU 结论。
- 详见 [ARCHITECTURE-REVIEW-1.31.md](ARCHITECTURE-REVIEW-1.31.md)。用户后续授权同步优化 Wiki 的技术实现页，Wiki 提交 `5bd32c90dcbf842ad26ed4576f0930e207a465aa` 已推送并核对；README 和其他 Wiki 页未改。源代码尚为本地未提交开发改动。
- 接手前 AndroidPcmSource.cs 的 SmartVolume 调参保留，Hash 与上轮一致；它不属于本轮改动。运行中的程序、共享 ADB、活动手机音频与虚拟屏均未停止；没有新真机或整版性能验收。

## 当前正式发布：v1.30

- 用户明确授权正式发布及构建微软商店包。GitHub `v1.30` 已公开为稳定 Latest，标签固定指向 `1079f743618981972dfb620ddae47ecec82ad2fd`；7 个附件均下载回读并与本地 SHA-256 匹配。后续交接文档提交不移动发行标签。
- 便携附件为 Full x64/ARM64、Lite x64/ARM64 的单 EXE，以及 Legacy x64 的 13 文件 ZIP，另有校验文件与更新元数据。NET8 不生成新版。Full/Lite 未启用程序压缩；ARM64 仍为实验性。
- 商店包为 **Full x64 / 1.30.0.0**，沿用商店身份；MakePri/MakeAppx 成功，包内 EXE 与正式 Full x64 相同。MSIX SHA-256：`3FD6171CAD9C1EC0A933D0161AF0162DD81BE0EAF25CC636C0E76403A6DED113`。包尚未签名、安装或上传商店，不能写成商店已上架 1.30。
- 正式候选在独立、干净的已提交源码副本构建；接手前已有的 `AndroidPcmSource.cs` SmartVolume 调参保留在开发工作区，**不包含在正式包或发行提交中**。正常本地入口已更新为父容器 `运行版/SonicRoute.exe`，SHA-256：`EB96F8BC435B017EE5AFA3D8E2582D0B5E919C2E09F7ECA279EA34E41292C16A`。正在运行的旧测试版和共享 ADB 未结束。
- 必要检查完成：78 个发布流程场景、Full/Lite 版本隔离、真实更新包暂存与回滚、NET10/net48 更新器及 33 组工作进程场景。工作进程首次在继承到非规范 ACL 的工作目录中失败；保留失败记录，在用户 TEMP 的新隔离目录重跑全部 33 组通过，没有修改项目目录权限。没有新增真机、人工听感、原生 ARM64 或商店安装验收。
- 已按 `v1.29..v1.30` 真实代码提交补齐 `docs/release-v1.30.md`、GitHub Release `v1.30` 在线说明正文、`SonicRoute.wiki/09-版本历史.md`、`docs/readme/generate.cjs` 与 `README.ai.md`（涵盖 Full 手机音频/虚拟屏/智能音量/峰值条，以及桌面端快速面板紧凑与完整布局设置、滑块命中区域加大、自动化新增动作 37/38 当前应用输入静音与分组展示、配置导入预览弹窗、9 语言“播放/录音”术语统一、外层阴影移除、关于页“开源许可”文字链接、音量合并写入与异步配置保存等）。Lite/Legacy 不显示“开源许可”文字链接；Full 保留该入口及内嵌声明。
- 本地完整收据与交接见父容器 `integration-docs/35-RELEASE-1.30.md`、`outputs/official-release-20261104/`，商店交付在父容器 `商店包/`。继续开发先核对实际 Git 和本地未提交调参，不将开发工作区误认为干净发行源。

## 历史界面调整

“许可与第三方声明”独立按钮已改为作者主页同一行末尾的“开源许可”Hyperlink，复用主题颜色，继续按需打开内嵌许可；Full 构建通过。新版在固定 `测试版/Full-x64/SonicRoute.exe`。顶层同名版本文件仍由用户运行，本轮保留，详见本地 `integration-docs/34-ABOUT-LICENSE-LINK.md`；此前“两个入口是相同文件”的记录仅描述上轮交付。Lite/Legacy 仍不增加手机许可入口。

## 上轮手机输出修复


- **选择手机输出继续接管，退出才交还手机**：快速面板与完整界面的手机应用选择源手机扬声器或蓝牙时，保留外部会话及音量、静音、智能音量控制，不再返回手机或关闭虚拟屏。快速面板“退出”和拖回左侧手机面板复用 `App.ReturnAndroidToPhoneAsync`，结束接收并处理对应虚拟屏。此前“选手机输出就返回手机”的语义已撤销。
- **明确选择扬声器，避免误走默认蓝牙**：`AudioOutputBridge` 为自己的 AudioTrack 建立会话范围的输出策略，并请求低延迟播放，避免与另一应用的蓝牙深缓冲输出合并；只有实际设备与所选目标匹配才解除静音并确认切换。未连接、路由失败或超时会报错/停止流转，不宣称切换成功。删除“仅有一个蓝牙就替代未知目标”的兜底。
- **复用播放连接**：`AndroidPcmOutput.cs` 集中管理帮助进程、精确端口转发、设备通知、目标确认与清理，电脑进程流转和入站手机应用共用。手机播放端消费原有 PCM 队列经过音量、静音与 SmartVolume 后的数据，不伪装 Windows PID。扬声器/蓝牙切换复用原连接、捕获与解码器；原来同时播放的会话选择手机输出时，仅在原生复制模式需要改变时重建一次捕获，源对象/ID 与控制偏好保持。
- **性能边界**：手机播放端由 PCM 到达事件唤醒，复用固定缓冲，没有新增闲时扫描或持续定时器；关闭播放释放自己拥有的连接，保留共享 ADB。手机应用仍受电脑控制且从手机播放时，需要额外发送 48kHz/16bit/双声道 PCM（约 192,000 B/s），不能说成零开销。
- **已有功能保留**：固定应用直显、Android 实时峰值条、默认 SmartVolume、Full 保护活动连接/音频/虚拟屏、Lite/Legacy 的 1.29 单次工作集回收及 Store 固定 Full。

## 必要验证与交付

- `AndroidUiChecks --speaker-routing`：31 项，实际快速/完整界面输出控件保留会话，退出才移除；保留改名、智能音量及峰值条检查。
- `AndroidAudioChecks --explicit-output`：9 项，复制模式改变、控制保留、异机路由拒绝、持久化输出与停止清理。
- `AndroidAudioChecks --phone-demand`：21 项；`--device-actions`：39 项，覆盖 PCM/按需加载与既有电脑流转租约生命周期。
- 真机 `--explicit-output-live`：9 项，小米13波点音乐，实际扬声器→蓝牙→扬声器、非静音音乐数据、静音后只发送静音 PCM、音量/智能音量/身份保留、切回 PC 与退出清理；包含从原本同时播放的会话切换。
- 真机 `--pc-to-phone-live`：9 项，Windows 进程回环非静音音频、实际扬声器→蓝牙→扬声器、复用静音租约、清理帮助进程/文件/端口。以上共 118 项通过，模拟和真机证据分开保存。
- 已更新父容器固定的 `测试版/SonicRoute-v1.30-x64.exe` 与 `测试版/Full-x64/SonicRoute.exe`：Full x64 ReadyToRun 单 EXE、未启用程序压缩。普通运行版、Lite 与 Legacy 未改，没有另建版本目录。
- 接手时已有 `AndroidPcmSource.cs` 智能音量增益调参未提交。本轮保留原有内容，仅追加 PCM 到达通知；本次提交不包含原有调参，本地测试包包含它。请勿把工作区或运行包当作纯 Git HEAD 构建。
- 未做：人工听感、整版长期 CPU/内存基准、其他机型/Android 版本及 ARM64 验收。实测设备输出与数据流不等于这些结论；未发布 Release/tag/Store 包。

## 后续接手

1. 唯一源码在父容器“当前项目”。先读根 AGENTS.md、父容器 `AI_CONTEXT.md`、本文件、[WORKLOG.md](WORKLOG.md) 及本地 `integration-docs/33-EXPLICIT-PHONE-OUTPUT.md`，核对 Git、未提交改动、运行进程和收据。
2. 不能恢复“选择手机扬声器/蓝牙就退掉主面板会话”的旧逻辑。退出和拖回手机面板使用同一操作，不能把外部手机源交给 Windows PID 路由。
3. 每轮记录要求、改动原因、取舍、必要验证、未测项、构建/提交/推送状态，更新本文件与父容器入口。私有设备信息、配置、原始日志与运行包留在本地。
4. 优先性能和按需加载，关窗不释放活动音频、连接或虚拟屏。Lite/Legacy 排除手机组件，商店使用 Full；不恢复已取消的“电脑应用拖出后切到窗口”。
