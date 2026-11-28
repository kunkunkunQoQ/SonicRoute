# Microsoft Store 包

此目录保存公开的商店清单及打包脚本，不包含签名材料。应用标识、发布者标识、应用 ID 和开机启动任务 ID 沿用原商店包。

开发版本由 [build/Version.props](../../build/Version.props) 的 `SonicRouteVersion` 提供。清单模板的 `0.0.0.0` 在构建或打包时替换。脚本默认使用 [AppxManifest.xml](AppxManifest.xml) 构建 Full 商店包，也支持用 `-Edition Lite` 和 [AppxManifest.Lite.xml](AppxManifest.Lite.xml) 构建独立 Lite 包。两者默认读取同版型的 .NET 10 Native AOT x64 单文件 EXE。已有 Partner Center 条目可用 `-IdentityName`、`-Publisher`、`-DisplayName` 指定包身份。在仓库根目录运行以下命令。

```powershell
# 默认 Full 商店包
& ./Native/Build-Product.ps1 -Edition Full -Architecture x64
& ./packaging/Store/Build-Package.ps1

# 独立 Lite 商店包
& ./Native/Build-Product.ps1 -Edition Lite -Architecture x64
& ./packaging/Store/Build-Package.ps1 -Edition Lite
```

需要 .NET SDK 和 Windows SDK 中的 `makepri.exe`、`makeappx.exe`。脚本默认选择已安装的最新 x64 打包工具。可用 `-Edition`（`Full`/`Lite`，默认 `Full`）、`-PublishDirectory`、`-OutputDirectory`、`-PackageVersion`、`-SdkDirectory`、`-ManifestPath`、`-IdentityName`、`-Publisher`、`-DisplayName` 指定参数；包版本最后一段为 `0`。输出目录必须尚不存在，防止覆盖历史包。

商店包只接受对应版型（`Full` 或 `Lite`）的 NET10 x64 单文件输入。自 GPL 转换起，应用内嵌完整 GPL 第 3 版文本，可在设置页点击“GPL-3.0”离线查看；MSIX 还附带根目录的 `LICENSE` 文件。若还要构建其他便携架构，按顺序执行发布命令，避免共享生成目录的文件占用；其他架构不作为商店包输入。

脚本从 `SonicRoute/SonicRoute.png` 生成透明资产，包含 14 种目标尺寸，以及每种尺寸的普通、`unplated`、`lightunplated` 变体。清单使用透明背景，并通过 MakePri 重新生成 `resources.pri`，避免 Windows 为缺少主题变体的图标添加底板。修改图标后必须重新生成资产与资源索引。参考 [Microsoft 应用图标构造规范](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction) 和 [桌面应用 MSIX 转换说明](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)。

产物是 **unsigned MSIX**，另附资产、资源索引、索引转储与打包日志。脚本不签名、不安装、不提交商店。正式分发时应沿用既有商店发布流程；已安装用户需收到更新后才会使用新资产。浅色背景保留原色、深色背景使用相同轮廓的白色版本，两个版本均保留透明度。

GitHub Actions 的 **Build and Release** 复用便携候选的 Full x64 Native AOT EXE 生成未签名 MSIX，并以独立 Actions 产物保留。2.00 的 GitHub 便携发布资产是四个 EXE、更新清单及校验文件。

默认输入为 `../outputs/native-product/candidate/<版型>-x64`，默认包输出为 `../outputs/native-product/store/v<版本>`，Lite 目录加 `-Lite`。已有经验证的发布目录可直接传给 `-PublishDirectory`。Native AOT 运行时随 EXE 构建，手机工具内嵌在 Full EXE 中。Store 包按包身份使用商店更新和自启任务。

打包成功、身份匹配和未签名包被本机拒绝安装，都不等于商店安装/升级验收通过。签名包安装/升级、包身份下的音频/自启/Store更新及手机工具启动需单独验收；未验证的场景记 NOT TESTED。GitHub 便携发布资产不包含 MSIX，改默认构建不代表已提交商店。

更换应用图标时，在仓库根目录运行以下命令，再重新发布并打包。生成的应用 PNG 为 256×256，ICO 包含 16–256 像素的九种尺寸；Full 和 Lite 的窗口和托盘共用这组资源。转换保持原图比例与透明背景。

```powershell
& ./packaging/Generate-App-Icon.ps1 -SourceImage '新图标.png'
```
