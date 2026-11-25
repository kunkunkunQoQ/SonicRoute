# SonicRoute 旧 Native AOT UI 与 WPF 1.36 对照报告

本轮从输入修复点 `550eb93a12762f95d245975fa5b1e64cc6b71b2e` 开始。冻结 WPF 源码和截图基准均对应 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。运行对照使用正式收据列出的 x64 Full，SHA-256 为 `C4289848808DB9B8D15367690181FC2CBE9A87A482F310394F0C4C034B750F1B`。[WPF 正式收据](../../../../../release-v1.36-20261124/published-receipt.json)

## 差异矩阵与实现

| 范围 | 源码定位及结果 | 仍有差别的代表项 |
|---|---|---|
| R-01、R-02 共享控件 | [共享矩阵](Shared.md)列出初始问题和最终处理。按 WPF 资源核对字体、9 套语言、主题参数和路径图标。恢复开关、单选、复选、弹出框和滚动槽的尺寸、着色、选择态和禁用态。下拉最长 320 DIP、弹出边框圆角 9 DIP。菜单按工作区量宽和翻转 | DirectWrite 字体回退、抗锯齿和基线；分层 HWND 的原生提示框和焦点环；下拉滑入效果 |
| R-03 主窗口与页面 | [页面矩阵及 XAML 具名控件索引](Main-pages.md)按冻结入口核对应用与多设备路由、自动化、快捷键、主题、设置、实验、Full 手机与 AirPlay。修正侧栏、应用列和滚动布局。恢复自动化全宽列表与编辑器切换、组合筛选、行命令、设置分组顺序、外部语言编辑及删除。补回可选取文本的历史和许可窗口。Full 应用目录可搜索、批量选择，去掉旧候选前 60 项限制 | 历史列表使用单行系统 ListBox；部分长说明仍有换行差异；设备别名的保存时间粒度与 WPF 不同 |
| R-04 快捷面板与独立窗口 | [窗口矩阵](Windows.md)包括经典/简洁行、托盘及级联菜单、OSD、输出音量、设备菜单、取色器、Full 手机媒体、拖放预览、虚拟屏恢复、文件和传输界面。恢复面板页脚、默认工作区定位，以及取色器的双栏、96 色、HSL、HEX/RGB 和确认/取消 | 面板固定操作按用户标注移至左上标题，整机接收入口保留。屏幕移除后的自定义坐标钳制、矢量音量标志和取色器标题栏与 WPF 有别；触屏长按拖放尚有差距 |
| R-05 输入与状态 | 左键按下、松开、系统取消、异步捕获转移仍经过原有共享入口。键盘 Tab 遍历可见控件，到滚动区域末端可露出下一组；Enter/Space 按目标身份在松键时激活。点击轨道按页滚动，长按依据系统按键重复参数，在失活或释放时停。原生 EDIT 处理 Ctrl+A、Enter、Tab、Esc；修正多处把正常失焦误当作取消的问题 | 完整输入法候选窗、多显示器跨 DPI、全部硬件动作及全部组合快捷键还需要用户确认。自绘项未提供完整 WPF UI Automation 树 |
| R-06 资源与生命周期 | 关闭/隐藏视图释放所属 DIB、DC、渲染目标、工具提示和页面订阅。位图按窗限 96，版本替换不重复入队；格式限 64，短文本测量值限 128，成功和失败的图标缓存均有限额。退出时释放共享 D2D/DWrite。托盘冷启动路径延迟创建图形资源。入场动画结束即停止，峰值采样限于面板可见且有可采样数据。快捷面板热点切片改用 span | 资源数字来自短时隔离采样。持续播放、传输和长时图形资源变化未测量 |

## 用户标注的四处改动

| 标注 | 实际行为及源码 |
|---|---|
| 主题色下方的三个小色块 | `ThemePageView.RenderModeAndAccentCard` 移除了三个额外按钮及命中项。主题色区保留当前色样、HEX 输入和取色器。已有蓝、绿、紫名称配置继续按原颜色显示。仅聚焦并离开 HEX 不改变原配置。 |
| 点击左上品牌标题固定面板 | `QuickPanelWindow` 使用同一个 `qp.title` 处理点击和键盘激活。固定时标题变为强调色，切到另一窗口仍保持打开；再次点击后恢复失活关闭。Full 和 Lite 均已移除右上独立图钉。 |
| 应用路由设备名称过长 | `AppsPageView` 的快捷按钮采用冻结 `PanelDevices.ShortName` 规则，去掉括号说明，超过 12 个 Unicode 文本元素取前 11 个加省略号。量宽和绘图用同一短名，提示和选择下拉显示全名。实际路由使用设备 ID，重复短名仍可区分。 |
| 设置页默认应用选择框 | `SettingsPageView.RenderDefaultAppCard` 采用冻结模板的三列选择卡、共同行高及 36 DIP 高的单个全宽下拉。「最近」「上次」下不可操作，「指定应用」下可选择实际音频会话，保存进程名。切换模式保留已保存值。共享下拉恢复禁用底色、边框、文字和箭头；主窗内容计入 1 DIP 外框，滚动区按真实轨道量宽。 |

