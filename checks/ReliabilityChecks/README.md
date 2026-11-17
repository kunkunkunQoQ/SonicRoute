# Reliability checks

Focused checks for configuration preservation/null input, corrupt PNG placeholders, actual mouse-shortcut arbitration, bounded diagnostics, and restart handoff. Full, Lite and Legacy share these checks. Product startup, phone/ADB access and real audio writes are suppressed.

Build `ReliabilityChecks.csproj` with `-p:SonicRouteEdition=Full` or `Lite`. For Legacy also use `-p:ReliabilityFramework=net48` and an isolated `ArtifactsPath`. Run the resulting DLL with `dotnet <dll> <output>` or the net48 EXE with `<exe> <output>` on a private test desktop. The output directory must be disposable test data, never the user's real app directory.

The tests create configuration/backup/corrupt-file fixtures, install a mouse hook only on that private desktop, and launch owned helper processes. One helper deliberately terminates with an unhandled exception to verify disk logging; the old/new restart helpers exercise acknowledgement and singleton release. These are test processes, not product instances. They do not change user key bindings or send input events.

The Android library additionally provides `AndroidAudioChecks --identity-review <output>` (local TLS/SPAKE2 only) and `--display-concurrency <output>` (simulated clients), alongside the existing `--idle-retirement` regression. Identity review documents AOSP's permissive host certificate policy; it does not assert that an arbitrary server identity is authenticated or apply a pairing-certificate pin.

## 人工验收

1. 在设置中先导出一份配置，从托盘退出旧实例，再运行新测试包。配置目录中的`config.json.bak`应随实际保存保留上一份配置。
2. 给两个动作录制同一个鼠标侧键组合，只有一个动作应注册成功，其余显示冲突/未注册。清除占用者后，另一动作应能注册。
3. 手机按原方式扫码或输入配对码连接。打开应用窗口，关闭后立即重开，重复几次；应创建新的有效窗口，而不是返回已取消的窗口，音频接管逻辑保持。
4. 导入刚导出的同一份配置，验证自动重启后只有一个实例，设置保持。替代进程启动失败时，旧实例应留在运行状态并提示失败。
5. 损坏配置、显式null、坏图标及异常日志轮换由上述隔离检查验证。实际出现错误时，诊断位于配置目录的`Logs/diagnostics.log`；日志按需生成，不持续写入。
