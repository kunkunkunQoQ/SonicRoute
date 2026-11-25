# 主窗口及全部页面

冻结提交为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。WPF 路径相对于 `SonicRoute/`，Native 路径相对于 `Native/Product/Views/`。符号及行号用于定位。下表以源码审查区分实现状态，系统输入、截图与用户实机验收见本轮验证报告。本表覆盖冻结页面及下方索引中的控件，硬件结果按实际设备条件另验。

|窗口、页面及控件|WPF 冻结源码|Native 源码|视觉及布局差异|行为、功能及处理结果|优先级 / 来源 / 验证|
|---|---|---|---|---|---|
|主窗口、标题、更新、三枚窗口按钮|MainWindow.xaml:1017、1059|MainWindowShell.cs:25、481、525|标题 13.5/600，按钮 46×30。原生使用路径图标及分层表面，字形、阴影和窗口边缘仍有差别。|接入共享 Tab 焦点及 Enter/Space，复用实际窗口命令和页面动作。鼠标标题拖曳和点击维持已有生命周期。|P1 / 共享、部分还原 / 源码审查|
|六项导航、条件可见、展开收起|MainWindow.xaml:26、1096；AnimatedExpandHost.cs|MainWindowShell.cs:405、445、547|恢复首行 2 DIP 上边距。保持 196 DIP 固定子布局、动态可见宽度和裁剪。|侧栏与页面继续共用动画几何，末帧停调度。实验入口受解锁和启用条件约束。|P1 / 共享、已修 / 源码审查|
|应用侧栏、详情页、图标、菜单|MainWindow.xaml:1136、1176；MainWindow.AppIcons.cs|AppsPageView.cs:235、259、414、765|修正应用栏和右侧详情的 8 DIP 分隔重复计算。图标、PID、命名和详情状态由当前音频来源提供。|应用列表键盘激活复用稳定的应用身份选择，原有右键菜单及图标导入/还原保留。不同真实进程的封面、标识和刷新后选中需配合样本。|P1 / 页面、已修 / 源码审查|
|应用路由、输入/输出、多设备选择、音量|MainWindow.xaml:1239、1263；MainWindow.AppRouteButtons.cs、MainWindow.Volume.cs|AppsPageView.cs:414、635、772|有滚动条时按收窄后的宽度重新测详情内容，设备按钮换行后的内容高度同步。字体度量与 WPF WrapPanel 在临界宽度仍可不同。|应用音量支持方向键、Home/End、PageUp/PageDown，沿用现有音量写入。Ctrl/Shift、乐观状态和异步失败回滚路径保留。|P1 / 页面、已修 / 源码审查；设备切换待实测|
|自动化列表与编辑器、保存和取消|MainWindow.xaml:396、466、540；MainWindow.AutomationEditor.cs:36|AutomationPageView.cs:209、235、437、1268、1497|恢复全宽列表、全宽编辑器和显式切换，消除旧候选打开即显示两栏的挤压。|新建/模板/编辑进入编辑器。保存和取消回到列表。不再自动编辑首条。增加行内编辑、复制、删除入口，保留右键及原生收藏/导入入口。|P0 / 页面、已修 / 源码审查|
|自动化筛选、触发、步骤、排序|MainWindow.xaml:419、466、492、529；MainWindow.AutomationLayout.cs:247|AutomationPageView.cs:268、437、758、1168|按冻结顺序显示搜索、状态、触发类型和选项。步骤仍为手工布局，自动聚焦名称、部分行内按钮位置以及长说明换行未完全对齐。|搜索与两类筛选可叠加，重置筛选恢复全部。延时数值 Enter/失焦写回编辑草稿。拖动及 Esc 路径保留。完整触发/动作组合未逐项执行。|P1 / 页面、部分还原 / 源码审查|
|自动化模板、配置导入预览|MainWindow.AutomationTemplates.cs:25、67；MainWindow.ImportPreview.cs|AutomationPageView.cs:1288；SettingsPageView.cs:1328；ExperimentalPageView.cs 的 ImportConfigFromPath；NativeDialogs.cs:112|恢复模板说明顺序和草稿重名后缀。导入预览使用系统信息图标、确定/取消、默认确定，与冻结消息框一致。|两处原生导入已有快捷键/设备别名/应用别名/缺失设备/缺失应用五项计数，复用现有统计。|P1 / 页面、已核对 / 源码审查；未写真实配置|
|执行历史操作与独立窗口|MainWindow.ExecutionHistory.cs:49、135|ExperimentalPageView.cs:108；MainWindowShell.cs:182；ReadOnlyWindow.cs:28、162、217|900×560，最小 680×400，随主窗口居中，列表/详情 42:58。Win32 ListBox 单行显示时间、名称、状态与来源，与 WPF 双行样式有差别。|实验开关控制可见性，限制 1–64 MB；打开、刷新、清除、选择行、复制只读详情。单实例，关闭、禁用或再次刷新可取消先前读取。明细保留步骤、原因、耗时及目标，Android 未命名目标可能显示包名。|P0 / 功能入口、已修 / 源码审查；真实历史回放待实测|
|快捷键分组、录入、冲突|MainWindow.xaml:915；MainWindow.xaml.cs 的 BuildHotkeyList|HotkeysPageView.cs:53、217、275、306|移去顶部额外标题，恢复提示后直接出现快捷键分组。原生重置按钮仍在页尾。|系统动作/规则录入、清除及冲突提示保留，支持修饰键、鼠标中/侧键和滚轮；与被其他程序占用的全局按键共存待测。|P1 / 页面、已修 / 源码审查|
|主题模式、强调色、预览、透明度|MainWindow.xaml:560、590、611；MainWindow.ThemeLayout.cs:80|ThemePageView.cs:81、135、258、729|色样、Hex、取色按钮恢复为第一行，原生三个快速配色保留在下方。预览及径向阴影仍有原生绘制差异。|试色保持在取色器，确认后更新一次配置，取消恢复。透明度键盘调整与画面同步。|P0 / 页面、已修 / 源码审查|
|主题页 OSD 与面板|MainWindow.xaml:637、664、707|ThemePageView.cs:273、398、602、643；ExperimentalPageView.cs:318|字号 0.7–2.0，面板高度 350–850，宽度 180–600。小尺寸时，自绘卡片边距和长文本行高与 WPF 自动布局存在差异。|字号/宽度/淡入淡出/面板高度支持方向键、Home/End 和 PageUp/PageDown。相应原生实验数值入口同步字号上限，失焦保存。|P1 / 页面、已修 / 源码审查|
|设置分组、设备过滤与别名、默认应用|MainWindow.xaml:749、772、791；MainWindow.SettingsLayout.cs:75|SettingsPageView.cs:126、282、541、726、784、1226|首屏恢复显示设备、设备名称、默认控制的应用。恢复系统默认端点与折叠条件。固定长度、文本基线、长名称换行与 WPF 有别。|别名输入和可见性以设备 ID 命中；失焦、Tab 和 Enter 可提交。原生编辑框按需创建。设备命名保存与 WPF 延迟写入的时间粒度仍有区别。|P0 / 页面、已修 / 源码审查|
|启动选项、音量步长、实验解锁|MainWindow.xaml:804、814、825；MainWindow.SettingsSave.cs|SettingsPageView.cs:354、1226|按基准将启动和音频开关放在后续设置组。旧候选实际已有数值框。|步长框修正失焦提交和数值归一化，编辑取消不触发回调。保留托盘/启动/本机自启动作用域。登录后真实自启动未验。|P1 / 页面、已修 / 源码审查|
|语言、外部语言、更新、关于与许可证|MainWindow.xaml:831、854；MainWindow.Licensing.cs:19；MainWindow.Updates.cs|SettingsPageView.cs:177、877、1557；ReadOnlyWindow.cs:28|语言列表补上每份外部语言的独立编辑、保存、删除。许可证恢复 800×600、最小 400×300 及项目版权头，正文为系统可选中、换行、多行只读 EDIT。|许可模态居中，Esc 关闭；外部语言通过现有语言服务保存/删除；关于、更新和原生维护入口保留。许可文字渲染与 WPF 字体抗锯齿有差异。|P1 / 功能入口、已修 / 源码审查；外部语言文件变更待实测|
|实验页配置、后台轮询、记录开关|MainWindow.xaml:933、951、977；MainWindow.ExecutionHistory.cs|ExperimentalPageView.cs:63、108；SettingsPageView.cs:1328|恢复后台扫描回退、执行历史、配置导入/导出/清理三卡在前的顺序，去除无基准的首屏提示。已有原生维护操作位于其后。|记录开关和限制保存失败时回滚。保留后台服务所有权。导入预览与两版本共用的统计见上。|P1 / 页面、已修 / 源码审查|
|Full 已连接设备、音视频设置、固定应用和应用目录|PhoneDevicePage.xaml:144、185、247、484；PhoneDevicePage.xaml.cs 的 AddApps_Click|PhoneDevicePageView.cs:274、498、954、1924；PhoneDevicePageView.Catalog.cs:46|恢复独立目录视图和搜索、完成、状态提示，设备详情仅列出固定应用。行按实际滚动视口显示。固定按钮和状态图标仍用原生样式。|取消旧候选前 60 项显示限制。按名称/包名筛选、Ctrl/Shift/Ctrl+A、右键批量添加及单项固定通过相同 profile 的已有业务入口。退出或设备改变取消读目录；设备名随键入更新。|P0 / 功能入口、已修 / 源码审查，在线设备待实测|
|Full Android 文件、传输与剪贴板|PhoneDevicePage.xaml:261、377；PhoneDevicePage.AndroidFiles.cs|PhoneDevicePageView.cs:547；PhonePreview.cs、PhoneAudioWindowView.cs|设备页现有文件选择、接收文件夹、传输状态和剪贴板入口。独立窗口与设备页的形态差别详见窗口矩阵。|维持由后台服务持有传输。受实机条件限制，权限拒绝、传输失败和关窗期间继续传输需运行证据。|P1 / 现有实现 / 源码审查，设备待测|
|Full 添加设备、二维码、配对码、直连、USB|PhoneDevicePage.xaml:389、428；PhoneDevicePage.DirectConnection.cs、PhoneDevicePage.Pairing.cs|PhoneDevicePageView.cs:725、958|两种配对和直连入口已有，原生输入使用按需 EDIT；二维码与状态布局依设备响应。|修复由地址/配对码点击到连接按钮时回退为进入编辑前内容。复用原有连接所有者。网络失败重试、USB 恢复需设备。|P1 / 页面、已修 / 源码审查，设备待测|
|Full AirPlay 接收、媒体、镜像|AirPlayReceiverView.xaml:32、55、89、102、119；PhoneDevicePage.AirPlay.cs|AirPlayReceiverViewControl.cs:108、149、316、331；PhoneDevicePageView.cs:725|接收名、解码、验证码、封面和镜像状态使用已有控件与动态布局。Lite 编译时排除整个手机功能。|修复接收名改完点击下一控件不保存。配置和运行状态沿用已有 AirPlay 服务；外部设备端发现、镜像或连续播放未验证。|P1 / 页面、局部已修 / 源码审查，设备待测|

