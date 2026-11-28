# SonicRoute 构建版本

SonicRoute 2.00 的便携发行矩阵包括 Full 和 Lite。两者使用内置 .NET 10 Native AOT 运行时的单文件 EXE，并按 x64 或 ARM64 架构构建。

| 版型 | 发行文件 | 功能范围 |
| --- | --- | --- |
| `SonicRouteEdition=Full`（默认） | `SonicRoute-v<版本>-x64.exe`、`SonicRoute-v<版本>-arm64.exe` | Windows 本机音频、Android App 与 ADB 互联、AirPlay 接收 |
| `SonicRouteEdition=Lite` | `SonicRoute-v<版本>-Lite-x64.exe`、`SonicRoute-v<版本>-Lite-arm64.exe` | Windows 本机音频。编译时排除 AndroidAudio、手机资源与相关依赖 |

Full 和 Lite 的「应用」画布用实线显示 Windows 原生输出，可以添加转发并单独调整各路音量和静音。快捷面板仍可通过 Ctrl 增减播放设备、通过 Shift 追加连续范围。应用再次播放或设备重新连接时会尝试恢复已保存的多输出组合。ARM64 构建为实验版，交叉编译和 PE 检查与原生 ARM64 实机运行分别记录。

## 日常构建与正式候选

日常开发可用 [Native 产品构建入口](../Native/Product/README.md) 生成候选程序。需要带版本名的本地便携文件时，按版型和架构调用：

```powershell
./packaging/Build-Edition.ps1 -Edition Full -Architecture x64
./packaging/Build-Edition.ps1 -Edition Lite -Architecture x64
./packaging/Build-Edition.ps1 -Edition Full -Architecture arm64
```

默认输出在检出目录相邻的 `outputs/native-product/editions`。版型和架构分开存放。已建立本地安装绑定时，脚本会先校验产物，再处理目标文件替换。未绑定的运行中程序和链接目录不能覆盖。

正式候选由 `packaging/Updates/Build-PortableRelease.ps1` 在新目录中生成四个 EXE、`SonicRoute-update.json` 和 `SHA256SUMS.txt`，共六项便携发布资产。Full x64 的未签名商店候选使用 [Store 打包脚本](Store/README.md) 单独生成。普通推送只验证，发布按[操作清单](Updates/RELEASE-CHECKLIST.md)显式执行。

## Full 内嵌组件与更新边界

Full 在 EXE 中嵌入手机工具及资源。功能首次请求时，`AndroidRuntimeBundle` 按内容标识准备并校验需要的工具。缓存默认在 `%LOCALAPPDATA%\SonicRoute\PhoneRuntime\<工具标识>\`，可通过独立数据目录隔离。缓存文件在运行时按需生成，便携包只有 EXE。

Full 和 Lite 的单实例、激活、命令及更新身份各自独立，普通用户配置位置保持一致。更新清单按版型、运行时与架构选择资产，两个版型都校验、暂存及回滚单个 `SonicRoute.exe`。程序内嵌 `LICENSE`，设置页可离线查看。第三方组件保留各自许可。[授权范围](../docs/LICENSING.md) · [第三方许可索引](AndroidLab/THIRD-PARTY-NOTICES.md)

## 历史版与独立维护工具

Legacy x64 的 .NET Framework 4.8 ZIP 和曾发布的 NET8 兼容包见相应 [历史发行页](https://github.com/kunkunkunQoQ/SonicRoute/releases)。已有 Legacy 客户端只匹配同版型、运行时和架构的历史 ZIP。需复现旧版时可按对应标签构建。也可以单独运行 `dotnet build SonicRoute.Legacy/SonicRoute.Legacy.csproj -c Release` 做本地兼容性检查，正式候选只收录上表四个 EXE。
