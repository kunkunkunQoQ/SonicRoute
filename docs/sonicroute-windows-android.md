# SonicRoute 2.00 Full 与 Android App 互联

Windows Full 的「设备 → 连接设备 → Android」提供附近手机配对和无线调试。打开手机「电脑」页的配对入口，在 Windows 选择附近手机，确认两端的八位数字一致。配对后，这部手机可直接收听电脑声音，手机和电脑都可以停止播放。以后回到同一网络会尝试自动连接。

Windows 按当前连接显示设备工具。通过 SonicRoute App 连接时，快捷面板按实际播放会话显示应用图标，手机授权的系统投屏可在电脑上只读查看。如需操作独立应用虚拟屏、传输文件或同步剪贴板，请在设备页选择已授权的无线调试或 USB 调试（ADB）。该连接也提供应用固定、手机智能音量及音画编码设置。

手机的「声音」页管理本机应用与输出。「来源应用」可以按名称或包名搜索并显示安装图标。「电脑」页可发起投屏、在电脑播放手机声音、在手机播放电脑声音；只有一台在线电脑时直接使用它。Windows 的手机设备行也能发起这些操作。系统投影授权、音频范围和来源应用捕获策略按实际设备执行。

- [Android 应用源码与构建入口](https://github.com/kunkunkunQoQ/SonicRoute-App)
- [已验功能、系统限制及后续工作](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/docs/verified-handoff-20261128.md)
- [SRAP 协议](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/docs/protocol.md)
- [Windows Full 和 Lite 构建脚本](../Native/Build-Product.ps1)