### 输入提交与资源生命周期

旧候选的 `InlineEditHost.Close` 仅在提交时调用 `Committed`。回调最后一个参数表示是否按 Enter。设备别名、固定应用、规则延迟、OSD 数值、配对地址与接收名原先在正常失焦时把这个参数当作取消。本轮修复这些调用者。Esc 保留宿主原有的取消路径。设备别名的命中键改为设备 ID，避免刷新排序后编辑到另一端点。

历史与许可在用户打开时创建。关闭销毁所属 Win32 控件、字体和画刷并取消窗口自己的历史读取；后台规则服务继续运行。手机目录按需取数，只对可见行绘图；离页解除手机/传输订阅与只为页面提供数据的观察任务。规则列表不再为显示步骤数量而反复克隆每个步骤。主页面未加入逐帧轮询。

主页面代码已经通过 Full Release win-x64 增量编译（0 警告、0 错误）。产品 Native AOT 发布、系统输入验证、九种语言的实际覆盖和数据采样以本轮总报告为准。未在本阶段测试完整 IME 组合、全部快捷键冲突、每个规则动作或硬件联机交互。

## 冻结 XAML 控件索引

按源文件和起始行列出具名元素，含动态容器和模板部件。它用于核对上表覆盖范围；模板中的命名不单独代表一个业务功能。


