# Microsoft Store 包

此目录保存公开的商店清单及打包脚本，不包含签名材料。应用标识、发布者标识、应用 ID 和开机启动任务 ID 沿用原商店包。

开发版本唯一维护于 [build/Version.props](../../build/Version.props) 的 `SonicRouteVersion`；工程、Legacy 生成清单与商店打包默认版本均由它派生。清单模板中的 `0.0.0.0` 在构建或打包时替换，无需手动更新。公开 README 和发布记录按正式发布状态另行维护。**脚本默认构建 Full 商店包**（使用 [AppxManifest.xml](AppxManifest.xml)，包含手机面板与手机音频功能，输出 `SonicRoute-v<Version>-x64-unsigned.msix`），同时也支持通过 **`-Edition Lite`** 为独立上架的微软商店 Lite 版本构建纯电脑音频商店包（使用 [AppxManifest.Lite.xml](AppxManifest.Lite.xml)，输出 `SonicRoute-v<Version>-Lite-x64-unsigned.msix`）；两者均复用 NET10 x64 框架依赖单文件 EXE，拒绝 NET8、ARM64 及版本/版型不匹配输入。如在 Partner Center 新建了独立的 Lite 条目，可通过 `-IdentityName`、`-Publisher`、`-DisplayName` 覆盖包身份。在仓库根目录运行：

```powershell
# 默认 Full 商店包
dotnet publish SonicRoute/SonicRoute.csproj -c Release -f net10.0-windows -r win-x64 -p:SonicRouteEdition=Full -p:PublishProfile=win-x64 -o ./build-artifacts/store-publish
& ./packaging/Store/Build-Package.ps1 -PublishDirectory ./build-artifacts/store-publish

# 独立 Lite 商店包
dotnet publish SonicRoute/SonicRoute.csproj -c Release -f net10.0-windows -r win-x64 -p:SonicRouteEdition=Lite -p:PublishProfile=win-x64 -o ./build-artifacts/store-publish-lite
& ./packaging/Store/Build-Package.ps1 -Edition Lite -PublishDirectory ./build-artifacts/store-publish-lite
```

需要 .NET SDK 和 Windows SDK 中的 `makepri.exe`、`makeappx.exe`。脚本默认选择已安装的最新 x64 打包工具。可用 `-Edition`（`Full`/`Lite`，默认 `Full`）、`-PublishDirectory`、`-OutputDirectory`、`-PackageVersion`、`-SdkDirectory`、`-ManifestPath`、`-IdentityName`、`-Publisher`、`-DisplayName` 指定参数；包版本最后一段为 `0`。输出目录必须尚不存在，防止覆盖历史包。

商店包只接受对应版型（`Full` 或 `Lite`）的 NET10 x64 单文件输入。若还要构建其他便携架构，按顺序执行发布命令，避免共享生成目录的文件占用；其他架构不作为商店包输入。

脚本从 `SonicRoute/SonicRoute.png` 生成透明资产，包含 14 种目标尺寸，以及每种尺寸的普通、`unplated`、`lightunplated` 变体。清单使用透明背景，并通过 MakePri 重新生成 `resources.pri`，避免 Windows 为缺少主题变体的图标添加底板。修改图标后必须重新生成资产与资源索引。参考 [Microsoft 应用图标构造规范](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction) 和 [桌面应用 MSIX 转换说明](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)。

产物是 **unsigned MSIX**，另附资产、资源索引、索引转储与打包日志。脚本不签名、不安装、不提交商店。正式分发时应沿用既有商店发布流程；已安装用户需收到更新后才会使用新资产。浅色背景保留原色、深色背景使用相同轮廓的白色版本，两个版本均保留透明度。

已有经验证的 NET10 Full x64 发布目录时，直接把该目录传给 `-PublishDirectory` 即可，无需重复发布；默认输入为 `dist/publish/Full/win-x64`。运行需要机器已安装 x64 .NET 10 Desktop Runtime；MSIX 不内置该运行时。手机工具内嵌在 Full EXE 中，首次使用时才按需提取。Store 包仍按包身份使用商店更新机制，已有身份与自启任务不变。

打包成功、身份匹配和未签名包被本机拒绝安装，都不等于商店安装/升级验收通过。签名包安装/升级、包身份下的音频/自启/Store更新及手机工具启动需单独验收；未验证的场景记 NOT TESTED。GitHub 便携发布资产不包含 MSIX，改默认构建不代表已提交商店。

更换应用图标时，在仓库根目录运行以下命令，再重新发布并打包。生成的应用 PNG 为 256×256，ICO 包含 16–256 像素的九种尺寸；Lite、Legacy、窗口和托盘共用这组资源。转换保持原图比例与透明背景。

```powershell
& ./packaging/Generate-App-Icon.ps1 -SourceImage '新图标.png'
```
