# R-07 / R-08 控件矩阵附录：文本与直接录入

基准为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。下表位置的 WPF 根为 `SonicRoute/`，Native 根为 `Native/Product/Views/`，符号与字段名用于行号变化后的定位。审核了产品 XAML 的控件、样式、C# 创建点、工厂调用、延迟页面与共享编辑宿主。共有 12 处 XAML TextBox 声明、12 处 C# TextBox 创建点，其中取色器的一处工厂用于七个字段。冻结产品没有 PasswordBox、RichTextBox 或 IsEditable=true 的 ComboBox。六位配对码使用明文单行框；快捷键框使用按钮捕获组合。测试程序、模板内的 ScrollViewer、TextBlock、临时引用已有文本框的分部类不重复计数。

本附录的逐项状态均为**源码审查**。系统输入、业务确认、写盘及重启结果按总报告带证据的场景归属；真机 IME、触摸键盘、九种语言、多 DPI 和外设场景未由本表证明。[主页面执行矩阵](R07-Pages.md) · [独立窗口执行矩阵](R07-Windows.md) · [托盘执行矩阵](R07-Tray.md)

## 编辑宿主与提交约定

冻结 `App.xaml:501` 指定圆角 8、边线 1、默认内边距 10×6、搜索图标/占位、只读底色、强调色选择和焦点态。Native 由 `Ui/UiPrimitives.cs:517` 的 DrawTextBox 绘制框体，`Ui/InlineEditHost.cs:109` 按需创建拥有者窗口与 Unicode EDIT，布局使用 DrawTextBox 的裁剪区域。正在编辑时 GDI 字体固定字重 400、选区与光标使用系统 EDIT；应用改名框原版的半粗输入字重与自定义主题选区未完全等价。应用未定义密码输入入口。历史/许可证分别持有独立的只读子 EDIT。

下表“提交”指 Enter、正常失焦或 Tab 提交当前文本。Esc 不调 Committed；这会丢弃未提交的单框编辑。对于实时过滤、实时重命名、300 ms 防抖字段，在 Esc 前已发送给所属业务的变更仍然有效。单行输入沿用系统编辑、复制/粘贴、撤销和选区；内联子类显式处理 Ctrl+A、Tab/Shift+Tab、IME 组合中的 Enter/Esc 及组合完成后的变更。已确认的规则参数先进入编辑草稿，保存规则才写 JSON，取消规则丢弃整份草稿。只读字段不回写。

## 主页面、设置与自动化

