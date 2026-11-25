# 原生图形探针

默认运行或传入 `--self-test`，探针在自己的进程中创建临时桌面和 UI 线程，先关联桌面再初始化 OLE STA；从不切换用户的输入桌面。UIA 客户端在同一进程的独立 MTA 线程读取实际控件。自检结束后关闭桌面及进程。显式传入 `--interactive` 会在当前桌面显示可以输入和调节的窗口。

## 原生边界

`../Graphics/DirectDrawing.cs` 使用 Windows SDK 的固定 IUnknown、ID2D1Factory、ID2D1RenderTarget、IDWriteFactory ABI。窗口开启时建立绘制工厂；首次绘制建立目标和两支画刷，调整大小时复用，收到 `D2DERR_RECREATE_TARGET` 后释放该目标域，下次 WM_PAINT 再建。普通窗口使用 HWND render target；OSD 使用绑定顶向下 BGRA DIB 的 DC render target，按需把预乘 alpha 交给 UpdateLayeredWindow。应用窗口、两个公用控件和透明 OSD 分别由同一个 UI 线程销毁。

`ProbeWindow.cs` 处理绘制、消息、DPI、原生 EDIT/Trackbar、滑块音量消息和不激活 OSD。圆角、三根音阶图标、中文标题均由 D2D/DirectWrite 绘制。`AutomationCheck.cs` 通过系统 CUIAutomation 查到真实 HWND 并获取 Value/RangeValue。测试输出区分真实显示器、注入的 DPI 消息、真实 IME 消息经 IMM 调用的来源，以及手动才可确认的候选选择。

## 复跑

工作目录是这份隔离源码树的根。按 `Native/Set-Phase1Toolchain.ps1` 配置各目标架构的 VS、SDK 和 CRT；以已安装的 .NET 10 SDK 在各自 shell 中执行。发布 SDK 版本和运行包版本以本阶段共用 targets 与日志为准。

```powershell
. ./Native/Set-Phase1Toolchain.ps1 x64 -VsRoot 'C:/Program Files/Microsoft Visual Studio/2022/Community'
$stage = [IO.Path]::GetFullPath('../stage/graphics')
& "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" publish Native/GraphicsProbe/GraphicsProbe.csproj -c Release -r win-x64 -p:IlcUseEnvironmentalTools=true "-p:ArtifactsPath=$stage/obj-x64" -o "$stage/win-x64"
& "$stage/win-x64/SonicRoute.GraphicsProbe.exe" --self-test
```

ARM64 构建中把三处 `x64` 的架构参数、输出目录以及中间目录换成 `arm64`，保留 VS 安装路径。在真实 ARM64 主机运行该 EXE 可重复同一组自检。手动验收用 `--interactive`，选择微软拼音并用键盘候选窗选择文字，用 Tab 转至滑块、方向键和鼠标调整；拖放到不同真实 DPI 的显示器，确认子控件缩放与 OSD 焦点。探针中的强制重建模拟 EndDraw 错误路径，无需重置系统显卡。私有桌面的 GDI/USER 计数包含系统输入法等组件，自有 HWND 与 D2D 资源单独检查。