## 已定位的交互根因

- 鼠标丢点击的原始路径位于 `Ui/LayeredWindow.cs` 的 `WmLButtonUp` 与 `WmCaptureChanged`。主动释放前结束宿主手势，意外转移才取消页面按下状态。原始侧栏问题位于 `Views/MainWindowShell.cs` 和 `Ui/UiPrimitives.cs`，完整布局按同一可见区裁剪绘图和命中。
- 旧候选在下拉框收到 Esc 后关闭 HWND，快捷面板的失活关闭又将面板隐藏。`Ui/ComboPopupWindow.cs` 的 `CloseToOwner` 在显式选择或取消时归还焦点。对照时通过实际打开面板、下拉、按 Esc、再用鼠标点开主窗口验证。
- `Ui/InlineEditHost.cs` 仅在提交时发布回调，回调参数表示是否由 Enter 提交。多个调用者曾用它判断要不要保存，导致点开下一控件会丢弃设备别名、延时、配对地址或接收名。现在调用者处理正常提交，Esc 由宿主取消。
- `Views/ThemePageView.cs` 与 `Views/ColorPickerWindow.cs` 将取色中的暂存值与持久化分开，确认才提交。`Views/AutomationPageView.cs` 将列表、编辑草稿和返回状态分开。`Views/ReadOnlyWindow.cs` 在主线程结束历史读取，并处理关窗和队列拒绝。
- `ApplicationHost.OnSignal` 与 `LayeredWindow.RefreshVisibleThemes` 使系统调色板变化只重画可见 UI；已打开的菜单、原生 EDIT、取色器和主窗从最小化恢复时都能取得当前主题。该路径避免因桌面改色重载自动化或重新枚举音频设备。

## 最终构建与隔离启动

| 版本 | Native AOT Release EXE | 隔离启动 |
|---|---|---|
| Full x64 | `E:/kunkun/SonarSwitch/outputs/native-legacy-ui-repair/parity-release/candidate/Full-x64/SonicRoute.exe` | [Run-Full-isolated.cmd](../../../../parity-release/Run-Full-isolated.cmd) |
| Lite x64 | `E:/kunkun/SonarSwitch/outputs/native-legacy-ui-repair/parity-release/candidate/Lite-x64/SonicRoute.exe` | [Run-Lite-isolated.cmd](../../../../parity-release/Run-Lite-isolated.cmd) |

双击相应启动脚本进入快捷面板。两版分别使用 `parity-release/manual/SonicRoute.NativeCandidate/legacy-parity/{Full,Lite}/data`。自动化验证另用每次新建的配置目录和 Win32 桌面。旧候选的测试包仍在同级原 `candidate`，不会与本轮包混淆。

两个 EXE 均为 PE32+ AMD64，CLR 目录 RVA 和大小均为 0。Full SHA-256 为 `D716AAE2697B578B7B8E6D8D05CF6709A491CBECD810D9BD6F9ADDF7D59282D4`，Lite 为 `BEAC2302768931D78B7FA97C1D77A4FFE50B68F279CEDE762BADF125B8D168E6`。干净构建日志每版 780 条依赖警告，777 条来自 StoreInterop，3 条来自 SonicRoute.Core，共 21 种代码。两份最终增量发布日志均为 0 警告、0 错误。本轮日志的 Native/Product、IL 和 CA1831 警告数均为 0。[产物、警告分组及哈希](../../../../parity-release/build-receipt.json)

## 同条件对照与输入证据

同一台 Windows、144 DPI、主窗 920 × 620 DIP、面板 372 × 450 DIP。用同一未绑定配置模板，指定主题、语言、行样式、面板位置，分别启动正式 WPF 与候选 Full。实际 Windows 音频会话和默认设备状态随运行时刻变化。每个收据记录 EXE 指纹、版型对应的隔离配置、窗口像素坐标或输入步骤。

[最终四处标注同条件对照图](../../../../parity-evidence/final-1272253/four-changes.png)按行展示快捷面板标题、已选应用的设备短名、设置页全宽下拉和主题色区。可放大查看[完整设置页](../../../../parity-evidence/final-1272253/settings.png)、[已选应用](../../../../parity-evidence/final-1272253/selected-app.png)、[快捷面板](../../../../parity-evidence/final-1272253/quickpanel.png)和[主题页](../../../../parity-evidence/final-1272253/theme.png)。这些画面的 Native 均来自最终 EXE。

