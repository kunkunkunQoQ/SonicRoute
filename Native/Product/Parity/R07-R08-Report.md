# SonicRoute 旧 Native AOT 产品 R-07 / R-08 交付记录

冻结基准为 WPF 1.36 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。对照运行使用该版发布收据匹配的 x64 Full。旧原生产品基线为 `1272253382567de5f44bf87436ea9bea32922b34`，实际开工点为含上一轮报告的 `93fa96941e24e8796b113879c805cd3a9762f06f`。在独立工作树的 `codex/native-legacy-ui-repair-20261125` 完成本地修改。

## 托盘首开和级联菜单

右击托盘后，第一层直接显示「系统默认播放：设备名」。名称取当前 Windows 默认播放端点，可显示已设置的设备别名。为适应用户截图中的紧凑菜单，根行的设备名限 16 个 Unicode 文本元素，设备子菜单限 24 个、文字列最多 196 DIP。优先保留 Gaming、Aux、Media 等可区分的通道，悬停可读全名。显示截短和设备选择分开处理，点击仍传递完整端点 ID。

| 旧候选问题 | 修改位置及验证方法 |
|---|---|
| 必须展开才看见真实设备，驱动名重复占宽 | `TrayMenuController.Show/ShowReady/OutputSummary/SystemOutputLabel`、`ProductAudio.DefaultEndpoint`。打开时只查一个端点，子菜单展开时才枚举。音频服务超过 80 ms 仍未返回时，可先使用菜单其他命令，结果到达后在原行更新。按 PID 和图标 ID 找到 Explorer 中的真实图标，用系统输入右击，展开前读取首层截图。 |
| 菜单包含冻结版没有的顶级分支，动态列表整体重建会中断级联 | `TrayMenuController.CurrentMenu/OutputMenu`、`PopupMenuWindow.UpdateItem/UpdateLabel`。按冻结顺序恢复面板、主窗、条件性更新、当前应用、默认播放、麦克风静音、常用规则、暂停滚轮与退出。当前应用包含静音、音量档和输出选择，规则及麦克风状态按实际服务加载。完整结构与 Full 条件见托盘矩阵。 |
| 通知版本和坐标解释不一致，级联之间抢激活 | `Shell/Tray.cs::OnTrayMessage` 按 v4 通知解析图标、事件和屏幕锚点，保留旧通知回退。根 HWND 接受激活，子菜单共享键盘路径。跨菜单移动有短暂容错，Esc 逐层返回，关闭销毁窗口并取消过期回调。 |
| 多入口状态分歧，失败操作仍可能显示成功 | `TrayMenuController.SetOutput/RunAudio`、`HostViews.SetTrayWheelPaused`。调用现有业务后读回，按真实结果反馈。默认播放核对 Console、Multimedia 两种角色。暂停滚轮核对监听安装和配置提交。 |

## 控件执行、输入和持久化

按冻结 XAML、控件模板、动态创建点，以及 Native 绘制、注册、分派和业务代码核对。动态行用可展开的 ID 模板记录，配置、规则文件、Windows 音频策略和外部设备分别列出实际所有者。每项的输入时机、禁用条件、命令、反馈、写入和已知偏差见四份矩阵。[主窗及全部页面](R07-Pages.md) · [面板、独立窗口和弹层](R07-Windows.md) · [托盘菜单逐项](R07-Tray.md) · [输入控件逐项](R07-Inputs.md)

