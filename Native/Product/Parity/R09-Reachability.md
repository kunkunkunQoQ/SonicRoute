# R09 产品窗口与弹层的双向入口核对

基准为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。下表从冻结提交的实例化、显示调用及 XAML Popup 追踪到用户动作，再从 Native 生产构造器、宿主分派和条件反查。WPF 路径相对 `SonicRoute/`，Native 窗口路径相对 `Native/Product/Views/`。两端视频实现位于 `SonicRoute.AndroidAudio/`。结论属于源码审查，R09 运行截图、滚轮和业务验证以当轮交付记录为准。

| 用户实际入口及版本 | 冻结 WPF 的可达路径和条件 | Native 的实际创建与反向核对 |
|---|---|---|
| 托盘冷启动与主窗，Full/Lite | `App.xaml.cs:201–225,649` 注册托盘。左键单击延迟 280 ms 切换快捷面板，双击取消未决单击并开主窗。托盘菜单、面板底部、已有实例激活均可开主窗。 | `Program.cs:70` 建后台消息窗及宿主。`Shell/Tray.cs::OnTrayMessage → HostViews.ShowFeedback → EnsureMainShell → MainWindowShell.ShowMain` 才呈现主窗。打开主窗收起快捷面板和托盘菜单。 |
| 主窗导航，Full/Lite | `MainWindow.xaml` 的导航及 `MainWindow.DeferredPages.cs` 加载实际内容。应用、快捷键、主题、设置、自动化，以及解锁并启用后的实验功能。Full 添加设备和管理设备进入应用页子视图。 | `MainWindowShell.NavigateTo/DrawNavItem → HostViews.CreateMainPage` 六个分支，实验页同时在导航和切页检查条件。Full 的 `OpenPhoneDevice → AppsPageView.OpenPhoneDevice` 复用应用页。页面细项见本轮设备与设置报告。 |
| 两种快捷面板行及位置调整，Full/Lite | `App.xaml.cs:496,620 → QuickPanelModernWindow`；`QuickPanelModernWindow.xaml.cs:787` 按 `QuickPanelUseLegacyAppRows` 选择行模板。同一窗口承载两种行、设备下拉、音量、静音、底栏等。主题页可进入位置调整。 | `HostViews.EnsureQuickPanel → QuickPanelWindow.ShowQuickPanel/BeginAdjust`，同一实例按配置手工布局两种行。行/设备选择及预设弹层分别从面板动作创建。保留用户指定的标题点击固定面板。 |
| 托盘右键和级联菜单，Full/Lite | `App.xaml.cs:201 → App.TrayMenu.ShowTrayMenu/CreateTrayMenu`，捕捉前台目标，生成顶级 `ContextMenu` 并动态填充子项。 | `Shell/Tray.cs → HostViews.ShowTrayMenu → TrayMenuController.Show/ShowReady → Ui/PopupMenuWindow.ShowAtCursor`；子菜单由该窗口创建和管理。按用户要求，系统默认输出设备名在根首次显示并截短、级联保留区分通道的短名。逐项命令及动态条件见 [R07 托盘矩阵](R07-Tray.md)。 |
| 设备选择、应用右键、预设、下拉，Full/Lite | `MainWindow.xaml`、`QuickPanelModernWindow.Rows.cs/.RowDevices.cs/.xaml.cs` 中实际绑定或程序建立的下拉与菜单，`ConvenienceMenus.cs`、`VolumePresets.cs` 提供共用菜单项。 | `AppsPageView`、`QuickPanelWindow` 及各页从控件锚点创建 `Ui/ComboPopupWindow` 或 `Ui/PopupMenuWindow`。后者也用于右键/级联。选项由调用者绑定至相应业务。 |
| 全部输出设备音量，Full/Lite | `QuickPanelModernWindow.xaml` 中的 `OutputVolumesPopup`。面板可见且打开开关时，`QuickPanelModernWindow.OutputVolumes.cs:72,110,117,155` 创建模板和端点行，订阅设备变化。 | `QuickPanelWindow.ToggleOutputVolumesPopup/SyncCompanionsVisibility → OutputVolumesPopupWindow.ShowForOwner`，使用真实端点标识。面板调整时收起，按工作区及手机伴随位置布局。WPF 与 Native 都由产品承载此列表，未发现调用 `sndvol`、`mmsys.cpl`、`ms-settings:sound` 的对应入口。 |
| 输出切换、音量、麦克风 OSD 与位置调整，Full/Lite | `App.xaml.cs::ShowVolumeOsd → TrayWheelService`、`OsdService.ShowVolume` 及其他状态入口，`OsdService.cs:469` 建立提示窗。`OsdPlacement.cs` 参与位置计算。 | `HostViews.ShowFeedback → ShowOsd → OsdWindow.ShowOsd`；输出切换中、完成、麦克风常驻与一次性音量分别驱动。调整入口单独按需取得此窗。与快捷面板输出音量弹层各有生命周期。 |
| 历史、许可和主题选色，Full/Lite | `MainWindow.ExecutionHistory.cs:135` 创建非模态历史；`MainWindow.Licensing.cs:35,49` 显示模态许可；`MainWindow.ThemeLayout.cs:79` 从选色按钮打开 `ThemeColorPickerWindow`，接受时应用。 | `MainWindowShell.OpenHistory/ShowProjectLicense → ReadOnlyWindow`。历史要求实验模式和执行历史启用；许可禁用 owner，关闭后恢复。主题页 `ColorPickerWindow.Open` 单独创建有 owner 的模态窗口，应用回调绑定接受结果。历史和许可证使用 Win32 可选择文本及系统窗口装饰。 |
| 打开/保存、多选文件、文件夹、确认，Full/Lite 按实际调用页 | `MainWindow.AppIcons.cs:72`、`MainWindow.xaml.cs:1817,1831,1906,1944,4253,4270`；`MainWindow.ImportPreview.cs::ConfirmConfigImportAsync` 使用信息提示框确认。Full 手机页也使用系统文件选择器。 | `NativeDialogs.Open/Save/OpenMultiple/BrowseFolder` 使用系统对话框。`ExperimentalPageView → NativeDialogs.ConfirmImport` 等使用 owner 的系统消息框。选择器对话框的外观取决于 Windows 版本。 |
| 手机音频伴随、Android 系统媒体，Full | `App.AndroidAudio.cs:389–418` 在面板可见且 `QuickPanelShowPhoneAudio` 开启时创建 `PhoneAudioWindow`；Android 身份和媒体能力决定媒体卡状态。`PhoneAudioWindow.xaml.cs:984` 创建媒体会话客户端。 | `QuickPanelWindow.TogglePhoneCompanion/SyncCompanionsVisibility → PhoneAudioWindowView.ShowForOwner`。Android 媒体位于伴随卡片，`RestartMediaWatch/StopMediaWatch` 随当前可见设备管理订阅，前一首、播放/暂停、下一首按会话能力路由到 `AndroidMediaSessionClient.SendAsync`。Apple 子视图有独立接收器状态。两端均未找到单独的 Android 系统媒体控制窗或 Windows 系统媒体面板入口。 |
| Android 虚拟屏视频及状态层，Full | 伴随列表/跨窗拖动 → `App.AndroidAudio.cs:344` 启动指定设备和包 → `InProcessVirtualDisplayClient.cs:152` 创建 `NativeVideoWindow`。`VirtualDisplayPresentation.cs:46–54` 只在回到手机、恢复中、目标缺失时创建状态提示；`VirtualDisplayOverlayWindow` 随视频客户区移动。 | `CrossDeviceDragHost.FinishAndroidDropAsync → PhonePreview.OpenDisplayAsync → Displays` 使用同一视频窗口类。匹配设备及包可返回手机；`PhonePreview.UpdateStatusOnUi → VirtualDisplayStatusWindow` 在相同三种状态且有视频 HWND 时呈现。冻结通用遮罩还有 PID 回退；当前生产工厂始终创建进程内客户端，`ProcessId` 在没有 HWND 时为零。 |
| AirPlay 镜像，Full | 已启用接收器且收到有效镜像视频会话时，`AirPlay/AirPlayMirrorStream.cs:160` 创建镜像模式的 `NativeVideoWindow`。纯音频会话不创建视频窗。 | 原生接收器 `AirPlayPreview` 接入同一媒体后端，镜像窗依旧由视频会话创建。主窗与伴随的 Apple 接收控制是嵌入视图。已检查实际构造路径，镜像、视频和真机媒体命令仍待有设备验证。 |
| Android 文件的本地选择与远端浏览，Full | `PhoneDevicePage.AndroidFiles.cs` 开启 Windows 本地选择器，`PhoneDevicePage.xaml` 的文件卡内展开远端列表；传输由后台队列持有。 | Native 从详情文件卡进入远端目录，调用系统对话框选择本地文件夹/文件。详细入口、条件及验证见 [R09 设备页](R09-Devices.md)。 |

