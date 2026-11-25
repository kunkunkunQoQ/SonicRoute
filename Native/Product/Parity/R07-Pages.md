# 主窗口控件执行与持久化矩阵

冻结行为取自 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。以下 WPF 文件位于 SonicRoute；Native 文件除注明外位于 Native/Product/Views。对应的冻结 XAML 具名控件和布局索引见同目录 Main-pages.md。每行最后一栏为源码核对结果，实际输入、系统状态和重启证据按总报告单独列出。一个动态 ID 模板覆盖该模板生成的全部行。表中的「配置」均指当前实例的 AppConfig，落盘用 ConfigService.TryCommit，重新启动由 ConfigService.Load 还原。自动化规则、Android 配置和系统状态各有独立所有者。

### 共同输入与窗口

|WPF 控件/行为|Native ID 与输入|实际分派和结果|保存、同步和失败处理|验证|
|---|---|---|---|---|
|MainWindow.xaml 标题、侧栏、拖动及双击|shell.titleToggle、shell.titleDrag，按下/移动/释放或双击|MainWindowShell.ToggleSidebar / BeginWindowMoveDrag / ToggleMaximize|侧栏收起写 MainSidebarCollapsed；窗位与最大化由窗口宿主处理|源码，拖动边界待实机|
|窗口按钮与更新通知|shell.min、shell.max、shell.close、shell.updateNotice，点按或焦点激活|MinimizeWindow、ToggleMaximize、Hide、OpenUpdateSettings → ScrollToUpdates|隐藏释放页面资源；更新跳转在设置布局完成后计算实际偏移|源码|
|导航|nav.Apps、Hotkeys、Theme、Settings、Automation、Experimental|NavigateTo → 按需创建页面；先提交普通失焦编辑，取消录制/拖动，关闭弹层|当前页临时，实验开关决定可见性|源码|
|ScrollViewer|各页 *.scroll、auto.list.scroll、edit.scroll，拖动/滚轮/滚动键|UiScrollState，命中与绘制同一裁剪；键盘 Tab 从 UiHitMap 取可见交互|位置运行时；失捕获终止拖动|源码|
|通用控件|各页 OnPointerDown/Up、ActivateFocused、OnFocusedKey|LayeredWindow → MainWindowShell → IViewPage，按下/释放须匹配；滑块、应用选择、菜单有专门事件分支|对话框取消、菜单未选择不会发出业务命令；IBeam 使用实际 EDIT；只读编辑框用于更新说明|源码|

### Windows 应用与路由

WPF 对应 MainWindow.xaml 的 AppsPage、MainWindow.xaml.cs、MainWindow.AppRouteButtons.cs、MainWindow.AppIcons.cs 和 MainWindow.Volume.cs。Native 入口为 AppsPageView.RenderLeftSidebar / RenderDetailsPane、OnPointerDown / OnPointerUp 和 ShowAppContextMenu。

