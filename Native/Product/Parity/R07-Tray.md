# R-07 托盘右键菜单

唯一基准为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。冻结入口为 `SonicRoute/App.TrayMenu.cs::CreateTrayMenu`、`App.TrayAudio.cs::AddTrayAudioItems`、`App.AutomationExtras.cs::BuildFavoriteRulesMenu`、`App.xaml.cs` 的 NotifyIcon 事件、`App.xaml` 的 ConvenienceMenu 三个模板。Full 与 Lite 的菜单树相同，Full 的当前应用与可用路由可以包含外部音源。麦克风开关独立于实验录音设置。实际设备名称、是否有更新和收藏内容随环境变化。

## 执行矩阵

以下为逐项源码审查。菜单控件以同一菜单生命周期的 `UiMenuItem` 对象绑定事件；表中的定位是实际语言键或业务键。鼠标经 `Shell/Tray.cs::OnTrayMessage`、`HostViews.ShowTrayMenu` 打开顶级 HWND。行由 `PopupMenuWindow.HitIndex` 定位，抬起时核对按下对象，键盘经 `OnKeyDown` 到同一 `ActivateItem`。Disabled 和占位项不能执行。除标注“展开”的行以外，叶节点关闭整条菜单后执行一次。托盘点击不通过命令行或 IPC。

| 层级及原生定位 | WPF 1.36 行为与隐藏条件 | Native 业务入口及结果、失败处理 | 持久化和重新打开时的数据源 |
|---|---|---|---|
| 托盘左键、双击、系统键盘选择 | `App.xaml.cs` 单击延时 280 ms 切换面板，双击取消单击并打开主窗 | `OnTrayMessage → QuickPanel/MainWindow → HostViews`。右击清理待处理的单击 | 运行时 |
| 托盘滚轮、Alt、Ctrl 等既有组合 | `TrayWheelService` 依设置和命中区分配原有动作 | 共享现有 `Shell/Mouse.cs::OnMouse/DrainWheel`、`ProductAutomation`，消费/观察语义沿用现有注册 | `TrayWheelPaused/TrayWheelEverywhere`，手势作用结果由对应端点或应用负责 |
| 顶层 `Tray.OpenPanel` | 已显示时激活，隐藏时打开 | `HostViews.ShowTrayMenu → RequestFocus/ShowPanel`，延用现有面板 | 运行时 |
| `Tray.OpenMain` | 打开或激活主窗 | `OpenProductMain → EnsureMainShell().ShowMain` | 运行时 |
| `Upd.Notice` | 仅 `ShouldNotify` 且存在版本信息 | `OpenProductMain → MainWindowShell.OpenUpdateSettings → ScrollToUpdates` | 此动作只导航，更新状态取 Updates 服务 |
| 分隔线、当前应用 `Ov.CurrentApp/Qp.CurrentApp` | 菜单打开时按前台上下文和默认应用策略解析；无输出会话禁用 | `CurrentApp.Resolve → Audio.Volume`，记录应用身份及可读的进程启动时间；占位/读取失败独立，不影响其余菜单 | 仅查询，全名可悬停 |
| 当前应用 / `Act.Mute` | 对已显示会话切换静音 | 重新核对身份、启动时间、当前会话后 `Audio.Volume(app,mute:…)`，核对读回值并显示 OSD、刷新可见音频视图 | Windows 会话或外部源保存 |
| 当前应用 / `Tray.VolumePresets` / 25、50、75、100% | 单选与当前音量相等的档位 | `Audio.Volume(app,level:…)`，反馈实际百分比 | Windows 会话或外部源保存 |
| 当前应用 / `Qp.Output` / 虚拟默认、具体端点 ID | 每次展开读取可用设备与应用当前路由；使用别名及可见配置；保留当前已选中的隐藏物理端点 | `ReadRoute`，点击 PC 应用经 `CurrentApp.SelectOutputAsync` 进入原有有序写入与读回；外部源经 `SetExternalOutputsAsync` 并核对 `GetSelectedOutputs`。失联或写入异常反馈失败 | Windows 应用音频策略、播放恢复服务或外部音源保存。托盘不写另一份应用路由 |
| 第一级 `Dev.SystemDefaultOut` / 实体输出 ID | 打开根菜单即读取 Windows 默认端点及真实名称；每次展开再读设备和默认值，向上更新 `Tray.SystemOutput` 标题；空列表显示 `Ov.NoDevice` | `ProductAudio.Devices/DefaultId → SetDefault(Render,id)`，检查 Console 与 Multimedia 的读回，失败反馈，成功刷新各窗口 | Windows 保存系统端点；`DeviceNames/HiddenOutputDevices` 作为显示输入 |
| `Tray.MuteAllMicrophones` | 始终有此行，端点不可用时禁用，全部静音时勾选，混合状态有悬浮说明 | 逐个读取捕获端点，`Audio.GlobalMicMute(bool)` 处理写入及整组读回，显示麦克风 OSD | Windows 捕获端点保存 |
| `Auto.Favorites` / 收藏规则 ID | 按 `FavoriteRuleIds` 顺序去重，运行中禁用，空列表为不可点击的 `Auto.FavoritesEmpty` | `AutoRuleStore.Find → AutoRuleService.ExecuteWithResultAsync(rule,"favorite") → DescribeResult`，已删除、运行失败各有反馈 | 收藏选择由自动化页保存；执行历史沿用规则服务。托盘只读选择 |
| 分隔线、`Tray.PauseWheel` | 勾选与暂停状态相同；点击先应用监听，再保存，任一失败提示并恢复 | `SetTrayWheelPaused → TryConfigureTrayWheel → HotkeyService.ConfigureTrayWheel → NativeMouseBindings.Reload → ConfigureMouse` 确认实际注册结果。保存成功刷新前台观察和设置页 | `AppConfig.TrayWheelPaused → ConfigService.TryCommit`；写入失败恢复配置及原钩子设置。下次打开使用同一配置 |
| `Tray.Exit` | 关闭菜单并正常退出 | `NativeApplicationWindow.RequestExit` 进入既有宿主关闭流程 | 退出时的配置和其他存储见总报告 |
| 级联焦点、滚动与关闭 | 工作区内放置，菜单行右侧展开，空间不足向左；Esc 逐层退出 | 一个根窗口持有激活与按键，所有级联 HWND 以非激活方式打开；键盘转交最深层，鼠标短暂跨越相邻行时保留当前级联，全部窗口关闭时撤销尚未执行的延迟切换和只读回调 | 临时窗口状态 |