| 范围 | 实际改动 |
|---|---|
| 快捷键及规则录入 | `HotkeysPageView` 与 `HotkeyBinding` 恢复冻结的分组、行距和 180×32 DIP 绑定框。直接点击框录入，Esc 清空。录制期间临时抑制已注册的动作，失焦、切页或完成时恢复注册。警告区分未注册与冲突后使用替代组合，规则快捷键在确认保存规则时生效。 |
| 文本与只读控件 | `InlineEditHost` 用按需创建的 Windows Unicode EDIT 维护光标、选择、剪贴板和输入法会话，Direct2D 保留框体、焦点及外围。绘制时把同一字段的文本区和可见裁剪区传给宿主，滚动出视口后结束编辑。主窗、手机面板、取色器共用编辑生命周期，Enter、正常失焦、取消和 IME 组合有不同语义。更新说明支持可选择复制的只读多行文本。 |
| 值校验及草稿 | 应用改名使用当前显示名，未修改直接离开不会创建别名。设备别名停键 300 ms 或正常失焦提交。主题 HEX 接受六位 RGB，支持可选的 #。取色器确认才写配置。自动化延时键入时校准至 0–60000 ms，规则文件在保存时更新。真实文件投放按指针所在的路径框或添加按钮进入对应步骤。非法音量步长保留既有值，此项依用户数据保护要求，与冻结版回退到 4 有差别。 |
| 保存与恢复 | `ConfigService.TryCommit/WriteSnapshot` 记录成功写入的快照，写入失败时将拥有者持有的配置恢复到上次确认值。保留队列、备份、旧配置恢复及字段兼容。普通设置、别名、热键与规则草稿分别处理，拖动预览与结束保存分开。正常退出限时等待挂起的配置写入。 |
| 多入口业务 | 修正应用、面板及弹层对连续音量或路由操作的顺序、旧读回和失败回滚。按稳定会话或端点标识分派，异步完成后核对窗口生命周期。手机文件传输队列移至已有宿主，结束一项后可推进下一项；断线和真实文件传输尚待设备确认。 |
| 用户视觉标注 | 主题模式沿用右侧截图的 136×46 DIP 等宽选项，间隔 8 DIP，主题色下方三个额外色块已移除。保留标题固定快捷面板、路由短标签、设置页默认应用的单一下拉入口。 |

逐文件变更与原因记录在交付清单。运行输入、控件矩阵及托盘的源码检查点为 `3c61df930f5872581ab8fb0f5ca4cfaafb049fe9`，编辑校验、别名和拖放补正后的最终产品检查点为 `795bc7f661575cbe24e6e482657a7b623e9c13cb`。[43 个文件的修改明细](../../../../r07-release/changed-files.md)

## 系统输入、真实业务与磁盘证据

使用独立配置和受测 EXE 指纹。鼠标和按键走 Windows 系统输入；截图、配置文件、窗口及音频 API 用于观察结果。WPF 和 Native 主窗对照使用简体中文、浅色、144 DPI、920×620 DIP。设备条目取自测试当时可用的音频端点，部分旧版菜单宽度与最终版不同。