|控件/冻结行为|Native ID/输入|命令与实际业务|持久化、同步和失败处理|验证|
|---|---|---|---|---|
|刷新应用|apps.refresh|TriggerRefresh(true) → Audio.Apps / Volume、RenderDevicesWithPhone、当前路由读回|临时，合并在途读取；按 identity 保留选择|源码|
|应用选中及多选|apps.item.{identity} 按下、键盘激活、Ctrl/Shift/Ctrl+A|HandleAppRowClick → SetPrimarySelectedApp / TriggerRefresh|选中临时；不同应用切换递增代数，旧回调不能清除新操作|源码|
|设备与手机页|apps.phone.add、apps.phone.{profile}、apps.phone.scroll|OpenPhoneConnection / OpenPhoneDevice → Store.SelectProfile；详情可返回设备或连接页|仅 Full，设备 profile 由 PhoneStore 管理|源码，硬件待验|
|改名|apps.rename；右键 Rename；EDIT 输入/失焦|AliasEditor.Open，以进程名为键，修改 AppNames。双入口同一提交路径|停键 300 ms 或失焦立即写；Esc 在防抖期限内能够丢弃未确认输入；保存失败提示|源码|
|导入/恢复图标|apps.icon.import、apps.icon.reset，文件拖放|ImportCustomIconFromPath / ResetCustomIcon → 稳定进程名、图标文件、TrySetAppIcon|AppIcons 映射与图标文件；系统对话框取消不写，失败不显示成功|源码|
|输出下拉与快捷端点|apps.out.combo、apps.out.dev.{id}；选择、Ctrl/Shift/组合多选|ApplyOutputSelection，经由 CurrentApp.SelectOutputAsync 或 SetOutputsAsync；原生路由写入与读回、镜像由共享业务处理。外部音频走 SetExternalOutputsAsync|Windows 音频策略和业务镜像存储。待定集合覆盖旧观测；同 PID 单/多选从输入线程按序进入同一队列。失败读回|源码，真实端点见总报告|
|输入下拉与快捷端点|apps.in.combo、apps.in.dev.{id}|SelectInputRoute → Audio.SetRoute(pid,Capture,endpoint)，每次按输入顺序提交|由 Windows 音频策略负责；待定时屏蔽旧快照；结束按身份/代数刷新|源码，真实设备待验|
|应用音量和静音|apps.vol.slider、apps.mute；拖动、滚轮、方向/Home/End，右键静音|QueueAppVolume → 主窗与面板共用宿主写入队列；Windows 以 PID，Android/AirPlay 以 ExternalSourceId 合并并串行写入；释放或失捕获 FlushVolumeWrites。静音走 Audio.Volume(toggle/mute)|系统或外部会话持有；写失败提示并按实际状态重读；成功按当前 identity 更新两个可见界面，丢弃旧读回。批量静音以打开菜单的主目标决定方向，核对真实读回，部分失败报告成功/失败数量|源码，实际音量见总报告|
|应用快捷控制、右键和批量|apps.ctrl.disableAuto、apps.ctrl.showPanel、apps.ctrl.resetRoute；菜单同名项、取消全部隐藏、重置路由|ToggleDisableAutoSwitch / ToggleShowInQuickPanel、SetBatchMembership / UnhideAllPanelApps / BatchResetRoutes|DisabledAutoSwitchApps / HiddenPanelApps 即时；混合显示状态按菜单勾选语义操作。批量路由复用单设备顺序队列，核对每个输入/输出结果并报告部分失败，两个界面重新读回。失败配置恢复|源码|
|设备与应用数量、滚动条|apps.phone.header、apps.list.header、apps.list.scroll、apps.details.scroll|标题提供提示，滚动条沿用对应 UiScrollState|临时，不写盘|源码|

### 设置、主题、实验

WPF 设置入口位于 MainWindow.xaml 的 SettingsPage、MainWindow.xaml.cs 的设置事件、MainWindow.SettingsSave.cs、MainWindow.Updates.cs 及 MainWindow.Licensing.cs。主题对应 ThemePage 和 MainWindow.ThemeLayout.cs，实验对应 ExperimentalPage 和 MainWindow.ExecutionHistory.cs。

