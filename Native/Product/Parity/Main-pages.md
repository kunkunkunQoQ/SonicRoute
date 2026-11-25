# 主窗口及全部页面

基准为本地 Git 对象 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。下表 WPF 路径相对于 `SonicRoute/`，Native 路径相对于 `Native/Product/Views/`。行号为审查时的起始行。分组基于冻结 XAML 的页面及动态创建代码。入口存在只代表源码可追踪；多语言渲染、设备返回值、IME 和操作结果另需证据。

|页面与控件|WPF 基准|原生实现|视觉、行为与缺口|优先级 / 实施状态|
|---|---|---|---|---|
|主窗外壳、标题、更新、最小化/还原/关闭|MainWindow.xaml:1017、1059；MainWindow.xaml.cs 的 HeaderTitle 与 Titlebar 处理|MainWindowShell.cs:388、441|主标题 13.5/600，标题栏按钮 46×30 对应。WPF 用字形和窗口阴影，Native 用路径图标和分层表面；检查标题双击、最大化边缘和点击还原需系统输入。|P1 / 字形及边距待校准|
|主窗导航、条件页、折叠|MainWindow.xaml:26、1096；AnimatedExpandHost.cs|MainWindowShell.cs:405、507、641|六个导航项顺序一致，实验项有解锁条件。固定 196 DIP 子布局与动态命中剪裁已保留。WPF 40 DIP 行上下各 2 DIP，Native 首行偏 2 DIP。|P1 / 待校准|
|应用侧栏、详情、刷新、空选中、图标|MainWindow.xaml:1136、1176；MainWindow.AppIcons.cs|AppsPageView.cs:235、259、414、1133|列表选择、图标导入/还原、详情信息与右键菜单已接入。左栏基准列宽含 8 DIP 右边距，Native 在列外再加 8 DIP，详情偏右。空选中禁用条件及无来源标签的 PID 显示需核对。|P1 / 待校准|
|设备按钮、快捷输出/输入、多选及音量|MainWindow.xaml:1239、1263；MainWindow.AppRouteButtons.cs、MainWindow.Volume.cs|AppsPageView.cs:414、1371|有 Ctrl/Shift 和乐观状态、错误恢复及 0–100 音量；录音列条件由实验麦克风决定。WPF 包装设备按钮/输入组合框，Native 手工流式排版与文本测量；滚动条出现后 pill 换行可能改变总高度。|P1 / 待校准布局；路由业务保留，设备待实测|
|自动化列表、非编辑态、编辑/保存/取消|MainWindow.xaml:396；MainWindow.AutomationEditor.cs:36；MainWindow.AutomationLayout.cs:38|AutomationPageView.cs:201、219、250、540、1375、1601|WPF 列表和编辑器各占全宽、显式切换；Native 初次选中首项并同时显示左右栏，窄窗压缩了步骤。WPF 列表行有编辑、复制、删除等按钮；Native 主要从右键进入。|P0 / 待还原|
|自动化搜索/状态/触发筛选、触发器、步骤、排序|MainWindow.xaml:419、466、492、529、540；MainWindow.AutomationLayout.cs:247|AutomationPageView.cs:250、761、861、1106、1725|有规则、步骤拖动、边缘滚动与 Esc；选择器、条件、参数由 Native 布局。WPF 编辑器在打开后显式聚焦规则名；拖动排序、跨页状态及所有参数组合尚未实机比对。|P1 / 部分等价|
|自动化模板、执行历史、导入预览|MainWindow.AutomationTemplates.cs；MainWindow.ExecutionHistory.cs；MainWindow.ImportPreview.cs|AutomationPageView.cs:417、1396、1704；ExperimentalPageView.cs:546|原生提供模板菜单及历史详情。配置导入为简版确认对话框，逐项检查及 WPF 模板/历史细节不能据此认定等价。|P1 / 待完善|
|快捷键分组、系统/规则录入、冲突|MainWindow.xaml:915；MainWindow.xaml.cs 的 BuildHotkeyList|HotkeysPageView.cs:53、220、270|基准从本地化提示直接进入分组；原生多一行标题及重置入口。保留录入、清除、规则编辑和冲突状态。鼠标组合、与正在占用的系统快捷键共存尚需实机。|P1 / 待调整布局|
|主题模式、强调色、预览、透明度|MainWindow.xaml:560；MainWindow.ThemeLayout.cs 的 ChooseThemeColor|ThemePageView.cs:81、135、258、688|模式与主题色可用；WPF 确认取色后一次更新，Native 预览试色就改共享配置。预览卡和控件使用手工几何，视觉近似。|P0 / 待修复确认时机|
|主题 OSD、面板高度和样式|MainWindow.xaml:637、664、707|ThemePageView.cs:273、398、604|OSD 宽度 180–600，字号 WPF 0.7–2.0、Native 至 1.6；WPF 显隐时间与面板高度按指定 tick，需检查 Native 写入的离散值。两种行样式的附加选项、位置拖动均有关联入口。|P1 / 待还原|
|设置卡片顺序、设备过滤/别名、默认应用|MainWindow.xaml:749、772、791；MainWindow.SettingsLayout.cs:75|SettingsPageView.cs:123、297、509、609、691|WPF 从设备显示及设备名开始，再选默认应用。Native 首屏合并启动和语言。设备名 WPF 在列表内可编辑，Native 需要点击后进入 HWND 编辑。别名、过滤均使用设备 ID。|P0 / 待恢复首屏信息顺序|
|启动、开关、音量步长、实验解锁|MainWindow.xaml:804、814、825；MainWindow.SettingsSave.cs|SettingsPageView.cs:193、370、1072|启动时隐藏/面板、自启动、麦克风选项和折叠设置均有入口。WPF 步长为数值框，Native 为滑块；原生候选独立自启动范围继续保留。|P1 / 待恢复数值编辑|
|语言、外部语言列表、维护、关于更新|MainWindow.xaml:831、854；MainWindow.Licensing.cs、MainWindow.Updates.cs|SettingsPageView.cs:193、784、839、1152、1520|语言选择、导入导出、还原、维护和更新入口已接入。基准外部语言文件逐个管理、更新和许可弹窗的富文本、链接布局需另外核对。|P1 / 部分等价|
|实验页后台行为、历史、配置传输|MainWindow.xaml:933、951、977；MainWindow.ExecutionHistory.cs|ExperimentalPageView.cs:62、394、546|冻结页面依次为后台轮询、历史、配置导入导出清理；原生多 Beta 提示及重复设置卡，且原生历史确认文案只区分中文/英文。额外原生入口须保留可达，基准的前三卡应先出现。|P1 / 待恢复顺序|
|Full 设备与串流、固定应用|PhoneDevicePage.xaml:144、185、247；PhoneDevicePage.Streaming.cs|PhoneDevicePageView.cs:245、266、496|存在设备命令、音视频编码参数、固定项、音量与启动显示。WPF 顶层 CatalogPanel 有搜索、勾选、统一添加，Native 与保留选项的布局、在线/离线刷新需真机逐条确认。|P1 / 部分等价，真机待测|
|Full 文件传输、文件选择、剪贴板|PhoneDevicePage.xaml:261、377；PhoneDevicePage.AndroidFiles.cs|PhoneDevicePageView.cs:545、1243|目录、传输、选择文件/目标文件夹和剪贴板开关可在源码定位。受工作区可用设备与系统权限约束，双向传输、进度和关闭页面期间持续传输尚未操作验证。|P1 / 源码接入，真机待测|
|Full Android 添加设备、二维码/配对码、直连、恢复 USB|PhoneDevicePage.xaml:389、428；PhoneDevicePage.DirectConnection.cs、PhoneDevicePage.Pairing.cs|PhoneDevicePageView.cs:723 及配对处理|两类配对与直接连接已接入；原生复用已有连接所有者。二维码、搜索状态和 Win32 输入框需联机检查，包括失败重试和取消。|P1 / 源码接入，真机待测|
|Full AirPlay 添加设备/接收设置、媒体与镜像控件|AirPlayReceiverView.xaml:32、55、89、102、119；PhoneDevicePage.AirPlay.cs|AirPlayReceiverViewControl.cs:108、149、316；PhoneDevicePageView.cs:723|状态、接收名、解码、验证码、封面、镜像控制有共享渲染；同设备状态和封面数据下的间距及状态切换未验。Lite 编译条件排除手机页。|P1 / 源码接入，真机待测|

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
