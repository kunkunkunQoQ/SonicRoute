# 手机解码器与编码器加载检查

在仓库根目录使用对应.NET SDK构建，所有夹具输出请放在独立测试目录：

```powershell
dotnet build checks/AndroidAudioChecks/AndroidAudioChecks.csproj -c Release -p:SonicRouteEdition=Full
dotnet run --no-build --project checks/AndroidAudioChecks/AndroidAudioChecks.csproj -c Release -p:SonicRouteEdition=Full -- --decoder-settings ../outputs/manual-decoder-checks
dotnet build checks/AndroidUiChecks/AndroidUiChecks.csproj -c Release -p:SonicRouteEdition=Full
dotnet run --no-build --project checks/AndroidUiChecks/AndroidUiChecks.csproj -c Release -p:SonicRouteEdition=Full -- ../outputs/manual-decoder-ui --panel-stream-settings --file-log
```

库层覆盖旧配置兼容、按物理手机保存、自动/显式Concentus输出一致、声音会话应用选择、编码器缓存与手动失效及取消竞态。合成Opus流进入真正解码器，合成H.264流进入Windows Media Foundation的自动、D3D11优先及软件模式；显式软件模式不绑定渲染设备。不会连接手机或打开真实音频输出。

UI使用隔离配置和注入的编码器查询；检查进入对应设备页才读取、后台准备、不重复查询、加载提示不可选、隐藏取消、迟到结果隔离、九语原名和两行浅/深主题。夹具创建自己的WPF窗口，不启动正常产品；本机交付验证另使用既有私有桌面启动器，未切换用户桌面。

声音的自动模式使用 Concentus。画面可选自动、Windows H.264 加速或软件解码，以及系统支持的 H.265。打开 Android 设置页可先保存 H.265 偏好，Windows 解码器缺失或已确认手机无法编码时，该选项置灰并说明原因。两种格式各自保留编码器，HEVC 选项标出 CQ 和 HDR 型号并保留原名。手机无法启动 HEVC 编码时在同一次连接使用 H.264，保存的偏好保留到下次使用。偏好在重新开启对应流时生效。

`--android-hevc-start <目录>` 接收该目录中 `h265-android-video.bin` 与 `h264-android-video.bin` 的模拟协议数据。两个文件是 64 字节设备名、视频首部和若干 `ScrcpyVideoPacketHeader` 加 Annex B 帧。夹具从独立应用窗口的正常入口打开，并检查实际 Windows 出帧、同一次连接的格式协商、资源回收和非法保存尺寸保护。交付样本在父容器 `outputs/android-hevc-reconnect-20261118/video-start`，设备端 JAR 测试范围见 74 号记录。

`--android-hevc-recovery <目录>` 使用以下命令生成模拟视频。需要与 AirPlay 测试相同的测试用 PyAV 环境，以及支持 HEVC 的 Windows。视频在一条连接内切换两次分辨率，混入编码切换期间的非独立帧，并检查 Windows 最终显示尺寸。

```powershell
python checks/AndroidAudioChecks/android_hevc_resize_fixture.py ../outputs/manual-hevc-recovery
dotnet run --no-build --project checks/AndroidAudioChecks/AndroidAudioChecks.csproj -c Release -p:SonicRouteEdition=Full -- --android-hevc-recovery ../outputs/manual-hevc-recovery
```

`--gamepad-window <目录>` 用原生窗口和模拟 XInput 检查同一手柄切换窗口时的路由、最小化及释放。实机时可打开两个安卓应用窗口，按住方向切到另一窗口，然后切到 Windows 应用，确认旧窗口释放手柄。

手工验收：进入已连接手机的设备页，确认行标题只显示“声音”“画面”，两行均有解码器且第一项/默认是自动；展开与悬浮保留翻译及原名。选择软件、加速或自动后关闭重开画面，选择Concentus后关闭重开声音，检查显示、操作及声音输出。加载期间切换页面再返回，确认列表与当前手机相符。首次真实手机能力查询仍需等待手机响应，已取得的结果会复用；设备页手动刷新使缓存失效。夹具不证明真机兼容、听音质量或整版性能。