|控件 / Native ID|冻结位置及类型|Native 位置|校验、提交、取消和差异|
|---|---|---|---|
|应用别名 `apps.rename`|MainWindow.xaml:1195，AppsRenameBox；MainWindow.xaml.cs:598/605/1074|AppsPageView.cs，BeginInlineRename；IViewPage.cs，AliasEditor.Open|按进程名写 AppNames；空白移除。以当前显示名进入并全选；打开后直接失焦不会新增默认名称别名。300 ms 稳定输入或正常失焦后存，Esc 可丢弃防抖期限内的输入。|
|播放设备别名 `st.devname.out.{id}`，笔形入口 `st.devchk.edit.out.{id}`|MainWindow.SettingsLayout.cs:87/89 动态 MakeNameRow；MainWindow.xaml.cs:1053/1074|SettingsPageView.cs，DrawDeviceNameRows:803 和设备卡片的 AliasEditor 调用:734；笔形入口内联 tag 为 st.rename.{id}|稳定端点 ID 写 DeviceNames，包含系统默认输出，未设名称留空；300 ms 或失焦存，Esc 同上。|
|录音设备别名 `st.devname.in.{id}`，笔形入口 `st.devchk.edit.in.{id}`|同一工厂 MainWindow.SettingsLayout.cs:89；输入设备列表由 SettingsDevices(eCapture) 产生|SettingsPageView.cs，设备别名调用及 IViewPage.cs，AliasEditor.Open|与播放别名相同，设备变更重绘时按稳定 ID 定位。|
|语言显示名 `st.lang.name.{code}`|MainWindow.xaml.cs:2056 动态 TextBox，保存按钮在同一行|SettingsPageView.cs:265，语言编辑字典与 RenameCustomLanguage|提交仅更新该语言草稿。点语言行保存才写外部语言文件，取消单框不会触发文件写入。|
|音量步长 `st.volstep.box`|MainWindow.xaml:817；MainWindow.xaml.cs:1448/1450|SettingsPageView.cs:445/449，VolumeStep 编辑提交回调|整数 1–20，越界钳制。Native 按本轮要求拒绝非法输入和 % 并保留已确认值，与冻结遇非法值退到默认 4 的行为有意区别；未变的合法输入不保存。|
|主题主颜色 `th.hex`|MainWindow.xaml:588；MainWindow.ThemeLayout.cs:51；ThemeColorPickerWindow.cs:357|ThemePageView.cs:183/703，CommitHex|提交后应用，非法值提示。冻结与 Native 均接受可带 # 的六位 RGB。八位 ARGB 和夹杂空白的六字符输入都拒绝，配置不变；主页面与取色器遵守同一规则。|
|历史容量 `exp.hist.limit`|MainWindow.ExecutionHistory.cs:73 动态 _historyLimitBox，MaxLength=2|ExperimentalPageView.cs，exp.hist.limit / ApplyHistoryLimitAsync|至多两字符；提交只接受 1–64 MB。写 ExecutionHistoryLimitMb 成功才通知历史服务调整；非法值提示，配置提交失败保留原设定；历史服务后续缩减文件的 LastError 由 ApplyHistoryLimitAsync 提示。|
|规则搜索 `auto.search`|MainWindow.xaml:432，AutoSearchBox；搜索样式同文件:224|AutomationPageView.cs:284|实时过滤规则，搜索图标；不写配置。Esc 结束输入，已生效的筛选文本仍在。|
|规则名称 `edit.name`|MainWindow.xaml:481，AutoNameBox|AutomationPageView.cs:508，ValidateEditor / SaveEditorRule|提交更新编辑中规则名称，保存规则时校验；取消单框不回写，取消整条规则丢弃草稿。|
|步骤延时 `step.{i}.delay`|MainWindow.AutomationEditor.cs:163；MainWindow.xaml.cs:3354|AutomationPageView.cs，UpdateDelay|冻结与 Native 实时校准至 0–60000 ms；输入空串时留上次值，非法或超限时修正 EDIT；IME 组合结束再校验。修改进入草稿，取消整条规则丢弃，保存规则才落盘。|
|OSD 标题 `step.{i}.osdt`|MainWindow.xaml.cs:3886 动态，仅对应 OSD 动作|AutomationPageView.cs:833|单行，提交到草稿 OsdTitle；冻结 TextChanged 更新同一草稿。弹框外部效果以实际执行规则为准。|
|OSD 正文 `step.{i}.osdx`|MainWindow.xaml.cs:3899 动态，仅对应 OSD 动作|AutomationPageView.cs:840|单行，提交到 OsdText；取消单框或规则不产生新文件。|
|启动路径 `step.{i}.li.{j}`|MainWindow.xaml.cs:4207 动态，AllowDrop + AutoLaunchBox_Drop|AutomationPageView.cs:882，libr / openwith 同一行|键入或文件对话框进入 LaunchItems[j].Path；打开方式独立保存。可把 Explorer 文件拖到指定步骤的路径框或添加按钮；宿主按实际命中 ID 投递，验证仍为原步骤后去重追加到草稿。|
|程序路径 / 命令 `step.{i}.ps.{j}`|MainWindow.xaml.cs:4330 动态，AllowDrop + AutoPathBox_Drop|AutomationPageView.cs:935|每行一个 ProgramPaths[j]，可保留原文，提交仅进草稿。也可向指定步骤的输入格或添加按钮拖文件，按冻结行为加入该步骤的程序路径，去重；不隐式执行。|

## Full 设备页与伴随窗

|控件 / Native ID|冻结位置及类型|Native 位置|校验、提交、取消和差异|
|---|---|---|---|
|手机名称 `phone.rename`（同入口 `phone.rename.box`）|PhoneDevicePage.xaml:166；PhoneDevicePage.xaml.cs:410/417/419，默认隐藏，64 字符|PhoneDevicePageView.cs:940，Store.Rename|全选，实时修改当前 profile 名称；回调核对 profile.Id，关闭编辑不能还原已经提交的名称。|
|配对地址 `phone.pairaddr`|PhoneDevicePage.xaml:421；PhoneDevicePage.xaml.cs:381|PhoneDevicePageView.cs:962，PairAsync|实时更新临时字符串，提交修剪空白；点配对才执行，不把草稿保存成已配对记录。|
|配对码 `phone.paircode`|PhoneDevicePage.xaml:423，MaxLength=6；PhoneDevicePage.xaml.cs:382|PhoneDevicePageView.cs:971/988|可见单行至多六字符；不设密码样式。按钮捕获地址/码后清空码，业务异步返回结果。|
|手工连接地址 `phone.connaddr`|PhoneDevicePage.xaml:434；PhoneDevicePage.DirectConnection.cs|PhoneDevicePageView.cs:980，DiscoverAndConnectAsync|临时手输地址，提交修剪，点连接才执行；已保存设备是独立下拉。退编辑不取消已发出的连接。|
|手机应用目录搜索 `phone.appsearch`|PhoneDevicePage.xaml:491；PhoneDevicePage.xaml.cs:465，CatalogSearch|PhoneDevicePageView.cs:957 / PhoneDevicePageView.Catalog.cs，SetCatalogSearch|搜索图标，实时按目录过滤；不改变固定应用和设备设置。|
|伴随窗搜索 `companion.search`|PhoneAudioWindow.xaml:97，SearchBox|PhoneAudioWindowView.cs:1211/1486|搜索图标，实时在伴随窗过滤；无配置写入。|
|AirPlay 接收名称 `phonepage.airplay.name`、`companion.airplay.name`|AirPlayReceiverView.xaml:59；AirPlayReceiverView.xaml.cs:111，公共控件，两处复用，50 字符|AirPlayReceiverViewControl.cs:41/328；PhoneAudioWindowView.cs:112|两入口限制 50 字符，提交 NormalizeName，再 TryCommit AirPlayReceiverName；未提交 Esc 不写；空名用默认显示。PIN 设置是开关，没有可编辑密码框。|

