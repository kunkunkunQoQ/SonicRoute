# 双进程架构废案

这个双进程（UI 与后台拆分）方案我最终决定不做了（2026-09-13）。

原因：启动变慢、内存偏高、复杂度上升，收益没达到预期。整体回退单进程（v1.15 正式版形态），只保留了并行枚举、图标懒加载、设备短缓存等通用优化。

本目录是我双进程开发期间的全部代码快照（git tag `v1.15-双进程-final`），留作参考，未来不排除重新启用。

- 双进程文件：`SonicRoute\` 下的 `BackendHost.cs` / `IHostServices.cs` / `IpcClient.cs` / `IpcProtocol.cs` / `IpcServer.cs`
- 核心思路：`--service` 后台进程（托盘/快捷键/OSD/音频/麦克风）+ `--ui` 界面进程，Named Pipe 双管道通信
- 两条血泪教训：① 管道 IO 必须 `ConfigureAwait(false)`，否则 UI 同步等待会死锁；② 事件连接必须长驻读取循环，否则广播全丢

想恢复代码：

```bash
git checkout v1.15-双进程-final -- SonicRoute SonicRoute.Core
```

恢复后把 5 个双进程文件加回 csproj，并还原 `App.xaml.cs` 的启动分流。