## 原生反向检查与边界

- `NativeApplicationWindow` 承载托盘通知、已注册热键和应用激活。`HotkeyService.cs:44` 的冻结隐藏宿主在这里有对应职责。这些消息窗不计入用户可浏览窗口数。
- `DragIconWindow` 由快捷面板与手机伴随的实际拖动创建。`Ui/WindowTooltip` 由命中提示触发，`Ui/InlineEditHost` 创建子 EDIT，`Ui/PopupMenuWindow` 还为子菜单创建自有 HWND。它们分别对应冻结拖动反馈、ToolTip、文本输入和菜单。没有独立导航或业务页面。
- `NativeDialogs.Input/RecordHotkey` 仍有实现，但已检查生产调用点，当前录入由页内编辑或录制控件负责。不能把两个未调用的通用对话框或 `Native/FullProbe` 的开发入口列为实际产品操作路径。
- `PhoneDevicePageView.UpdateQrPairing` 只在 `_active && IsConnectionPage && _airPlayConnectionChoice` 时绑定 Apple 视图订阅。切回 Android、进入设备详情或退出页面时解除。Android QR 只在活跃、未忙的 Android 添加页启动，离开此状态会取消本次令牌、清掉二维码像素和重试状态。等待任务的 `finally` 释放 CTS，过期结果按实例身份丢弃。上述为 UI 生命周期，不要求停止正在接收的音频或文件传输。
- `Directory.Build.targets:2` 仅对 Full 定义 `SONICROUTE_PHONE`；`Native/Product/Product.csproj:21` 的手机项目与 QR 包同样受 Full 条件控制。Lite 不暴露手机、镜像、虚拟屏窗口。Windows Link Beta 依任务范围排除。
- 两种快捷面板的实际行模板、只读窗所用系统控件、各屏不同缩放、OSD 强调色及镜像画面仍需分别检查。双向路由核对只说明入口和条件的来源；设备切换的听感、虚拟屏恢复、实际传输及用户实机验收须以单独的运行证据判断。
