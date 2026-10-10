# Microsoft Store 包

此目录保存公开的商店清单及打包脚本，不包含签名材料。应用标识、发布者标识、应用 ID 和开机启动任务 ID 沿用原商店包。

先发布 Lite x64 框架依赖单文件，再在仓库根目录运行：

```powershell
dotnet publish SonicRoute/SonicRoute.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/SonicRoute-v1.22-Lite-x64-event-only
& ./packaging/Store/Build-Package.ps1
```

需要 .NET SDK 和 Windows SDK 中的 `makepri.exe`、`makeappx.exe`。脚本默认选择已安装的最新 x64 打包工具。可用 `-PublishDirectory`、`-OutputDirectory`、`-PackageVersion`、`-SdkDirectory` 指定输入、输出、四段包版本和工具目录；包版本最后一段为 `0`。输出目录必须尚不存在，防止覆盖历史包。

脚本从 `SonicRoute/SonicRoute.png` 生成透明资产，包含 14 种目标尺寸，以及每种尺寸的普通、`unplated`、`lightunplated` 变体。清单使用透明背景，并通过 MakePri 重新生成 `resources.pri`，避免 Windows 为缺少主题变体的图标添加底板。修改图标后必须重新生成资产与资源索引。参考 [Microsoft 应用图标构造规范](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction) 和 [桌面应用 MSIX 转换说明](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)。

产物是 **unsigned MSIX**，另附资产、资源索引、索引转储与打包日志。脚本不签名、不安装、不提交商店。正式分发时应沿用既有商店发布流程；已安装用户需收到更新后才会使用新资产。浅色背景保留原色、深色背景使用相同轮廓的白色版本，两个版本均保留透明度。

更换应用图标时，在仓库根目录运行以下命令，再重新发布并打包。生成的应用 PNG 为 256×256，ICO 包含 16–256 像素的九种尺寸；Lite、Legacy、窗口和托盘共用这组资源。转换保持原图比例与透明背景。

```powershell
& ./packaging/Generate-App-Icon.ps1 -SourceImage '新图标.png'
```