| 验证层次 | 实际观测 |
|---|---|
| 源码审查 | 四份矩阵覆盖产品控件及其动态变体。冻结版共有 12 处 XAML 文本框声明、12 处 C# 创建点，其中一处工厂生成取色器七个字段。托盘使用的 33 个字面语言键和设备名占位符已核对九个语言文件。 |
| 编译及产物检查 | Full、Lite x64 Release 均成功生成原生代码。最终两份增量发布日志各 0 警告、0 错误。首轮两版各有 780 条已有依赖警告，与上一版完整日志逐条一致，其中 StoreInterop 777 条、Core 3 条。PE 头均为 AMD64/PE32+，CLR 目录为空，EXE 与相应的原生中间产物哈希一致。[构建收据](../../../../r07-release/build-receipt.json) |
| 系统输入与 Windows 默认设备 | 最终 Full 在真实 Explorer 图标上右击，首层显示 Media。展开、跨父子边界、切换兄弟菜单和逐层 Esc 均生效。点 Aux 后 Windows Console、Multimedia 两角色均切至所选端点，通信角色保持。重新右击后首层已是 Aux，随后从菜单恢复 Media。双击打开主窗、单击打开面板。单独验证暂停滚轮的勾选落盘，两次从托盘正常退出并重新启动。[托盘动作与三角色读回](../../../../r07-evidence/native-1ab96f57a/result.json) · [托盘重启收据](../../../../r07-evidence/tray-save-7000e1ee6/result.json) |
| 真实业务，应用音频 | 最终 Full 从应用页操作隔离测试音频会话，Windows 读回音量 100% → 36%、静音开启，两种 per-app 角色均分配到所点端点。Ctrl 增选第二设备后 WASAPI 读到新增活动输出会话。结束时应用路由恢复继承，音量恢复 100%、静音关闭。双设备实际听感仍待实机确认。[独立 Windows 音频读回](../../../../r07-evidence/native-light-audio-final-66b127a1fa/result.json) |
| 快捷键、规则与筛选 | 最终 Full 对两个绑定录入相同 Ctrl+Shift+F23，检查冲突提示，录制 Shift+向下滚轮及中键，Esc 清空。离开录入区后 Ctrl+Shift+F22 打开快捷面板。通过真实文件对话框导入规则，步骤延时输入 7 后输入无效字符，保存文件仍为 7 ms。Ctrl+Shift+F21 实际显示规则的自定义 OSD。输入搜索与清空分别隐藏和恢复规则行。[热键注册与触发](../../../../r07-evidence/native-light-bindings-f99faf21f6/result.json) · [规则、搜索和 OSD](../../../../r07-evidence/native-light-automation-14b96c28ba/result.json) |
| 系统输入，选择和输入法 | 最终 Full 在原生 EDIT 中双击和拖动选词，Ctrl+A 后替换只留下一个字符。右键打开系统编辑菜单，Esc 关闭。Tab 提交别名，Esc 丢弃下一次未确认替换。在默认桌面按 N-I-H-A-O，候选栏出现在字段下方，空格确认“你好”，失焦后从隔离配置读回。[选区及右键菜单](../../../../r07-evidence/native-light-selection-c4c0ffeebe/result.json) · [Tab 和取消](../../../../r07-evidence/native-light-edges-782ffac873/result.json) · [中文输入法](../../../../r07-evidence/ime-live-7a765c4be/result.json) · [候选窗口实图](../../../../r07-evidence/ime-live-7a765c4be/ime-candidates-0.png) |
| 滚动、裁剪与切页 | 最终 Full 在 144 DPI 下滚动一格，带焦点的同一 EDIT 及其宿主向上移动 72 个物理像素（48 DIP），原位置已命中主页面。继续滚出可见区域时两个 HWND 均销毁，别名提交。再次编辑后切往主题页，两个 HWND 也均销毁，第二个值正确提交。[窗口位置、命中及保存记录](../../../../r07-evidence/native-light-edit-lifetime-18d15d8a78/result.json) |
| 持久化与正常重启 | 最终 Full 在隔离配置上制造文件共享冲突，原文件字节完整，解锁再次点击成功写入。快速点击以最后一次为准，滑块按住保留原值，释放后从 85 写入 63。非法取色和取消保留原值，确认改变颜色，Unicode 别名正常失焦保存。两次实际点击托盘退出，使用同一份配置重新启动，核对了磁盘、新界面的别名、不透明度读数和色样像素。另验证六位 HEX 生效、八位被拒，Lite 真实打开主窗、切页、写入主题并关闭。[正常退出及重启收据](../../../../r07-evidence/persist-6374b1232/result.json) · [HEX 与配置导入重启](../../../../r07-evidence/native-light-restart-dd92eaa9df/result.json) · [Lite 系统输入](../../../../r07-evidence/native-light-lite-1665a9ed8a/result.json) |

**单独记录的未通过项**。Shift+托盘滚轮实际改变了系统音量，但反向滚动一次后系统读数为 100%，先前为 98%。冻结版与 Native 均将未绑定的托盘滚轮消息继续传给后续钩子。现有证据不能定位此次差额的来源。清理时只对原端点恢复到 98%，收尾独立读回两种播放角色均为 Media、98%，通信角色仍为 63%，均未静音。该项需在独占托盘输入环境复测。[原始收据](../../../../r07-evidence/native-1ab96f57a/result.json) · [测试证据索引](../../../../r07-evidence/final/evidence.json)

## 对照图、资源与剩余范围

同一组十个系统输出设备和 144 DPI 下，冻结版输出子菜单实宽 517 像素，最终 Native 为 294 像素。两张首层截图都是指针进入设备行之前取得。默认快捷键行与编辑焦点的三栏图使用冻结 WPF、修改前 Native 和最终 Full 的真实窗口。录入与冲突状态另有最终版实图。[托盘首层](../../../../r07-evidence/final/review-pairs/root-e4ef8b44.png) · [输出级联](../../../../r07-evidence/final/review-pairs/output-e4ef8b44.png) · [快捷键页面三版对照](../../../../r07-evidence/final/review-pairs/hotkeys-e4ef8b44.png) · [输入框焦点细节](../../../../r07-evidence/final/review-pairs/focused-edit-detail-e4ef8b44.png) · [录入态](../../../../r07-evidence/native-light-bindings-f99faf21f6/recording.png) · [冲突态](../../../../r07-evidence/native-light-bindings-f99faf21f6/conflict.png) · [截图来源](../../../../r07-evidence/final/review-pairs/source-e4ef8b44.json)