| 验证标记 | 结果与证据 |
|---|---|
| 源码审查通过 | 已逐项核对共享矩阵、主页面和独立窗口矩阵。共九份语言资源，每份 944 键。由当前 UI 的直接字面 `L10n.T` 调用提取 453 个不同键，九份均无缺项；动态拼接的键另行按页面审查。[语言核验](../../../../parity-evidence/locale-keys.json) |
| 编译通过 | Full/Lite x64 .NET 10 Native AOT Release，两个 EXE 的哈希均与各自原生链接产物一致。[构建收据](../../../../parity-release/build-receipt.json) |
| 自动化验证通过 | 核对 PE、冻结资源、受测 EXE 指纹、独立配置与窗口 DPI。标注版分别对比九语标准导航，其中中文、英文和德文有标注版实图，其他语言沿用共享控件阶段截图。[多语言对照](../../../../parity-evidence/final-0125fd9/nine-locales.png) |
| 系统输入验证通过，最终 EXE | Full 实际打开快捷面板和主窗，选择应用并核对两列短名。设置为「上次」时点击无弹窗；切到「指定」后选择实际音频应用 `audiodg`，确认隔离配置写入，再切回「上次」。最终 Full 再次真实点击标题固定、切换到另一个前台窗口、取消固定并确认失活关闭。Lite 用鼠标从面板打开和关闭主窗。测试使用定向 SendInput。[Full 输入与采样](../../../../parity-evidence/native-light-annotated-77bb86104e/result.json) · [最终标题固定](../../../../parity-evidence/native-light-pin-66dbb24f2a/result.json) · [Lite 输入](../../../../parity-evidence/litefinal-6d0490ef7c/result.json) |
| 系统输入验证通过，标注阶段 `0125fd9` | 标题固定时外窗真实取得前台，面板保持显示；再次点击标题、切到外窗后面板自动关闭。下拉 Esc 后正常进入主窗、取色取消未写回、确认才写回。Tab、Enter、Space、工具提示、移出取消/移回松开、原生 EDIT 的 Ctrl+A 和 Unicode 输入均有收据。另验证顶边 450 → 402 DIP、自定义坐标保存、另一 HWND 夺取捕获后松开无误点、再次点击生效。[标题固定](../../../../parity-evidence/native-light-pin-47fdd35a33/result.json) · [下拉](../../../../parity-evidence/native-light-visible-121525ee62/result.json) · [取色器](../../../../parity-evidence/native-light-color-f520085893/result.json) · [键盘与编辑](../../../../parity-evidence/native-light-inputs-fd2d4e21f4/result.json) · [捕获与拖动](../../../../parity-evidence/custom-resize-e809028c9f/result.json) |
| 用户实机验收待确认 | 下面的验收清单供真实设备、日常配置副本、常用输入法和显示器环境确认。 |
| 尚未验证 | 全部机器、DPI、输入法与跨显示器组合，完整自动化规则/热键组合，Android/AirPlay 实际音视频和虚拟屏恢复、传输中的断开重连，以及九种语言的所有页面与业务动态数据。 |

系统输入和视觉检查分阶段取证。深色主窗、简洁面板和取色器的全窗图来自标注阶段，最终抽测复拍集中在用户指出的四处。截图索引列有来源哈希。[深色主窗](../../../../parity-evidence/final-0125fd9/dark-apps.png) · [深色简洁面板](../../../../parity-evidence/final-0125fd9/dark-compact-quickpanel.png) · [取色器](../../../../parity-evidence/final-0125fd9/color-picker.png) · [最终截图索引](../../../../parity-evidence/final-1272253/summary.json)

仍可观察到应用路由上方选择框缺少冻结版的小设备图标，部分卡片底部留白、字体基线、下拉箭头与边缘的光栅化也有差别。标注处的设置选择框已恢复单控件全宽布局；144 DPI 对照中，两版左右边缘同为 x=363..1289 像素，卡片顶部均在 y=501。WPF 选择框上下边缘为 y=725..779，Native 为 y=723..776，分别相差 2 和 3 个物理像素。[几何测量](../../../../parity-evidence/final-1272253/settings-geometry.json)

## 资源占用

Full、同一台机器和相同隔离条件。每个状态连续取三点，间隔 250 ms，表中为中位数，单位 MiB。冷启动用独立进程，在启动约 2.5 秒且无可见产品窗口时取样。另一次交互运行按面板、主窗、关窗的顺序进行；最后一行在窗口全部隐藏约 2 秒后采样。

| 状态 | WPF Working Set | Native Working Set | WPF Private Bytes | Native Private Bytes |
|---|---:|---:|---:|---:|
| 托盘冷启动 | 119.9 | 23.5 | 71.4 | 6.1 |
| 快捷面板显示 | 156.4 | 80.9 | 113.9 | 68.6 |
| 主窗口应用页显示 | 185.7 | 91.6 | 164.5 | 80.8 |
| 关窗后 2 秒 | 204.5 | 84.6 | 144.8 | 77.3 |

