# Android Lab 构建与检查

源码仓库可以使用任意目录名（本地当前为“当前项目”）。脚本从自身位置定位仓库；仓库的父目录是工作区容器，其下 `outputs` 保存产物与检查记录，`android-audio-test` 提供已有只读 SDK/原生构建参考。容器不必是 Git 仓库，不再依赖旧研究目录或名为 SonicRoute 的源码目录。不要重跑初始化脚本覆盖已有树。当前仅维护 .NET 10，停止 .NET 8 更新和构建检查，旧验收记录保留。

默认构建为带手机功能、名称无 Lite 后缀的 SonicRoute；仅电脑版须显式 SonicRouteEdition=Lite，实际排除手机功能。普通发行包入口见 [EDITIONS.md](../EDITIONS.md)。本页 Build-Lab 固定选择 Full，并只由显式 --android-lab 启用实验数据和启动行为。

- Build-Catalog.ps1：编译Catalog及MediaSessionBridge，使用容器内现有只读SDK输入，新目录构建后生成JAR；可用 `-ReferenceDirectory` / `-JdkBinDirectory` 指定输入和JDK。SDK哈希记录在当次输出目录，不要求第二份旧路径副本。
- Build-VideoClient.ps1：新输出目录叠加参考四补丁与flex_resize、独立VD尺寸协议、OwnedDisplayPolicy，构建scrcpy并运行8个隐藏真实HWND自由边缘案例及最终尺寸/回收检查，输出video-client。OwnedDisplayPolicy只在支持该系统API/feature flag的Android16+可信虚拟屏请求忽略应用方向/宽高比/不可调整尺寸限制，不修改全局配置；不支持则保留标准创建路径。生产启动使用display-ime-policy=hide，手机软键盘不在虚拟屏/手机弹出，电脑SDK键盘输入保留。
- 用户撤回独立桌面，整机入口恢复`--display-id=0`、Flex=0/Mirror=1：直接显示/控制手机主屏，整机声音仍用原接管源。八边等比调整、按工作区等比最大化、旋转时更新比例，标题栏只显示手机名；不发送虚拟屏尺寸包、不新增定时器。普通应用独立屏幕继续自由调整。原生构建包含48项隐藏HWND镜像断言；只改客户端时可用`Build-VideoClient.ps1 -ClientOnly`复用仓库中固定Hash视频server，跳过无关Android端重建。
- 46/47号独立桌面、应用选择层、桌面任务事件及专用错误/操作文案已撤回，不安装其他launcher。整机保留手机图标资源、已验证的GPU D3D11和首帧就绪；忽略旧桌面分辨率，不对主屏传只允许副屏使用的IME策略，不等待桌面事件。视频server恢复已校验的D92B5E固定版本，音频server和helper不变，普通应用原包监听保留。`Build-PhoneIcon.ps1`仍从共享Phone Geometry生成PNG/ICO，PE程序图标保持手机轮廓。详见父容器48号记录。
- 虚拟屏尺寸以手机主屏长短边为预算，窗口仅提供比例；复用创建时的屏幕信息，不在拖动时查询手机。私有、非presentation的自有屏幕在Android16+禁用抢夺top focus。Windows原生IME候选/组合UI开启，组合按键不重复转发；已提交Unicode经设备显式粘贴发送到目标屏，不读写电脑剪贴板，自动同步仍关闭。此Unicode路径会更新手机剪贴板，并要求应用输入框支持粘贴。原生构建附带手机预算/对齐以及IME按键配对检查；本次实机和交互验证见Lab/integration-docs/19-PHONE-WINDOW-INPUT-ISOLATION.md。
- Build-VideoRuntime.ps1：新输出目录内下载并校验固定FFmpeg8.1/zlib1.3.2/NASM源，最小构建四DLL到video-runtime；不修改全局缓存。下载/configure/哈希有收据。
- Test-VideoRuntime.ps1：用新库解码真实H264/HEVC样例和App图标，验证精确视频客户端六文件装载，不访问手机。
- Build-Lab.ps1 -Publish：NET10 x64框架依赖包，默认固定写入SonicRoute-Android-Perf-x64/.pending；记录构建输入并确认构建期间不变。
- Package-Lab.ps1：校验固定server/视频清单，复制启动脚本、说明和实际许可证，输出包SHA256清单，不计LabData。
- Test-Backend.ps1 -Mode modules/package/catalog-retained/connection-state：定向模块或包检查；包检查默认检查Perf目录中的.pending，支持 -OutputName 与 -Direct。connection-state只确认现有已连接传输，命令白名单拒绝实际配对、connect/disconnect及音视频控制，不替代新二维码扫码验收。catalog/media是只读设备检查；live会实际捕获，不作为默认检查。
- Test-Performance.ps1 -Label <唯一标签>：Release组件基准，结果保留每轮耗时/分配/校验值；-BaselineAppDirectory 可使用Lab内保留的旧程序集，复用同一harness。列表视口固定150px以排除分类/底部高度差异；无手机访问、声音或可见窗口，不代表整机闲时或长期占用。
- Test-Ui.ps1：默认离线WPF界面/规则检查；额外LiveDrop/LiveMulti/Lifecycle参数须按当前授权范围使用。
- Verify-Compatibility.ps1：保留按需Legacy编译及原Windows应用行检查；不再包含NET8，本轮连接修复无需整套兼容检查。

本机.NET10 SDK位于用户目录，JDK21为BellSoft；native工具读用户AndroidAudioTest缓存，并用临时ASCII盘符映射到已验证的Lab输出，结束只移除映射、保留物理日志。工具路径和输入版本需在迁移机器时重核。

后续版本复用现有 SonicRoute-Android-Perf-x64，构建和打包写入其固定.pending子目录；原启动入口在用户退出旧托盘实例后应用更新，保留LabData和未知用户文件。源文件和包哈希见上层交付收据；性能原始样本及完整边界见12-PERFORMANCE.md。

--android-lab 仍使用独立数据/实例并默认打开快速面板；当前启用正常自动化、快捷键、托盘/滚轮与前台监听。它跳过自动更新、自启动修复、全局短命令登记，宿主退出先停规则再清理手机来源。正式启动/控件/声音的实际试用边界见上层integration-docs。

catalog-retained仅查询波点/哔哩两个包；无音频或屏幕控制。QRCoder按固定1.7.0 NuGet引用，打包附QRCoder-LICENSE.txt。原比例锁定已由自由边缘调整与事件尺寸请求取代。

-Direct只用于明确直出/历史包检查。Test-Updater.ps1用隔离小文件验证Windows PowerShell应用更新、路径与哈希拒绝、配置保留，不启动/结束应用。原有历史目录本次不删除，不再逐版本新建完整目录。
