# 双进程架构废案（SonicRoute 双实例方案归档）

> **状态：已废弃（2026-09-13）** — 用户决定「不做双实例了」，SonicRoute 已整体回退单进程架构（v1.15 正式版形态 + 保留并行枚举/图标懒加载/设备短缓存等通用优化）。

## 本目录内容

完整双进程源码快照（对应 git tag `v1.15-双进程-final`，commit `010e724`）：

- `SonicRoute/` — UI 工程（含双进程文件：`BackendHost.cs` / `IHostServices.cs` / `IpcClient.cs` / `IpcProtocol.cs` / `IpcServer.cs`）
- `SonicRoute.Core/` — 核心库（含双进程期 ConfigService IPC override）
- `.github/`、`README.md`、`LICENSE`、`SonicRoute.sln` 等仓库配套文件

## 双进程架构概览（仅作技术参考）

- **阶段 0**：`IHostServices` 接口化（UI 三窗口 37 处 `((App)Application.Current)` 调用改经接口）
- **阶段 1**：`BackendHost` 后台宿主化（托盘/快捷键/OSD/麦克风/当前应用检测/idle 回收进 Backend，App 变轻量入口）
- **阶段 2**：Named Pipe 双管道 IPC（Req=`SonicRoute.Ipc.Req` 请求/响应 + Evt=`SonicRoute.Ipc.Evt` 事件广播；4 字节小端长度前缀 + UTF-8 JSON）
- **阶段 3**：稳定化/清理（生命周期、重连、单实例整理）
- 启动参数：`--service`（Backend）/ `--ui` / `--panel` / `--main` / `--restart`
- 关键教训：① 库级 IO async 必须 `ConfigureAwait(false)`（否则 UI 线程同步等待管道 → 永久死锁）；② 事件连接必须长驻读取循环（否则广播全丢 + ExistsUi 残留）

## 废弃原因

双进程架构完成且可运行，但：

- UI 启动速度明显比单进程旧版慢（WPF 主窗口构造 ~470ms + 冷设备枚举 ~600ms）
- 打开 UI 后总内存明显偏高（UI ~160MB / Backend ~130MB，双进程共 ~290MB，高于单进程）
- 架构复杂度显著上升，收益（关闭 UI 后后台继续运行）未达到用户预期

## 如何恢复双进程代码（如未来需要）

```bash
git fetch origin v1.15-双进程-final
git checkout v1.15-双进程-final -- SonicRoute SonicRoute.Core
# 或：git worktree add <path> v1.15-双进程-final
```

注意：恢复后需将双进程 5 个文件加回 `SonicRoute.csproj` 的 `<Compile Include>`，并将 `App.xaml.cs` 的启动分流逻辑一并恢复（或直接 checkout 全部 SonicRoute 文件）。