## 取色器与只读文本

|控件 / Native ID|冻结位置及类型|Native 位置|校验、提交、取消和差异|
|---|---|---|---|
|红 `cp.r`|ThemeColorPickerWindow.cs:152/220/245|ColorPickerWindow.cs:13/213/254|整数 0–255，非法值恢复当前选中色。仅确认对话框后写主题。|
|绿 `cp.g`|同文件:153/220/245|同一 CommitField|整数 0–255，更新绿色分量。|
|蓝 `cp.b`|同文件:154/220/245|同一 CommitField|整数 0–255，更新蓝色分量。|
|色相 `cp.h`|同文件:133/220/245|同一 CommitField|十进制 0–360。|
|饱和度 `cp.s`|同文件:134/220/245|同一 CommitField|十进制 0–100%，转为 0–1。|
|亮度 `cp.l`|同文件:135/220/245|同一 CommitField|十进制 0–100%，转为 0–1。|
|Hex `cp.hex`|同文件:145/220/245/357|同一 CommitField|可带 #，严格接受六位十六进制。七个输入均只改变对话框选中色；确定提交，关闭/取消丢弃。|
|更新说明 `st.about.notes`|MainWindow.Updates.cs:129，动态只读多行、自动换行与滚动|SettingsPageView.cs:1005；Ui/InlineEditHost.cs:118|详情可用才注册；原生只读多行 EDIT 支持选择、复制和自身垂直滚动，无写回。|
|执行历史明细 `EDIT 101`|MainWindow.ExecutionHistory.cs:152，动态只读多行 _historyDetails|ReadOnlyWindow.cs:58，历史模式 _detail|选定条目后显示真实历史；Ctrl+C / 选择 / 滚动不修改日志，刷新从历史存储读。|
|许可协议 `EDIT 101`|MainWindow.Licensing.cs:24，动态只读协议文本|ReadOnlyWindow.cs:58，许可模式 _detail|另一个 HWND 中的只读多行文本，可选择复制；退出窗口无写入。|

## 控件类型不同但可录入的现有入口

|Native ID|冻结 WPF 参照|Native 位置与作用|保存及校验|
|---|---|---|---|
|全局动作 `hk.act.{action}.pill`|MainWindow.xaml.cs:2548/2630 动态按钮|HotkeysPageView.cs:109/221；HotkeyBinding.cs:6|单击框进入录入，支持键盘及中键/侧键/滚轮组合、录入时暂停实际全局动作，Esc 清除，失焦撤销录制。动作名和注册链见主页面矩阵。|
|规则快捷键 `edit.hk.rec`|MainWindow.xaml:496 按钮|AutomationPageView.cs:529/1256，同一键盘鼠标格式|写规则草稿，保存时注册；取消草稿不写磁盘。|
|触发应用 `edit.trigapp.box`、目标应用 `step.{i}.app`|MainWindow.xaml:505 的 AutoTriggerAppCombo；MainWindow.xaml.cs:3703 的 BuildStepParams |AutomationPageView.cs:554/775|冻结采用不可自由键入的下拉选择；旧 Native 同时提供自由键入和下拉，提交后写草稿 TriggerApp / TargetApp。|
|OSD 坐标 `th.osd.ox`、`th.osd.oy`|MainWindow.xaml:634 的实际位置调整按钮|ThemePageView.cs:322/333|旧 Native 另提供数值入口；提交整数，限制 −2000 至 2000。|
|面板坐标 `th.panel.cx`、`th.panel.cy`|MainWindow.xaml:697 的面板位置调整按钮|ThemePageView.cs:413/420|旧 Native 的附加数值入口；合法整数设为 custom 并保存，解析失败保留旧值。|
|实验页 OSD 宽度 `exp.osd.w`、字号 `exp.osd.font`|MainWindow.xaml:669 起的主题滑块组|ExperimentalPageView.cs:307/326|附加数值入口。宽 180–600；字号可输入比例/百分比，限 0.7–2.0。拒绝 NaN 和正负无穷，保留原配置；提交成功再 OSD 预览。|
|实验页 OSD 渐入 `exp.osd.fin`、渐出 `exp.osd.fout`|MainWindow.xaml:675 起的主题滑块组|ExperimentalPageView.cs:353/370|附加数值入口，分别限 0–500 和 0–1000 ms；提交保存，Esc 丢弃未提交值。|

系统文件打开、另存为、选择文件夹对话框的文件名及目录输入框交由 Windows Shell 持有。托盘没有产品文本输入框。对照源码中的动态菜单名、路径标签、系统托盘提示和 OSD 显示名都只读展示，没有进入 EDIT 的输入路径。
