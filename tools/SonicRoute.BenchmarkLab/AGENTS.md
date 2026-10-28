# AI 测试入口

本目录的测试规范面向 AI。修改或验证前读取 [AI_TESTING.md](AI_TESTING.md)；按改动选择功能专项、轻量或全量，不能因为存在全量入口就自动全跑。

规划性能测量时再读 [BENCHMARK_POLICY.md](BENCHMARK_POLICY.md)：预先固定轮数、预热、时长和基线；禁止测到通过为止。新增图表复用 Viewer 的数据归一化与场景筛选，不复制判定器。

测试材料必须按 [ARTIFACTS.md](ARTIFACTS.md) 隔离，结束按 [CLEANUP.md](CLEANUP.md) 清理。保留报告、日志、失败证据与最终交付；不得清理根旧 WIP、旧包、冻结输入或用户配置。

优先复用现有检查，不复制维护相同测试。规范未覆盖的测试路径先检查其 README / Program 参数及副作用。任何假体、私有桌面、实际原生调用与物理输入验证必须分别标明。
