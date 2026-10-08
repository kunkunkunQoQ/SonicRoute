# 设置 UI 清理记录

2026-10-08，工作区 1.21。

## 已清理

- `QuickPanelModernWindow.xaml` 中的 `SystemDevItemTemplate`：生产 `.cs/.xaml` 全树仅有声明，无 StaticResource、DynamicResource、FindResource、SetResourceReference 或拼接键引用。删除模板及重复注释，实际系统设备下拉保持原实现。
- `MainWindow.xaml.cs` 旧的输出/输入设备勾选框重复构造与名称编辑行：统一为 `MainWindow.SettingsLayout.cs` 的设备标签、勾选卡片和名称编辑行。每个 helper 仅保留一个定义。
- 主题与设置页七个“更多选项”按钮的重复本地字体、颜色和背景定义：统一为 `SettingsControls.xaml` 的 `MoreOptionsToggle`，避免系统 ToggleButton 的选中底色；展开/收起事件不变。

## 保留与核对

- 与 `v1.21-06-启动模式对齐与禁用图标反色` 快照对比，设置页保留相同的 50 个命名控件、46 处事件挂接；34 个唯一处理器仍有定义。
- `BuildDeviceFilter`、`BuildDeviceNameLists`、全选、设备过滤、名称延迟保存等方法继续参与现有流程。
- 设置开关整行处理点击；内部 `AutomationSwitch` 仅以 OneWay 绑定显示状态，禁用命中与焦点，不挂接保存事件。
- 其余生产共享样式仍有引用，未删除，也未进行全局资源迁移。
- 以上资源与事件审计由子代理只读完成；实际运行验收结果见本轮测试报告。
