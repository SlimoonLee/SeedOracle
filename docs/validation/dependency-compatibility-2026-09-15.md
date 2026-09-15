# 依赖兼容性审计 · 2026-09-15

本次检查针对 RitsuLib 和 Combat Solver 的工坊更新，基线为游戏 `0.111.0`。

## 当前工坊版本

| 组件 | 当前工坊版本 | 关键证据 |
| --- | ---: | --- |
| STS2-RitsuLib | `0.6.2` | `3747602295/mod_manifest.json`；使用 `compat/0.111.0` 与 `shared` 拆分程序集 |
| Combat Solver | `0.39.0` | `3790899961/CombatSolver.json`；`CombatSolver.dll` 程序集版本 `0.39.0.0` |
| Random Foreseer | `0.13.15` | `3747531952/lib/0.13.15/RandomForeseer.dll`，本次只作为现有预测适配器的更新运行时 |

RitsuLib `0.6.x` 不再把设置、共享数据和 UI 类型全部放在主 DLL 中，而是通过类型转发拆到 `STS2-RitsuLib.Shared.dll`、`STS2-RitsuLib.Ui.dll` 和 `STS2-RitsuLib.Settings.dll`；`STS2-RitsuLib.Runtime.dll` 则随 `compat/0.111.0` 提供游戏 API 适配。Seed Oracle 的工程现在会从 RitsuLib 根目录自动选择 `compat/<RitsuLibReferenceTarget>`，并引用这四个拆分程序集。旧的单 DLL 引用会在新版本编译时出现 `CS1069` 类型转发错误。

Combat Solver `0.39.0` 的 `CombatSolver.Api.PreCombatForecastApi` 仍为 API v6。与 Seed Oracle 适配器使用的 `ForecastAsync`、`SimulateAsync`、`SimulatePlanningAsync`、worker 状态和 worker 生命周期方法签名逐项比对，没有发现变更；不需要改 `CombatSolverAdapter` 的调用代码。

## Seed Oracle 调整

- 版本提升到 `0.1.23`。
- RitsuLib 最低依赖从 `0.5.18` 提升到 `0.6.0`，因为新 RitsuLib 的拆分程序集已成为编译和运行时必需部分。
- Combat Solver 仍保持 `>=0.31.2`，没有把当前 `0.39.0` 写死；v5 是确定战损和当前状态模拟的最低能力，v6 在运行时启用规划快照模拟。
- 本地构建配置更新为当前 RitsuLib 根目录和 Random Foreseer `0.13.14` compat/lib 路径；烟测脚本能识别 RitsuLib 的 `compat` 目录。

## 验证边界

已执行新依赖编译和 API 反编译签名比对；隔离游戏烟测先在 RitsuLib `0.6.0`、Random Foreseer `0.13.14` 和 Combat Solver `0.39.0` 上通过，随后又以当前工坊的 RitsuLib `0.6.2`、Random Foreseer `0.13.15` 重新构建并复测。当前版本 planning self-test、事件审计和规划战斗模拟均通过：`native-combat-reward-groups PASS`、`event-audit SUMMARY: ok=110 degraded=11 nondeterministic=0 failed=0`、`planning-combat-simulation PASS`。可见 Steam 游戏 UI 仍需重新启动后手动观察。

上游资料：[RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)、[Combat Solver](https://github.com/Torch1230/CombatSolver)。
