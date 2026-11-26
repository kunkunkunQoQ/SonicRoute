# R09 普通页面、实验设置与折叠侧栏

范围为旧 Native 产品的设置、实验设置、主题、自动化、快捷键和主窗导航。WPF 引用均指冻结提交 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a` 下的 `SonicRoute/`。Native 引用从 `Native/Product/Views/` 起算。原有逐控件执行及输入明细见 [页面矩阵](R07-Pages.md) 与 [输入附录](R07-Inputs.md)。R09 的变化以下表为准。

## 冻结版实际装载路径

`MainWindow.xaml` 中的普通页均由资源延迟装载。须从 `MainWindow.DeferredPages.cs` 的 FindDeferredControl、导航选择、可见条件和动态构建入口回查。旧事件处理方法、配置字段或多语言键的存在不能单独证明原版页面挂载过某个控件。普通与实验页在 Full/Lite 共享。移动设备形成的应用、端点候选随 Full 能力和连接状态变化。Windows Link Beta 不在此范围。

|窗口与最终分组|冻结 WPF 页内证据|Native 的最终可达控件及业务|可见、编辑和保存条件|
|---|---|---|---|
|设置·显示的设备|MainWindow.xaml:749–768；MainWindow.SettingsLayout.cs:73、108；MainWindow.xaml.cs:993|SettingsPageView.RenderDeviceVisibilityCard，`st.keep.toggle`、`st.keep.{out,in}.all`、`st.devchk.{out,in}.{deviceId}`|按组全选/全不选，含系统默认端点，写 HiddenOutputDevices/HiddenInputDevices。显示/隐藏不禁用设备。录音组跟随 ExperimentalMic。折叠设备组时出现“更多选项”。|
|设置·设备名称|MainWindow.xaml:768–786；MainWindow.SettingsLayout.cs:87|RenderDeviceNamesCard，`st.names.toggle`、`st.devname.{out,in}.{deviceId}` → AliasEditor|名称编辑只在自己的卡片；空别名时编辑框为空。保存 DeviceNames，显示名和设备 ID 分离。|
|设置·默认控制的应用|MainWindow.xaml:786–801；MainWindow.MemoryLayout.cs 应用候选|RenderDefaultAppCard，`st.appmode.{recent,last,fixed}`、`st.fixedapp.combo`|三个选择卡和单个全宽下拉。仅 fixed 可选，写 DefaultAppMode/FixedAppName。保留先前用户确认样式。|
|设置·杂项|MainWindow.xaml:801–835；MainWindow.xaml.cs:1154、1699|RenderAudioBehaviorCard，`st.startmin`、`st.autostart`、`st.autostart.more/clean`、`st.startpanel`、`st.volstep.box`、`st.traywheel.everywhere`、`st.expmic`、`st.micinpanel`、`st.panelchangesysdef`、`st.panelretainedonly`、`st.collapsedev`、`st.expmode`|音量步长保留 64 DIP 居中数字框，以 Enter 或正常失焦提交。非打包才有清理启动项；麦克风开启才有面板麦克风子项；实验模式开关仅解锁后出现。关闭模式停止历史服务、关闭对应窗口并隐藏导航。|
|设置·语言|MainWindow.xaml:835–870；MainWindow.xaml.cs:1894–2160|RenderLanguageCard，`st.lang.combo/more/export/import/dir/restore` 和 `st.lang.name/save/delete.{code}`|更多展开才显示文件操作；自定义语言存在才有命名、保存、删除。导出弹文件夹选择器；点击多选或拖到“导入语言”才导入。多文件按顺序处理并应用最后成功项，单个覆盖率低于 80% 或批次部分失败给出反馈。文件操作运行期间禁用四个文件按钮。|
|设置·软件更新|MainWindow.xaml:871，MainWindow.Updates.cs:78 起|RenderUpdatesCard，`st.about.checkupd/changelog/autochk/notes/dl/install/cancel/later/ignore`|单独动态卡。打包版转 Store；非打包的检查、自动检查、可选下载、进度、只读发布说明、安装重启和通知随现有 UpdateService 状态显示。|
|设置·关于|MainWindow.xaml:872–912，MainWindow.xaml.cs:1675|RenderAboutCard，`st.about.author/bili/ifdian/github/license`|页底独立卡，第一行本地化署名与版本，第二行作者主页和四个链接；五次点击立即解锁并启用实验导航；保持原先配置存取。|
|实验·后台状态检测|MainWindow.xaml:938–952；MainWindow.DeferredPages.cs:249 起|ExperimentalPageView.Render，`exp.polling`|仅在实验页注册，写 BackgroundPollingFallbackEnabled。|
|实验·最近执行记录|MainWindow.xaml:954–969；MainWindow.ExecutionHistory.cs:55–123|同页 `exp.history`、`exp.hist.limit/view/clear`|启用后才创建容量、说明、查看和清除。容量 1–64 MB，Enter/正常失焦校验并保存，Esc 取消。关闭停止历史写入，保留历史文件。|
|实验·配置文件管理|MainWindow.xaml:971–1017|同页 `exp.cfg.export/import/clear`|左栏导出、导入，右栏单独说明及重置，按钮顶端同排。拖入仅命中“导入配置”有效，保留原配置服务、确认、错误和重启路径。|
|主题·模式和主题色、背景不透明度|MainWindow.xaml:560–644，MainWindow.DeferredPages.cs:127 起|ThemePageView.RenderModeAndAccentCard / RenderOpacityCard，`th.mode.*`、`th.hex/chooseColor/opacity`|用户已指定三张宽选择卡，保留现有强调色选择器及单一颜色入口、无三预设小色块。|
|主题·OSD|MainWindow.xaml:644–694；MainWindow.xaml.cs:2213、2243、2443|RenderOsdCard，`th.osd.adjust/reset/volvisual/micpersist/mictrack/more/w/font/fin/fout`|调整/取消、恢复默认、音量可视化和麦克风常驻设置。更多默认关闭，展开才注册宽度、字体比例、淡入淡出滑块。恢复默认遵照冻结同时恢复位置、大小、字体和动画。|
|主题·快捷面板|MainWindow.xaml:695–748；MainWindow.xaml.cs:2278、2310、2474|RenderPanelCard，`th.panel.adjust/reset/h/peak/legacy/compactOut/fullOut`|位置采用真实面板拖放，调整中按同一按钮取消。展示位置说明、面板高度、峰值、行样式及其对应输出名开关。简洁/经典各自保留配置字段。|
|自动化·列表|MainWindow.xaml:399–457；MainWindow.AutomationLayout.cs:34–158；MainWindow.AutomationExtras.cs:31–110|AutomationPageView.RenderLeftPane，`auto.new/templates/search/stateFilter/triggerFilter/listopt`、`auto.grip./sw./run./edit./copy./delete./more.{id}`|新建、模板、过滤、列表选项；规则行依次为启用、运行、编辑、复制、删除、更多。更多仅复制执行命令（写剪贴板，反馈成功或失败）、收藏（FavoriteRuleIds）。拖动排序按既有规则 ID 持久化，运行中禁用再次运行。|
|自动化·编辑器|MainWindow.xaml:458–559；MainWindow.AutomationEditor.cs；MainWindow.AutomationTemplates.cs:92；MainWindow.MemoryLayout.cs:107|RenderEditorPane / RenderStepCard / RenderStepParams，`edit.name/trigger/hk.rec/trigapp.pick/sched.*/step.add/save/cancel`，`step.grip./act./cp./del.{i}`|触发方式控制快捷键、应用或时间；调度为每周时出现星期。规则启用位于列表。直接录入快捷键；应用为只选现有候选的下拉。日期、参数与操作按草稿/保存/取消。|
|自动化·步骤的动态参数|MainWindow.xaml.cs 的 BuildAutomationStepParameters，MainWindow.AutomationTemplates.cs:84–122|同页 `step.{i}.apppick/dev/vol/mute/lmode/li.{j}/libr.{j}/lirm.{j}/liadd/openwith.{j}/ps.{j}/psrm.{j}/psadd/trule/osdt/osdx/delay/stop`|按 AutoRuleActions 和具体步骤类型装载；保留延时实时验证、文件拖入、程序打开方式、音量和静音参数等。非应用路由不能指定手机输出或“系统默认”伪端点。已不存在的嵌套规则不能保存。|
|快捷键|MainWindow.xaml:913–932，MainWindow.xaml.cs:2548，HotkeyActions.Groups|HotkeysPageView.Render，`hk.act.{action}`|冻结版四组，按动作清单生成；麦克风切换相关项受 ExperimentalMic 控制。保留 R08 直接点击录制、键盘/鼠标冲突反馈、失焦停止录制。规则快捷键仍在规则编辑器。|

## 从 Native 多余入口反查

|R09 前 Native 入口|冻结运行时的实际归属|处理|
|---|---|---|
|设置底部 `st.cfg.export/import/opendir/reset`；页面任意 JSON 拖入即导入配置|普通设置只有语言的导入按钮接受语言文件；实验页管理配置，且只有导入按钮接受拖放|删除普通页配置卡及其命令分派；按实际命中 ID 分派。普通设置最末卡为关于。|
|杂项额外的 `st.volstep.slider`，滚轮经过滑块还能改配置|MainWindow.xaml:813–818 只有 `VolumeStepBox`|移除滑块绘制、鼠标/键盘命中与滚轮拦截，数字框和原字段保留。|
|设备过滤卡自己的编辑铅笔和双击编辑 `st.devchk.edit.*`|显示设备卡只勾选；改名属于第二张卡|整行恢复勾选，名称编辑保留在对应卡。|
|设置里的暂停托盘滚轮和两项麦克风 OSD 常驻|暂停属于托盘；麦克风 OSD 两项属于主题|移除重复绘制和命中；业务与配置字段仍有真实入口。|
|实验页的模式、麦克风、面板、OSD 九宫格/数字预览、重置全部应用路由|模式、麦克风和面板行为在普通设置；外观在主题；全应用路由重置是自动化动作|从实验页移除；该页始终只含三张基准卡。|
|主题额外的 OSD 预览按钮、九宫格与 X/Y、面板位置模式及 X/Y、所有输出音量|冻结主题页使用调整/恢复；音量列表在真实快捷面板中展开|删除多余选择器、数值框和重复开关。拖动/取消/重置继续使用同一业务。|
|自动化列表的导入规则 JSON、执行历史；行内收藏及右键 JSON 导出|列表表头仅新建和模板；历史窗口由实验页打开；收藏位于规则的更多菜单|列表恢复基准按钮及其条件。复制运行命令重用 WPF 的短命令格式，Full/Lite 共用原生写剪贴板服务。|
|规则编辑器的草稿试运行、单独启用开关、步骤上下移箭头、应用自由文本框|运行、启用归规则行；步骤通过抓手拖动；触发/目标应用使用候选下拉|移除重复入口；保存、取消、拖动排序、候选选择和所属行命令均可达。|

## 用户指定的图标式折叠导航

这是用户在 R09 追加的产品行为。展开宽度保留 196 DIP，折叠宽度 64 DIP，按钮保留 40 DIP 高度。折叠后各页保留原图标、选择态及本地化悬停提示，展开稳定时取消重复的页名悬浮提示。实验图标沿用同一解锁条件。品牌标题仍可点击切换和拖动窗口，折叠态底部增加独立展开按钮。鼠标和 Tab 配合 Enter/Space 使用同一分派，沿用 MainSidebarCollapsed 持久化。MainWindowShell.OnRender、RenderNavRail、DrawNavItem 使用同一动画宽度创建按钮、剪裁、命中及内容区；连续切换从当前宽度继续，180 ms CubicOut 动画结束停帧。未增加进程、轮询或全局钩子。

## 真实画面与输入核验

冻结 WPF 光色中文、150% DPI、1380 × 930 窗口的系统输入证据在交付目录同级的 `r09-evidence/wpf-light-settings-9dd03e4c71/`。坐标以窗口左上角为零，测试其他 DPI 时按控件标签定位。

1. 进入设置（展开栏约 x=118、y=326）。顶部依序为显示设备、设备名称和默认应用；展开第二张卡才出现别名输入。中段取包含默认应用和杂项的视口，底部取语言、更新与独立关于同框。展开语言“更多”，仅其导入按钮接受语言 JSON。禁止用完整配置文件测试正常配置导入。
2. 用隔离配置从关于的署名五击解锁；打开实验设置。同一光色 WPF 的三卡顶部分别约 y=148、333、685，勾选历史后容量、查看/清除位于第二卡。向下滚至页底应在配置管理结束。在非导入按钮位置拖一个测试配置，确认没有配置覆盖提示；将同一文件拖到实际“导入配置”按钮后取消。
3. 在主题页滚到 OSD 和面板部分，点两次调整确认按钮标签与实际拖动模式同步；再从真实浮层用 Esc、拖放和恢复默认检查交叉同步。OSD 更多仅展开时出现滑块。九语逐行宽度、浮层位置和异步失败仍须以系统输入验证。
4. 自动化列表顶部检查新建和模板。用隔离规则打开更多 → 复制命令并粘贴到测试文本框，收藏后重开；打开编辑器，确认触发/目标应用均为单一下拉。用草稿测试拖动、复制和删除步骤，最后取消。快捷键页检查靠麦克风开关决定的两个条件项。
5. 折叠后应看到 5 个常规图标；已解锁启用时为 6 个。用真实输入切换应用、快捷键、主题、设置、自动化、实验；悬停辨识页面。用 Tab 加 Enter/Space 选择并从底部展开，重新启动验证上次折叠状态。在折叠动画中点击边界，不应触发相邻页内容。

首轮同条件实图为 `r09-evidence/wpf-light-settings-1404cc4507/` 和 `r09-evidence/native-light-settings-c873fbdaf6/`。六个采样位置逐对审查。顶部设备卡与默认应用的内容、次序和边界已接近；杂项截图暴露了多余步长滑块及长说明截断；实验页产生了错误的单行历史说明和省略的历史操作按钮；页底的语言下拉、更新按钮与末卡留白有明显尺寸差异。随后按冻结样式实现开关行内容测量和完整换行、实验卡宽高与滚动条联动、无图标的紧凑检查更新按钮和内容宽度的语言选择。原生编辑器与 DirectWrite 数字框使用同样的居中对齐。

折叠轨道在 540 DIP 最小高度、含实验页时，最后一枚页按钮底边为 324 DIP，展开按钮顶边为 488 DIP，净间距 164 DIP。在 620 DIP 的普通高度，净间距为 244 DIP。这两种窗口高度都能完整显示六个页面图标与展开按钮。此项为源码几何核验。

此附录记录 R09 源码审查与冻结 WPF 实图。最终 Native 编译、截图、系统输入、业务及持久化验证按主交付报告分项记录。用户实机验收待确认。