|所属页面与冻结行为|Native ID/输入|实际分派、完成状态|保存、同步和失败处理|验证|
|---|---|---|---|---|
|设置/显示的设备|st.keep.toggle、st.keep.out.all、st.keep.in.all、st.devchk.{out/in}.{id}|展开/收起、整组或逐端点切换；遇到系统默认伪端点按其稳定 ID 保存|展开临时；HiddenOutputDevices/HiddenInputDevices 即时提交，托盘与面板重读|源码|
|设置/设备别名|st.names.toggle、st.devname.{out/in}.{id}、st.devchk.edit.{out/in}.{id}；笔形按钮/双击端点/输入|AliasEditor.Open，按 device.Id；未设置时留空；两类入口按各自的真实区域放置编辑器|DeviceNames，300 ms 防抖或失焦提交；删除别名恢复原名；写失败提示|源码|
|设置/默认应用|st.appmode.{recent/last/fixed}、st.fixedapp.combo|RenderDefaultAppCard / OpenFixedAppCombo → 当前模式和实际进程，非 fixed 不注册下拉命中|DefaultAppMode、FixedAppName 即时；显示已关闭/无匹配应用时保留已保存进程名|源码|
|设置/启动|st.startmin、st.startpanel、st.autostart、st.autostart.more、st.autostart.clean|ToggleAutoStart/CleanAutoStart 调隔离 HKCU Run 或 StoreStartup，回读实际启用；折叠只影响界面|StartMinimized、StartPanelOnStart、AutoStart/AutoStartStore；外部启动项由系统持有|源码，系统登录待验|
|设置/音量步长|st.volstep.box、st.volstep.slider；EDIT Enter/失焦、拖动/键盘/滚轮|整数归一 1–20；非整数按冻结默认 4；滑动预览、释放/失捕获收尾|VolumeStep；拖动一轮最多提交一次，输入结束/离散键轮提交|源码|
|设置/托盘|st.traywheel.everywhere、st.traywheel.paused|TrayWheelEverywhere → 重载实际钩子设置；暂停与托盘菜单复用 Host.SetTrayWheelPaused|TrayWheelEverywhere、TrayWheelPaused；读回钩子安装失败时恢复|源码|
|设置/麦克风与面板|st.expmic、st.micinpanel、st.panelchangesysdef、st.panelretainedonly、st.micmute.persist、st.micmute.trackdef|开关 → 实际 ViewSettingsChanged，切换麦克风监视、快捷面板与 OSD 源|ExperimentalMic、MicInPanel、PanelChangeSystemDefault、PanelOutputVolumesRetainedOnly、MicMuteOsdPersistent、MicMuteOsdDefaultInputPersistent，各自即时提交|源码|
|设置/展示和实验解锁|st.collapsedev、st.expmode、st.about.author|折叠设备组；五击作者解锁；模式影响实验导航和历史可见|CollapseDeviceSections、ExperimentalUnlocked、ExperimentalMode；未解锁不注册实验模式开关|源码|
|语言选择、导入与磁盘操作|st.lang.combo、st.lang.more、st.lang.export、st.lang.import、st.lang.dir、st.lang.restore|OpenLanguageCombo、ExportBuiltinLanguages、ImportLanguageFile、OpenExternalLangDir、RestoreBuiltinLanguagesAsync|Language 保存成功才应用；外部语言文件由 L10n 管理；文件操作返回值决定成功/失败提示，取消无写入|源码|
|自定义语言|st.lang.name.{code}、st.lang.save.{code}、st.lang.delete.{code}|编辑只更新 _languageEdits；保存调用 RenameCustomLanguage；删除调用 DeleteCustomLanguage 并更新选择|确认后更新语言目录/必要的 Language；文件错误保留提示|源码|
|配置维护|st.cfg.export、st.cfg.import、st.cfg.opendir、st.cfg.reset；exp.cfg.export/import/clear 和拖放|CaptureExportSnapshot / ExportTo；PrepareImport / ReadPreparedConfig → 预览确认 → FlushPendingSaves / ApplyImport；TryResetToDefault 成功后重启，删除失败提示并保留当前界面|导入或重置须二次确认；导出只写所选目标；保留已有安全备份|源码|
|更新|st.about.checkupd、autochk、changelog、dl、install、cancel、later、ignore、notes（共同前缀 st.about.）|CheckForUpdates / AutomaticSettingChanged / DownloadAsync / InstallReadyUpdate / CancelDownload / Dismiss；外链由 shell；notes 只读多行 EDIT|AutoCheckUpdates 即时；忽略/推迟由 UpdateService 管；正在执行/Store 状态决定是否注册控件；说明可选中复制|源码，真实发布安装未执行|
|关于与许可|st.about.bili、ifdian、github、license|OpenUrl 到冻结原链接或 ShowProjectLicense；许可使用只读 Win32 EDIT|导航与打开文档，无产品配置写入|源码|
|主题/模式和颜色|th.mode.{system/light/dark}、th.hex、th.chooseColor|用户本轮标注指定 3 张等宽模式卡，每张最多 136×46 DIP，间隔 8 DIP；3 个旧版额外预设色点继续移除。ThemeMode 选择；CommitHex 校验 RGB；取色器只在确认后 ApplyAccent|ThemeMode、Accent；提交后刷新可见主题；Esc/取消不写，非法颜色提示|源码|
|主题/不透明度与 OSD 滑块|th.opacity、th.osd.w/font/fin/fout、th.panel.h|ApplySliderDrag/OnFocusedKey，基于实际滑块几何，数值约束；不透明度实时绘制；宽度/字号松手预览|BackgroundOpacity、OsdWidth、OsdFontScale、OsdFadeInMs、OsdFadeOutMs、QuickPanelHeight；拖动结束/失捕获、键盘离散提交|源码|
|主题/OSD 开关与位置|th.osd.adjust/preview/reset、volvisual/micpersist/mictrack、pos.{code}、ox/oy、more|宿主 BeginOsdAdjust、PreviewOsd、ResetOsdSettings；九宫格和数值 EDIT 更新位置；more 展开临时|OsdVolumeVisualEnabled、MicMuteOsdPersistent、MicMuteOsdDefaultInputPersistent、OsdPosition、OsdOffsetX/Y；重置同时清物理坐标等；提交失败不显示重置成功|源码|
|主题/面板设置|th.panel.adjust/reset、posDef/posCus、cx/cy、peak、legacy、compactOut、fullOut、allOutVol|BeginQuickPanelAdjust 或改面板定位/行样式，以配置驱动真实面板；依旧保留上轮用户指定标题钉住行为|QuickPanelPosMode、QuickPanelCustomX/Y、ShowAppPeakMeter、QuickPanelUseLegacyAppRows、QuickPanelCompactShowOutputName、QuickPanelShowOutputName、QuickPanelShowAllOutputVolumes|源码|
|实验/后台及历史|exp.polling、exp.history、exp.hist.limit/view/clear|实际 watcher 设置；开关历史存储、限 1–64 MB、OpenHistory、确认后 ClearAsync|BackgroundPollingFallbackEnabled、ExecutionHistoryEnabled、ExecutionHistoryLimitMb；历史内容另存。写失败不触发历史业务|源码|
|实验/其他设置与重置|exp.mode、exp.mic/micpanel、exp.panelretained、exp.osd.volvisual/pos/w/font/fin/fout/preview/theme、exp.resetroutes|同主设置和主题字段，OSD 预览调用宿主、theme 导航；重置调用 Audio.ResetRoutes，按实际返回报告|配置同对应主设置；重置路由由 Windows/音频业务负责；运行时导航无写入|源码|

