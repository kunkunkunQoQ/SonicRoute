# R-07 快捷面板、独立窗口与弹层控件执行审查

以冻结 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a` 的窗口、XAML、分部类及实际事件作为基准。下表将具有相同参数、命令和保存方式的动态控件列为 ID 家族，覆盖同一窗口的两种行布局。动态应用参数使用 `AudioAppInfo.Identity` 或 Android 包名，输出参数使用实际端点 ID，文件参数包含真实目录和条目名。Full 专属操作在 Lite 不创建控件。

**验证口径**　除总报告中带本轮运行收据的条目外，表内结果均为源码审查。Windows 音量会话、设备音量以及外部服务持有的状态不重复写入 AppConfig。独立窗口的输入与业务结果不能由矩阵推断为实机通过。

## 快捷面板

冻结位置为 `SonicRoute/QuickPanelModernWindow.xaml`、`.xaml.cs`、`.Rows.cs`、`.RowDevices.cs`、`.PlaybackSelection.cs`、`.AndroidAudio.cs`、`.OutputVolumes.cs` 及 `QuickPanelPosition.cs`。Native 位置为 `Native/Product/Views/QuickPanelWindow.cs`。两种布局共用 `OnPointerDown/Move/Up`、`ActivateFocused`、`OnMouseWheel` 和 `OnKeyDown`。按下后移出、丢失捕获、改变行身份和取消调整均检查原手势；右键档位只接受同一控件的右键按下和抬起。

| 控件与 Native ID | 冻结行为和输入 | 命令、同步与失败 | 保存语义 |
|---|---|---|---|
| 经典/简洁、音量指示、峰值显示 | 两种行样式由设置配置；音量/峰值数字仅显示 | `RefreshData` 读取真实会话、选中源、路由及音量，峰值只在显示时采样 | 设定源为主题/设置页。行重绘不保存 |
| 标题固定 `qp.title`、主窗口 `qp.settings` | 左键或键盘激活。标题固定为用户保留调整 | 变更 `_pinned` 影响失活关闭；主窗经 `OpenProductMain` | 仅本次窗口，不保存固定状态 |
| 上下沿 `qp.resize.top/bottom`、调整模式表面 | 拖动改变面板高度，调整模式拖动保存位置，Esc/右键取消 | Release 后由 `CurrentCustomPosition` 或 `SaveAdjustPosition` 生成最终值；写入失败恢复/提示 | `QuickPanelHeight`、`QuickPanelPosMode`、`QuickPanelCustomX/Y`。拖动帧仅预览 |
| 音频输出 `qp.masterdev` | 下拉选择可见输出；两个模式分别改变系统默认或面板控制目标 | `OpenMasterDeviceCombo` 按稳定端点调用 `Audio.SetDefault(Render,…)` 或设置 `_panelOutputId` 并从真实设备刷新 | 端点由 Windows 保存，单面板控制目标为运行时 |
| 总音量 `qp.masterslider/mastermute`、右键百分比 | 拖动、滚轮、键盘、25/50/75/100 档位或点击静音 | `QueueMasterVolume/ToggleMasterMute` 修改实际目标端点；和输出弹层交换目标、乐观值及读回 | 系统端点保存 |
| 输出音量弹层、手机伴随窗 `qp.outputs/phone` | 打开、再次点击收起；手机按钮仅 Full | `ToggleOutputVolumesPopup/TogglePhoneCompanion → SyncCompanionsVisibility`，按显示状态创建，隐藏后释放专属资源 | `QuickPanelShowAllOutputVolumes / QuickPanelShowPhoneAudio` 点击提交 |
| 播放/麦克风全局静音 `qp.globalmute/mic` | 控制全部对应端点，按钮体现真实复合状态 | `ToggleGlobalOutputMute/ToggleGlobalMicMute → Audio.GlobalPlaybackMute/GlobalMicMute`；真实设备组读回、刷新 | Windows 端点保存 |
| 应用图标/静音 `qp.appmute:{app}` | 点击控制静音；拖动超过阈值分配给跨设备操作，不再发送静音 | `ToggleAppMute → Audio.Volume(app,…)`，按原身份刷新 | 系统会话或外部音源 |
| 应用音量 `qp.appslider:{app}` | 两种行布局、拖动、滚轮、方向键/翻页/首尾、右键档位 | `QueueAppVolume`，PC 走共享写入服务，外部源走真实会话；行失效取消拖动，读回不能更新其他应用 | 系统会话或外部音源 |
| 展开 `qp.expand:{app}`、滚动 `qp.scroll` | 展开/收起设备项；滚轮或轨道拖动 | `ToggleRowExpand` 绑定应用身份；滚动及命中使用同一偏移 | 窗口状态 |
| 应用输出 `qp.appdev:{app}:{endpoint}` | 点击单选，Ctrl 增删，Shift 范围，Ctrl+Shift 按业务约束；支持手机端源 | `SelectAppOutput → CurrentApp.SelectOutputAsync/SetOutputsAsync` 或 `Audio.SetExternalOutputsAsync`。输出意向/读回按身份与请求代际更新 | Windows 路由、播放恢复和镜像状态服务或手机源持有 |
| 录音输入 `qp.appin:{app}:{endpoint}` | 实验开关和面板显示均开启时，按具体设备选择 | `SelectAppInput → Audio.SetRoute(pid,Capture,…)` 并读回 | Windows 应用输入策略 |
| 断开外部应用 `qp.appexit:{app}` | Full，收音/接收中的外部应用 | `DisconnectExternalRow` 按 source ID 调用 Android `RemoveAsync` 或 AirPlay `Disconnect`，重新读取会话 | 由外部会话及恢复服务管理 |
| 面板拖动区、预览图标 | PC 到手机输出；Android 到电脑、手机原声、窗口外虚拟屏；AirPlay 音频/镜像 | `CrossDeviceDragHost` 按屏幕物理坐标检查已登记的实际窗口和投放区；退出手势时结束预览、释放捕获，重查来源和设备 | 执行后按路由、外部接收或虚拟屏所属服务处理。拖动图标本身无持久化 |

## 输出弹层与共享选择窗口

| 窗口/控件 ID | WPF 及 Native 位置 | 输入、命令、回读及失败 | 保存 |
|---|---|---|---|
| 输出设备名 `ov.def:{id}` | `QuickPanelModernWindow.OutputVolumes.cs`；`Views/OutputVolumesPopupWindow.cs::ActivateFocused` | 左键/键盘选择实际端点 `Audio.SetDefault`，刷新默认状态及父面板 | Windows |
| 单设备静音 `ov.mute:{id}` | 同上 | 点击后调用 `Audio.Volume(Render,id,…)`，更新复合状态及同 ID 的主面板音量显示 | Windows |
| 单设备音量 `ov.vol:{id}`、`ov.scroll` | 同上，`QueueDeviceVolume`、`OnPointerUp/OnMouseWheel` | 拖动、滚轮、键盘或右键档位作用于完整端点 ID；列表可滚动。关闭窗口与设备消失时不把旧读回套到新目标 | Windows。无 AppConfig 滑块写盘 |
| 通用选择项 `ComboChoice.Key` | WPF ComboBox；`Ui/ComboPopupWindow.cs::CommitIndex` | 鼠标匹配原行、方向键/首尾/翻页、文本搜索、Enter 选中，Esc/失活关闭；交还所有者焦点。选中回调携带原键 | 各调用者负责。选择窗口不独立写配置 |
| 可嵌套菜单 `UiMenuItem`、滚动 `menu.scroll` | `App.xaml::ConvenienceMenu`；`Ui/PopupMenuWindow.cs` | 共享鼠标/键盘入口核对可用状态和对象身份，再调用 `Click`；按需创建子 HWND，收尾取消未执行的悬停任务 | 打开/导航不保存；叶项见对应业务表 |
| 工具提示、拖动跟随图 | WPF ToolTip、拖动预览；`Ui/WindowTooltip.cs`、`Views/DragIconWindow.cs` | 由宿主命中项或拖动生命周期驱动，不存在独立业务点击控件 | 无 |

## 浏览器、系统对话框、取色及 OSD

| 窗口/控件 ID | WPF 及 Native 位置 | 输入、命令、回读及失败 | 保存 |
|---|---|---|---|
| 取色器 96 色 `cp.preset.{i}`、`cp.spectrum/lightness` | `ThemeColorPickerWindow.cs`；`Views/ColorPickerWindow.cs::Activate/Pick/OnKeyDown` | 鼠标拖动、键盘选择/方向调整；同步 HSL、RGB、预览及十六进制文本 | 调色草稿 |
| `cp.r/g/b/h/s/l/hex` | 同上，`CommitField`、`Ui/InlineEditHost.cs` | Win32 EDIT 中文输入、选择复制，确认/正常失焦提交草稿，校验范围及 6 位十六进制；非法值保留既有颜色 | 不直接保存 |
| `cp.ok/cancel/close` | 同上，`Accept/OnClosed` | 确认先提交当前字段并只调用一次接受回调，取消/关闭/Esc 退出草稿 | 接受回调回主题页写 `Accent`；取消不写 |
| 普通 OSD、定位模式窗口 | `OsdService.cs/OsdPlacement.cs`；`Views/OsdWindow.cs::CommitDragAdjust/CancelAdjust` | 正常穿透。显式进入调整后左键拖动、释放提交，右键/Esc 取消 | `OsdPosition/OsdCustomPhysicalX/Y/OsdCustomX/Y` 释放提交，失败还原并提示 |
| 历史、许可与文本选择 | `MainWindow.ExecutionHistory.cs`、设置许可窗口；`Views/ReadOnlyWindow.cs` | HWND EDIT 101 只读选择/复制；BUTTON 102 刷新实际历史；LISTBOX 104 选行加载摘要与步骤，103 显示状态。Tab、Ctrl+A、Esc、标题栏关闭；取消过期读取，许可关闭恢复所有者 | 展示及选择无写入 |
| 系统文件/文件夹选择及确认 | WPF 对应 Open/Save/Folder/MessageBox；`Views/NativeDialogs.cs::Open/OpenMultiple/Save/BrowseFolder/Confirm/ConfirmImport` | Shell 或系统对话框返回用户实际选项；取消返回空，具体调用者核对身份/存在性并执行业务 | 路径/导入等只由调用者按其语义提交 |
| 热键/文本输入模态对话框 | WPF 自动化步骤及文本/热键录入；`NativeDialogs.Run/TryRecord/Finish` | 系统输入、修饰键和鼠标组合进入录入，确定/取消返回给原步骤编辑器。只读字段可选择复制 | 对话框本身不保存，自动化草稿由编辑页保存 |
| 手机文件提交/选择 `phone.sendfile/browsefiles/remotefolder/localfolder` 和 WM_DROPFILES | `PhoneDevicePage.AndroidFiles.cs`；`Views/PhoneDevicePageView.cs::OpenAndroidPicker/EnqueueOrStartFiles` | 多选文件后复核当前在线设备及页面；浏览远端目录；本地接收目录由系统对话框选择；拖入文件走同一传输队列 | 选定的手机目录写 `Store.SetReceiveFolder(profileId,…)`；本机接收目录写 `ReceiveDirectorySettings.SetReceiveDirectory` |
| `phone.folderup/rfile:{path}/filescroll/rfileact/fileclose/cancelfile` | 同上，`LoadRemoteFolder/ActOnAndroidPicker/CloseAndroidPicker` | 上级、进目录、多选文件、确认拉取或选定目录，按当前目录和文件名重查。关闭选择器只取消目录读取，明确取消才撤销 profile 队列和 `Transfers.Cancel`。进度/失败来自 Transfers 事件 | 浏览位置/勾选临时；选择接收目录才保存。文件传输真实设备待验 |

## Android、AirPlay 及视频窗口

| 控件/ID | 冻结及原生位置 | 输入、真实业务及反馈 | 保存 |
|---|---|---|---|
| 手机设备切换 `companion.device/signal/airplaygrip`，管理、刷新 `companion.manage/refresh` | `PhoneAudioWindow.xaml(.cs)/.AirPlay.cs`；`Views/PhoneAudioWindowView.cs::ActivateFocused/OpenDeviceSwitcher` | 打开 profile 列表，选择手机调用 `Store.SelectProfile`，AirPlay 切换伴随内容；管理打开对应手机页，刷新 `Phone.RefreshAsync`。切换/关闭后丢弃上一设备 UI 回调 | 手机选择由设备 Store 管理；切换到 AirPlay 是窗口状态 |
| 过滤、行、滚动 `companion.search/app:{package}/scroll` | 同上，`GetFilteredApps/LaunchAppVirtualDisplay/ToggleAppAudioCapture` | 搜索文本过滤保留应用，单击选中，Enter 发起接收，右键增删接收，双击打开虚拟屏；拖动按实际设备和包身份复核 | 过滤/选中临时；接收及显示状态由源/虚拟屏服务管理 |
| `companion.receive` | 同上，`ToggleWholePhoneAudio` | 读取当前设备整机源，调用 `Sources.AddAsync/RemoveAsync`，复用已连接会话 | 音源/恢复服务 |
| 媒体控制 `companion.prev/play/next/seek` | `PhoneAudioWindow.xaml.cs` 媒体事件；`PhoneAudioWindowView::ActivateFocused/CommitMediaSeekAsync` | 按 CanPrevious、CanToggle、CanNext、CanSeek 决定可操作；真实媒体客户端接收 previous/toggle/next/seek。拖动或键盘预览在释放后校验包名、曲目再发定位；失焦或 Esc 放弃。命令失败回到消息线程，设备、窗口和请求仍匹配才更新状态栏 | 外部媒体播放器管理；产品不保存播放进度 |
| 接收器 `companion.airplay.toggle/name/decoder/pin` | `PhoneAudioWindow.AirPlay.cs`、接收设置；`Views/AirPlayReceiverViewControl.cs::HandleClick/CommitSettings` | 启停 `AirPlayPreview.SetReceivingAsync`，观察实际 Stage/错误。修改名称经规范化，解码按真实能力提供可用选项，PIN 控制连接要求 | 开启接收属于运行时；`AirPlayReceiverName/AirPlayVideoDecoder/AirPlayRequireCode` 提交并由服务读取 |
| `companion.airplay.showmirror/topmost/fullscreen/disconnect` | 同上 | `ShowMirror/SetMirrorTopmost/SetMirrorFullscreen/Disconnect` 作用于当前接收器/镜像窗；状态源为接收服务 | 会话内窗口模式，不写通用配置 |
| Android 和 AirPlay 跨窗口拖放 | `PhoneAudioWindow.xaml.cs::DragAppRow/DropPhoneApp`、`QuickPanelModernWindow.AndroidAudio.cs`；`CrossDeviceDragHost` 及两窗口手势 | 实际控制按屏幕投放区和来源分流到路由、回手机、关闭 AirPlay 或展示虚拟屏。捕获丢失、取消或目标已变终止预览 | 实际业务按路由或接收服务持有 |
| 虚拟屏恢复 `display.restore` | `VirtualDisplayPresentation/VirtualDisplayOverlayWindow.cs`；`Views/VirtualDisplayStatusWindow.cs::ActivateFocused` | 以实际状态显示，恢复时禁用重复点击，`IVirtualDisplayClient.Restore`，状态事件决定隐藏/恢复控制 | 本次虚拟屏 |
| 视频窗口原生客户区及窗口操作 | 冻结和候选均使用 `SonicRoute.AndroidAudio/NativeVideoWindow.cs::WindowProc`；`InProcessVirtualDisplayClient` | WM 鼠标移动、左右中键、侧键和滚轮转触摸、导航或滚动；键盘/IME/游戏手柄经已有输入映射；焦点丢失释放，系统标题栏缩放、全屏、置顶与关闭走原视频宿主 | 输入由外部设备消费，窗口与源生命周期由各服务持有 |

本轮另外修正了伴随窗此前直接丢弃的媒体控制、外部路由、虚拟屏打开、整机及应用收音任务。所有失败使用既有翻译和诊断入口；代际、隐藏、关闭及切设备后到达的旧结果不再改写伴随窗。修正了媒体跳转失败时从线程池操作画面计时器的路径。源码已核对，真实 Android 媒体会话、文件往返、AirPlay 镜像、跨设备拖动和虚拟屏输入仍需相应设备实测。