最终 Full 同一隔离进程，每个状态取三个约 250 ms 相隔样本的中位数。冷启动采样时进程年龄 2.62–3.13 秒，屏幕上无产品窗口。冻结 WPF 1.36 既有冷启动样本为 Working Set 119.9 MiB、Private Bytes 71.4 MiB；冻结版未在相同托盘菜单位点采样。长期泄漏和持续音频负载不在本组数据的测量范围。[采样条件与中位数](../../../../r07-evidence/final/memory/summary-43972.json) · [原始采样](../../../../r07-evidence/final/memory/observe-43972.json)

| 最终 Native Full | Working Set，MiB | Private Bytes，MiB |
|---|---:|---:|
| 托盘冷启动 | 23.78 | 6.23 |
| 首层菜单可见 | 62.09 | 58.87 |
| 默认输出级联可见 | 63.89 | 60.05 |

**视觉偏差与尚未验证的操作**。原生 EDIT 使用系统选区色，应用改名输入态字重与冻结版有差别。设置页设备别名的对照图中，编辑框左边缘较冻结版靠左约 20 个物理像素，右边缘基本一致。原生附加手工坐标、规则目标输入等已有入口及其与 WPF 的差别逐项列在输入附录。测试覆盖了本机微软中文输入法，日文和韩文 IME、跨屏不同 DPI、九语全状态、实际 Explorer 拖放、剪贴板剪切/粘贴完整往返、Android、AirPlay 与虚拟屏仍待验证。本机剪贴板已有用户数据，未替换其内容。自绘控件尚无与 WPF 等同的完整 UI Automation 树。

## 隔离启动与用户实机验收

交付根目录为 `E:/kunkun/SonarSwitch/outputs/native-legacy-ui-repair/r07-release/`。脚本会指定候选根目录及实例 ID，Full 与 Lite 各用独立 data 目录。使用下面的脚本启动即可保留本次测试设置供下次启动读取。

| 版本 | EXE | 隔离启动 | SHA-256 |
|---|---|---|---|
| Full x64 | [SonicRoute.exe](../../../../r07-release/candidate/Full-x64/SonicRoute.exe) | [Run-Full-isolated.cmd](../../../../r07-release/Run-Full-isolated.cmd) | `e4ef8b44b396404c4407f5c64f46a6b85f4401d1b2abf133439e3bcf24a7638a` |
| Lite x64 | [SonicRoute.exe](../../../../r07-release/candidate/Lite-x64/SonicRoute.exe) | [Run-Lite-isolated.cmd](../../../../r07-release/Run-Lite-isolated.cmd) | `c7e7b39e0b981e0424ae08334192e35bf44f4f91e21d172bf492a19dc5b1e13e` |

用户实机验收待确认。优先覆盖以下四组操作。

1. **托盘**。记下原默认设备，右击候选图标，先看首层，再移入「系统默认播放」。选择另一个测试输出，在 Windows 声音设置中核对，重新右击检查短名与勾选，最后切回原设备。
2. **快捷键与菜单**。点击一个绑定框，用当前空闲的键组合或修饰键加鼠标滚轮录入。切页后触发实际动作。再测试冲突提示、Esc 清除，恢复原绑定。
3. **输入与重启**。在隔离配置编辑设备别名，用常用输入法组字、选词、粘贴、失焦保存，再打开修改后按 Esc。拖动不透明度，确认一次取色、取消一次，正常退出并重新打开同版，核对已确认值。
4. **拖放与设备**。把测试文件拖到自动化步骤的具体路径框两次，检查重复处理和规则取消。使用 Full 的常用 Android/AirPlay 设备验证连接、媒体及传输，重点看传输中关闭文件窗口后能否继续。