- MainWindow.xaml L396　AutomationPage、AutomationScroll、AutoListPanel、AutoNewButton、AutoTemplatesButton、AutoSearchBox、AutoStateFilter、AutoTriggerFilter、AutoListOptionsButton、AutoEmptyText、AutoRuleList、AutoEditCard、AutoEditTitle、AutoTemplateHintText、AutoNameBox、AutoTriggerCombo、AutoHotkeyPanel、AutoHotkeyCapture、AutoHotkeyHintText、AutoTriggerAppPanel、AutoTriggerAppCombo、AutoSchedulePanel、AutoScheduleModeCombo、AutoScheduleHourCombo、AutoScheduleMinuteCombo、AutoScheduleWeekdayPanel、AutoWeekdayPanel、AutoStepsHost、AutoAddStepBtn、AutoCancelButton、AutoSaveButton
- MainWindow.xaml L560　ThemePage、ThemeSystem、ThemeLight、ThemeDark、ThemeColorPreview、ThemeColorHex、ThemeChooseColor、OpacityText、OpacitySlider、OsdAdjustBtnTheme、OsdResetButtonTheme、OsdVolumeVisualCheck、MicMutePersistCheck、MicMuteTrackInputCheck、OsdMoreToggle、OsdSlidersPanel、OsdWidthSlider、OsdWidthValue、OsdFontSlider、OsdFontValue、OsdFadeInSlider、OsdFadeInValue、OsdFadeOutSlider、OsdFadeOutValue、PanelPosAdjustBtn、PanelPosResetBtn、PanelHeightSlider、PanelHeightValue、AppPeakMeterCheck、PanelLegacyRowsCheck、PanelCompactOutputNameRow、PanelCompactOutputNameCheck、PanelOutputNameRow、PanelOutputNameCheck
- MainWindow.xaml L749　SettingsPage、KeepDevicesMoreToggle、KeepDevicesBody、SelectAllOutputButton、OutputFilterList、InputFilterHeader、SelectAllInputButton、InputFilterList、DeviceNamesMoreToggle、DeviceNamesBody、OutputNameList、InputNameHeader、InputNameList、DefaultAppRecent、DefaultAppLast、DefaultAppFixed、FixedAppCombo、SettingsStartMinimized、SettingsAutoStart、SettingsMoreToggle、SettingsMorePanel、CleanAutoStartBtn、SettingsShowPanelOnStart、VolumeStepBox、SettingsTrayWheelEverywhere、ExpMicOptionCheck、ExpMicPanelCheck、SettingsPanelChangeSysDef、SettingsPanelOutputVolumesRetainedOnly、ExpCollapseCheck、SettingsExperimentalMode、ExperimentalModeHint、LangCombo、LangMoreToggle、LangMorePanel、ExpExportLangButton、ExpImportLangButton、ExpOpenLangDirButton、ExpRestoreLangButton、CustomLangSection、CustomLangList、UpdatesHost、AboutAuthorText、BiliLink、IfdianLink、GithubLink、LicenseLink
- MainWindow.xaml L915　HotkeysPage、HotkeyList
- MainWindow.xaml L933　ExperimentalPage、ExpBackgroundPollingCheck、ExpHistoryCheck、ExpHistoryOptionsHost、ExpExportConfigButton、ExpImportConfigButton、ExpClearConfigButton
- MainWindow.xaml L1017　WindowShadow、RootBorder、TitleBarGrid、HeaderTitleHoverBg、HeaderTitleText、UpdateNoticeButton、UpdateNoticeText、TitlebarMaxButton、MainNavHost、MainNavSidebar、NavApps、NavHotkeys、NavTheme、NavSettings、NavAutomation、NavExperimental、MainContentHost
- MainWindow.xaml L1136　AppsPage、AppsSidebarContent、AppsRefreshButton、AppsListBox、AppsDetailsScroll、AppsIconPreview、AppsDetailTitle、AppsRenameBox、AppsRenameButton、AppsDetailPid、IconImportButton、IconResetButton、AppsRoutingPanel、AppsOutputCard、AppsOutputCombo、AppsOutputQuickPanel、AppsInputCard、AppsInputLabel、AppsInputCombo、AppsInputQuickPanel、AppsMuteButton、AppsVolumeText、AppsVolumeSlider、AppsControls、AppsDisableAutoButton、AppsShowInPanelButton、HotkeysPageHost、ThemePageHost、SettingsPageHost、ExperimentalPageHost、AutomationPageHost
- PhoneDevicePage.xaml L1　Row、Icon、Size、CheckBox、Check、Next、OpenFolder
- PhoneDevicePage.xaml L144　DeviceScroll、DeviceCard、DeviceName、PhoneNameBox、RenameButton、DeviceDescription、DeviceCommands、ReconnectButton、DisconnectButton、UnpairButton、RemoveDeviceButton
- PhoneDevicePage.xaml L185　StreamingCard、AudioEncoderCombo、AudioDecoderCombo、AudioBitRateCombo、AudioLatencyCombo、VideoEncoderCombo、VideoDecoderCombo、VideoBitRateCombo、VideoFpsCombo
- PhoneDevicePage.xaml L247　RetainedPanel、RetainedCount、RetainedApps、RetainedEmpty、AddAppsButton
- PhoneDevicePage.xaml L261　FileTransferCard、AndroidFilePicker、AndroidFileUp、AndroidFilePath、AndroidFileCancel、AndroidFileEntries、AndroidFileEmpty、AndroidFileGet、AndroidBrowseFiles、SendFileButton、FileDropLabel、FileStatus、FileProgressRow、FileProgress、AndroidReceivePath、AndroidChangeFolder、LocalReceiveFolderPath、ChangeLocalReceiveFolder
- PhoneDevicePage.xaml L377　ClipboardCard、ClipboardStatus、ClipboardSyncToggle
- PhoneDevicePage.xaml L389　ConnectionPanel、AndroidConnectionChoice、AirPlayConnectionChoice、AirPlayCard、AirPlayConnectionView、PairingCard、PairMethodsGrid、QrPairCard、QrButton、PairQrImage、QrStatus、CodePairCard、PairAddressBox、PairCodeBox、PairButton
- PhoneDevicePage.xaml L428　DirectConnectCard、ConnectAddressBox、SavedConnectDevices、SearchSavedDevicesButton、ConnectButton、SavedDeviceSearchStatus、RestoreUsbDevices
- PhoneDevicePage.xaml L484　CatalogPanel、CloseCatalogButton、CatalogSearch、CatalogStatus、CatalogApps、AddSelectedAppsMenu、OperationStatus
- AirPlayReceiverView.xaml L1　ReceiverHeader、HeaderStatus、ReceiveStatusDot、ReceiveStatusLabel、AirPlayToggle、ReceiverPreferences
- AirPlayReceiverView.xaml L55　PreferenceFields、ReceiverName、DecoderField、MirrorDecoder、RequireCode
- AirPlayReceiverView.xaml L89　AirPlayStatusText、CodePanel、ReceiveCode、AirPlaySteps
- AirPlayReceiverView.xaml L102　TrackPanel、ArtworkPlaceholder、ArtworkImage、TrackTitle、TrackArtist
- AirPlayReceiverView.xaml L119　MirrorControls、ShowMirror、MirrorTopmost、MirrorFullscreen