### 快捷键和自动化

冻结对应 MainWindow.xaml 的 HotkeysPage、AutomationPage，MainWindow.xaml.cs 的 BuildHotkeyList、PreviewKeyDown/PreviewMouseDown/PreviewMouseWheel、UpdateAutoHotkeyHint，及 MainWindow.AutomationEditor.cs、AutomationLayout.cs、AutomationTemplates.cs。Native 使用 HotkeysPageView、HotkeyBinding、AutomationPageView 和 ProductAutomation。录入方式与执行方式分别追踪。

|冻结控件/行为|Native ID/输入|实际命令、目标、完成状态|保存、同步、失败处理|验证|
|---|---|---|---|---|
|动作分组与录入|hk.act.{HotkeyActions action}.pill；点按、Tab 后 Enter/Space|StartRecordingAction → HoldHotkeys 暂时注销真正的全局键位；下一次键盘/中键/侧键/滚轮录入。Ctrl、Alt、Shift、Win 可组合；纯修饰键继续等待；Esc 清空；失焦/切页恢复注册|Hotkeys[action]，确认后 TryCommit → 暂停结束 → ReloadBindings → HotkeyService.Reload → RegisterHotKey 或 NativeMouseBindings → Dispatch → ProductAutomation.Hotkey。实际回退组合、未注册、空绑定分别呈现；失败保留可用配置|源码；实际执行见总报告|
|音量组，六项|同上；ActVolUp / ActVolDown / ActSystemVolUp / ActSystemVolDown / ActMute / ActMuteInput|当前应用音量增/减、系统默认输出增/减、应用静音与输入静音；操作目标由当前应用选择或实际默认端点确定|键位归 Hotkeys；业务状态归音频系统|源码|
|应用输出输入、全局及界面组，八项|ActSwitchOutput / ActSwitchInput；ActSwitchAllOutput / ActSwitchAllInput / ActResetAllApps / ActSetDefaultOutput / ActSetDefaultInput；ActPanel|切当前应用端点、批量切活动会话、还原持久化端点、系统默认端点切换、切快捷面板；麦克风选项关时隐藏两项，各版保持相同键位 ID|已保存的自定义绑定按原键名读取；空串表示显式不绑定，不回退默认|源码|
|规则快捷键|edit.hk.rec；同一键鼠输入，Esc 清空；再次点框或切页结束录制|ToggleHotkeyCapture / HotkeyBinding；录制修改当前规则草稿；SaveEditorRule 验证后 AutoRuleStore.Save；规则以 HotkeyPrefix+Id 注册|规则文件 Hotkey；取消整个草稿无文件更新；绑定碰撞、未注册或默认回退有不同反馈|源码|
|规则列表、搜索和筛选|auto.search、auto.stateFilter、auto.triggerFilter、auto.listopt；右键菜单|文本输入与两种过滤组合；重置筛选、打开规则目录；重置排序单独提交|过滤临时；AutomationRuleOrder 仅实际排序提交。窗口/页面重绘不保存|源码|
|新建/模板/单条导入、打开历史|auto.new、auto.templates、auto.import、auto.history|NewRule、OpenTemplatesMenu → Clone 草稿；导入 JSON 校验、撞 ID 重新生成 → AutoRuleStore.Save → 编辑器；历史调用 OpenHistory|新建/模板先草稿，导入成功产生规则文件；模板取消无文件。文件异常提示|源码|
|打开/复制/删除规则|auto.rule.{id}、auto.edit.{id}、auto.copy.{id}、auto.delete.{id}；行右键|按稳定 rule.Id 定位，打开草稿、CopyAsNew 后生成独立规则、删除需确认。右键另有 ExportRuleJson|规则独立 JSON，删除还清 FavoriteRuleIds 和 AutomationRuleOrder；删除成功但元数据失败时按真实规则存储刷新并提示|源码|
|启用/收藏/手动执行|auto.sw.{id}、auto.fav.{id}、auto.run.{id}；右键收藏|ToggleRuleEnabled → AutoRuleStore；ToggleFavorite → FavoriteRuleIds；RunRuleById → Host.RunRule → RuleCommandLine.ReadResult|立即持久化启用和收藏；手动执行没有草稿写盘，更新历史；运行中不可重复触发|源码|
|规则列表拖动|auto.grip.{id}；鼠标拖放/边缘滚动、Esc|UpdateRuleDropTarget 只改落点，CommitOrCancelRuleDrag 在成功释放后以当前规则 ID 构建顺序|AutomationRuleOrder；取消、失捕获无写入，其他控件保存时不会捎带临时排序|源码|
|规则编辑通用|edit.name、edit.enabled、edit.trigger、edit.trigapp.box / pick、edit.sched.mode / hr / min / wd.{day}|输入/选择更新克隆 AutoRule.Name / Enabled / Trigger / TriggerApp / ScheduleMode / ScheduleTime / ScheduleWeekdays；数值与星期校验|这些输入只改草稿；只有 edit.save 持久化，切换类型清除无关参数|源码|
|动作步骤增删排|edit.step.add、step.act.{i}、step.del.{i}、step.cp.{i}、step.dn/up/grip.{i}|新增/复制独立步骤；限制至少一步；上、下箭头和拖放改变当前草稿顺序，Esc 取消拖动|规则文件 Actions 数组在确认后保存|源码|
|每个动作目标|step.{i}.app、apppick、dev、trule、vol、mute、delay、stop|AutoActionDisplay.Groups 选择枚举；按 NeedsApp / NeedsDevice / SetsVolume / AdjustsVolume / SetsMute 显示相应参数。当前/会话应用、真实设备和目标规则以真实 ID 写进草稿|AutoRuleStep.TargetApp / TargetDeviceId / TargetRuleId / Volume / VolumeDelta / Muted / DelayMs / StopOnFailure；ValidateEditor 拒绝缺必要目标与自递归|源码，各组合未逐一运行|
|OSD、程序与脚本步骤|step.{i}.osdt/osdx、lmode、li.{n}/libr.{n}/lirm.{n}/liadd/openwith.{n}、ps.{n}/psrm.{n}/psadd|OSD 标题正文；启动列表和随机/全开；真实文件对话框；打开方式来自 Shell 关联接口，支持默认或自选 EXE；命令列表逐条增删|OsdTitle / OsdText / LaunchMode / LaunchItems.Path/OpenWith/OpenWithName / ProgramPaths；只改草稿。菜单异步结果附带请求号，过期/关闭不再弹出|源码，外部程序未逐一启动|
|确认、取消与测试|edit.save、edit.cancel、edit.test|ValidateEditor → AutoRuleStore.Save → 重新注册观察器；CancelEditor 直接丢弃；TestEditorRule 运行草稿副本并显示真实结果|确认保存独立 JSON 后离开编辑器，失败保留草稿；测试执行不会保存草稿|源码|

