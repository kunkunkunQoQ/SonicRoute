# Microsoft Store 包

此目录保存公开的商店清单及打包脚本，不包含签名材料。应用标识、发布者标识、应用 ID 和开机启动任务 ID 沿用原商店包。

开发版本唯一维护于 [build/Version.props](../../build/Version.props) 的 `SonicRouteVersion`；工程、Legacy 生成清单与商店打包默认版本均由它派生。清单模板中的 `0.0.0.0` 在构建或打包时替换，无需手动更新。公开 README 和发布记录按正式发布状态另行维护。Store 与 NET10 Lite x64 使用相同的框架依赖单文件 EXE，不另外生成自包含版本。打包前复用便携版的只读 PE、版本、描述符及内嵌运行时验证，拒绝 NET8、ARM64 及版本不匹配输入。在仓库根目录运行：

```powershell
dotnet publish SonicRoute/SonicRoute.csproj -c Release -f net10.0-windows -r win-x64 -p:PublishProfile=win-x64 -o ./build-artifacts/store-publish
& ./packaging/Store/Build-Package.ps1 -PublishDirectory ./build-artifacts/store-publish
```

需要 .NET SDK 和 Windows SDK 中的 `makepri.exe`、`makeappx.exe`。脚本默认选择已安装的最新 x64 打包工具。可用 `-PublishDirectory`、`-OutputDirectory`、`-PackageVersion`、`-SdkDirectory` 指定输入、输出、四段包版本和工具目录；包版本最后一段为 `0`。输出目录必须尚不存在，防止覆盖历史包。

x64与ARM64发布命令按顺序执行。StoreInterop使用共享生成与输出目录，并发发布可能造成输出文件占用；本轮串行发布验证通过。

脚本从 `SonicRoute/SonicRoute.png` 生成透明资产，包含 14 种目标尺寸，以及每种尺寸的普通、`unplated`、`lightunplated` 变体。清单使用透明背景，并通过 MakePri 重新生成 `resources.pri`，避免 Windows 为缺少主题变体的图标添加底板。修改图标后必须重新生成资产与资源索引。参考 [Microsoft 应用图标构造规范](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction) 和 [桌面应用 MSIX 转换说明](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)。

产物是 **unsigned MSIX**，另附资产、资源索引、索引转储与打包日志。脚本不签名、不安装、不提交商店。正式分发时应沿用既有商店发布流程；已安装用户需收到更新后才会使用新资产。浅色背景保留原色、深色背景使用相同轮廓的白色版本，两个版本均保留透明度。

已有经验证的 NET10 Lite x64 发布目录时，直接把该目录传给 `-PublishDirectory` 即可，无需重复发布。运行需要机器已安装 x64 .NET 10 Desktop Runtime；MSIX 不内置该运行时。这是与 NET10 Lite 一致的部署方式，运行时需求应在商店说明中告知用户。Store 包仍按包身份使用商店更新机制。

打包成功、身份匹配和未签名包被本机拒绝安装，都不等于商店安装/升级验收通过。签名测试包的安装/1.24升级、包身份下的音频/自启/Store更新需单独验收；未验证的场景记 NOT TESTED。七项 GitHub 发布资产不包含 MSIX。

更换应用图标时，在仓库根目录运行以下命令，再重新发布并打包。生成的应用 PNG 为 256×256，ICO 包含 16–256 像素的九种尺寸；Lite、Legacy、窗口和托盘共用这组资源。转换保持原图比例与透明背景。

```powershell
& ./packaging/Generate-App-Icon.ps1 -SourceImage '新图标.png'
```