Working Set 为当前驻留物理内存，Private Bytes 为进程私有提交量。每个产品每种场景只有一次隔离会话，样本支持本轮场景对照。长期泄漏、实际在线业务负载和整版内存降幅需要不同的测量设计。[原始采样索引](../../../../parity-evidence/final-1272253/summary.json)

## 用户实机验收清单

以下均为**用户实机验收待确认**，可在隔离启动器中使用。每项列出可直接观察的结果。

1. 在快捷面板点开设备下拉，按 Esc，再打开主窗口。按住最大化按钮移出后松开应无动作；移出再移回松开应只执行一次。切换侧栏时点刚露出的入口，只有可见区域响应。
2. 在设置页改一个设备别名，点击下一控件，再回来看值。重复一次用 Esc 取消。用实际中文或日文输入法选词，检查候选窗、选区、粘贴以及 Tab 的焦点去向。试一次深浅主题切换和最小化恢复。
3. 在有声应用上用 Ctrl/Shift 选两个输出，分别调整音量。在测试设备上断开其中一个，观察选中态、失败回滚和音频。用右键、滚轮和越界松开复查滑块。
4. 新建一条自动化草稿、添加步骤、拖动排序，再分别试取消和保存。启用执行历史后打开、刷新、选择及复制详情，验证再关窗不会中断后台执行。
5. 两种快捷面板样式各试一次，在不同缩放的屏幕边缘打开托盘级联菜单和输出音量弹层，验证定位、焦点和关闭。取色器取色后先取消后确认，重启核对只保存确认的值。
6. Full 在有设备时试 Android/AirPlay、手机应用目录多选、跨窗口拖放、虚拟屏恢复以及传输期间关窗；Lite 验证没有手机入口。使用长名称或平时使用的第二种语言检查换行和可点击性。

## 本地检查点与文件

| 阶段 | 本地提交 |
|---|---|
| 保留的旧候选 / 交互修复 | `c9ff37cc9a58915cb59dda98825b0801eda8dec7` / `550eb93a12762f95d245975fa5b1e64cc6b71b2e` |
| R-01 差异矩阵 | `b2989d4c7a7037271f6a9b3ffbafa7ed3ca6cb70` |
| R-02 共享控件 | `9138f9a8b141de2ca38dc5f2abf73e9271efb6fd` |
| R-03 主窗口与页面 | `d1a0f028c3b1b3701c6c1e08b95832513f23ef2b` |
| R-04 窗口与面板 | `fd3daf3435cb6c3e769a176b0a2218927fabb35d` |
| R-05 输入与主题 | `ccaf810fc1ee579a1ae1e004a142ca4528591cc6` |
| R-06 资源生命周期 | `4705e25ecd9b8e04317cd61fc68e50ebe885764c` |
| Full 伴随窗口编辑态配色 | `f1beba4c053a0a0efffd87bee18040b604072140` |
| 截图反馈及四项用户标注 | `0125fd9e0f0fc74cc70e5a1b186d923f95a4bc41` |
| 最终产品源码，设置页几何校准 | `1272253382567de5f44bf87436ea9bea32922b34` |

源码工作树为 `E:/kunkun/SonarSwitch/outputs/native-legacy-ui-repair/source/`，分支为 `codex/native-legacy-ui-repair-20261125`。各阶段为本地开发检查点，最终 Full/Lite 的构建收据单列于上。Windows Link Beta 不在本轮范围内。

| 目录 | 实际改动的文件 |
|---|---|
| Native/Product | ApplicationHost.cs |
| Ui | ComboPopupWindow.cs、D2dRenderer.cs、InlineEditHost.cs、LayeredWindow.cs、NativeAppIconService.cs、PopupMenuWindow.cs、UiPrimitives.cs、UiTheme.cs、WindowTooltip.cs |
| Views 主窗与页面 | MainWindowShell.cs、IViewPage.cs、AppsPageView.cs、AutomationPageView.cs、HotkeysPageView.cs、ThemePageView.cs、SettingsPageView.cs、ExperimentalPageView.cs、ReadOnlyWindow.cs、NativeDialogs.cs |
| Views 面板与主题 | HostViews.cs、QuickPanelWindow.cs、TrayMenuController.cs、OutputVolumesPopupWindow.cs、ColorPickerWindow.cs、OsdWindow.cs |
| Views Full | PhoneDevicePageView.cs、PhoneDevicePageView.Catalog.cs、AirPlayReceiverViewControl.cs、PhoneAudioWindowView.cs、PhonePreview.cs、VirtualDisplayStatusWindow.cs、CrossDeviceDragHost.cs、DragIconWindow.cs |
| Parity | Shared.md、Main-pages.md、Windows.md、Report.md |