### Android 设备页、连接页与嵌入的 AirPlay

仅 Full 编译这些入口。冻结来源为 PhoneDevicePage.xaml、PhoneDevicePage.xaml.cs、PhoneDevicePage.DirectConnection.cs、PhoneDevicePage.AndroidFiles.cs、PhoneDevicePage.Pairing.cs、PhoneDevicePage.AirPlay.cs 与 AirPlayReceiverView.xaml。Native 对应 PhoneDevicePageView、PhoneDevicePageView.Catalog、AirPlayReceiverViewControl、PhonePreview 和原有后台所有者。下面各项均已核对源码，外部 Android 与 AirPlay 设备的配对、同步、传输和输出效果均待真机。

|冻结控件/业务|Native ID/入口|实际业务与状态来源|持久化/错误处理|
|---|---|---|---|
|连接种类、连接/已保存设备切换|phone.add / back / prof:{id}、phone.connchoice.android / airplay|ShowConnectionPage、ShowProfile → Store.SelectProfile、UpdateQrPairing / AirPlay 状态|页面选择临时，已保存设备由 Store 管理；离开取消此页读取和订阅|
|设备重命名、重连、断开、取消配对、移除|phone.rename / rename.box、reconnect、disconnect、unpair、remove|Store.Rename、ReconnectSelectedDeviceAsync、DisconnectAsync、RemovePhoneAsync；破坏性动作先确认|档案含名称和连接状态；操作通过 RunBusy 执行，按结果更新。操作结束不向已隐藏页面作无效显示|
|二维码、配对码、直接连接、保存的设备及 USB|phone.qrrefresh、pairaddr、paircode、pairbtn、connaddr、saved、searchsaved、connbtn、usbrefresh、restoreusb:{deviceKey}|二维码监听或 PairAsync；服务发现/FindWirelessConnectionCandidates/VerifySavedConnectionsAsync 只处理已保存目标；选中后 ReconnectAsync，手填时 ConnectAsync。恢复 USB 时重新读取在线设备且验证身份与歧义|地址/配对码为临时输入，提交配对后清空码；成功接纳由 Connections.Admit 保存。首次进入和手动搜索按需执行，离开取消。无已保存设备时保留手工地址输入|
|音频接收与媒体|phone.receive / receiveboth、audiovol / audiomute、mediaprev / mediaplay / medianext|Sources.AddAsync / RemoveAsync；会话 SetVolume / SetMute；AndroidMediaSessionClient.SendAsync(previous/toggle/next)|会话和媒体状态由原有所有者掌管；后台已建立的音频不因 UI 关闭而停止|
|八个流设置|phone.stream.audioenc / audiodec / audiobr / audiolat / videoenc / videodec / videobr / videofps|HandleStreamComboClick → SaveStreaming → Phone.Settings.RememberStreaming(device,normalized)。编码器按实际设备能力和 HEVC/PCM 条件过滤|AndroidStreamSettings.AudioEncoder、AudioDecoder、AudioBitRate、AudioLatencyMode、VideoEncoder/HevcEncoder、VideoDecoder、VideoBitRate、MaxFps，按设备身份保存|
|应用目录、搜索、固定、智能音量、启动|phone.addapps、catalog.done、appsearch、catalog.row:{package}、pin:{package}、smart:{package}、launch:{package}；右键批量|LoadCatalog → ADB AppsAsync；SelectCatalogRow 支持 Ctrl/Shift/Ctrl+A；Store.Retain/Remove/RetainMany；SetSmartVolume + SyncSmartVolume；OpenDisplayAsync|目录筛选/选择临时，固定应用与智能音量在对应 profile 持久化；列表按包名而非行号命中|
|文件与传输|phone.browsefiles、folderup、rfile:{folder}/{entry}、rfileact、fileclose、sendfile、remotefolder、localfolder、cancelfile；拖放|LoadRemoteFolder、ActOnAndroidPicker、EnqueueOrStartFiles → PhonePreview.EnqueueTransfer / StartTransfer、Transfers.Cancel；PC 接收目录由 ReceiveDirectorySettings 写入|临时文件选择无配置；手机接收目录随 profile，PC 目录独立文件；在途和排队任务由宿主持有，完成事件按 profileId 核对连接后推进下一批；关闭页面不取消|
|剪贴板|phone.clipboard 开关|Phone.Clipboard.SetEnabled 持有设备级监听与订阅|从对应设备的同步状态读回，启闭由 clipboard 服务负责|
|AirPlay 开关与首选项|phonepage.airplay.toggle / name / decoder / pin；伴随窗共享 companion.airplay 前缀|SetReceivingAsync、NormalizeName、解码选项和连接 PIN；Starting/Stopping 不重复切换|AirPlayReceiverName、AirPlayVideoDecoder、AirPlayRequireCode 经 TryCommit。失败恢复原配置并显示提示。启停结果由实际接收器状态反馈|
|AirPlay 镜像与中断|同前缀 showmirror / topmost / fullscreen / disconnect|ShowMirror、SetMirrorTopmost、SetMirrorFullscreen、Disconnect|来自接收器、镜像窗的实际状态；不保存为通用配置|
|滚动、状态及装饰|phone.scroll / filescroll、AirPlay status（Winsock 提示）、二维码/封面|滚动由两个对应视口所有；状态和图片只用于展示|无写入|


### 源码审查的剩余限制

- Android 关页后推进下一批的所有权已移至宿主。多文件、拔线中断及显式取消仍待真实设备确认。
- 配置重置失败、批量静音/路由部分成功的路径已追踪到真实返回值；这两类故障未做 UI 故障注入。