## 具体差异及改动

- 原候选从实际音频会话构造额外“应用”树（截取前 20 项），还在托盘加上输入设备、系统音量档位、重置所有应用、手机管理、整托盘滚轮开关和多余分隔线。现按冻结菜单顺序构造。应用的麦克风路由、手机管理、设置等仍由原有页面和快捷键提供。
- 旧 `BuildFullItems` 将全部枚举和规则读取完成后整体替换列表，可能中断鼠标或关闭使用中的级联。当前菜单先创建全部正确位置，分别回填当前应用、麦克风与收藏；默认播放标题在根菜单打开时单独读取一个端点，完整端点列表只在其子菜单打开时读取。后台结果只投递到原 HWND 与生命周期，选择动态项按对象及最终稳定 ID。
- 原层逐级激活 HWND，在 `WM_ACTIVATE` 同步转换期间使用 `GetForegroundWindow` 判断归属，存在提前关闭整树风险。改为单个激活根窗口，级联只接受指针；上下、左右、Tab、Enter/Space 与 Esc 使用同一树。鼠标捕获、取消与移出再移回均沿用共享输入生命周期。
- v4 通知先核对 lParam 高字的图标 ID，再解码低字事件。鼠标通知从 wParam 的有符号 16 位坐标获得实际屏幕锚点；WM_CONTEXTMENU 本身的 wParam 无坐标保证，使用先前的鼠标锚点或 Shell_NotifyIconGetRect 定位键盘打开。兼容仅上报右键抬起或额外上报上下文菜单的宿主，同一手势避免重复。旧通知版本使用其图标 ID 与当前鼠标位置。[Microsoft 的 NOTIFYICONDATAW 坐标定义](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)
- `PopupMenuWindow.MeasureAndLayout/OnRender` 按冻结样式采用 1 DIP 边框、4 DIP 面板内边距、8×6 DIP 行内边距、12.5 DIP 字体、6/4 DIP 圆角，文字后跟勾号或箭头，恢复普通文本前景。WPF 模板没有额外画阴影或危险色退出图标。菜单使用当时的浅色/深色/自定义调色板和九语言资源。
- 按本轮用户后补标注，根菜单直接显示“系统默认播放：设备名”，名称限 16 个 Unicode 文本元素，并保留完整名称的悬停提示。超过 80 ms 仍未返回的音频服务先显示本地化的加载状态，结果到达后主动更新；打开子菜单后优先采用更新的读回。系统输出子项最多显示 24 个 Unicode 文本元素，文字列最多 196 DIP，鼠标悬停显示全名；长驱动名优先保留产品名和通道（Gaming、Chat 等），别名按用户给定文本裁切。应用输出子项与动态父级的文字列沿用冻结的 300 DIP 上限。显示标签不参与路由匹配。
- Full 的外部音源候选来自现有已发现设备和接收会话目录；Android 来源可选择本机原声及所属手机可用输出，其他手机在线时提供其当前输出。系统默认输出菜单只包含 Windows 实体端点。无实际外部链路时，不能由列表源码或暗色资源审查证明声音已到达设备。

语言资源已执行静态核对。托盘控制器使用的 33 个字面语言键在九份 JSON 中均存在，九个 `Tray.SystemOutput` 模板均保留设备名占位符，资源目录与冻结提交无差异。具体系统输入、读回及截屏文件以本轮总报告的构建指纹和收据为准。多显示器边缘、硬件断连、不同外壳版本及所有语言逐项手动操作仍需实机确认。
