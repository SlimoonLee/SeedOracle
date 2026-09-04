# 建议标题

`[RFC] 为伴生 Mod 提供隔离的战前预测 API`

# PR 说明

## 背景

地图信息类伴生 Mod 可以在玩家进入房间前确定部分 Encounter，但 Combat Solver 当前公开能力从已经存在的 `CombatState` 开始。若伴生 Mod 在当前游戏进程中直接调用战斗初始化，会推进跑局/战斗 RNG、触发全局 Hook，并修改当前求解器会话；在外部只复刻一部分初始化流程，又容易遗漏开战遗物、Mod Hook、怪物 HP/意图、抽牌和其他语义。

这个 Draft PR 提议增加一个公开、显式隔离的战前预测边界，让伴生 Mod 能复用 Combat Solver 的现有模拟与搜索实现，同时不修改玩家当前跑局。

## 提议的接口

新增 `CombatSolver.Api.PreCombatForecastApi`：

- `ForecastAsync`：输入当前单人 `RunState`、已确定的 `EncounterModel`、目标楼层/地图列、房间和地图节点类型，以及可选的连续前置非战斗节点与入战 HP；
- 返回预计整场战损、终局 HP、计划用药、结束回合、搜索边界、可信度、耗时和诊断日志；
- `CaptureLiveStateToken`：供调用端缓存并判断跑局是否变化；
- worker 状态与生命周期：查询 PID/忙闲/内存/静音状态，停止、重启/预热和配置空闲期限；
- `SimulateAsync`：对当前幕原生 Encounter 使用调用方提供的样本种子，显式生成不代表确定未来的假设战斗结果。

## 隔离与安全边界

1. 主线程只捕获并规范化完整 `SerializableRun`，记录当前房间身份、精确 Mod 集合和 SHA-256 状态令牌。
2. 战斗初始化和搜索在 Combat Solver 独占的 Windows headless 游戏进程中执行；worker 使用独立用户目录、关闭 Steam，并把主音量、BGM、音效和环境音设为零。
3. worker 加载与主进程一致的 Mod 集合，恢复跑局后重新规范化序列化，必须与输入快照完全一致才继续。
4. 只有通过快照核对后，才补记已确定的前置非战斗节点、应用可选入战 HP，并从目标地图坐标进入 Encounter。
5. 结果返回前再次核对 live 跑局引用、无活动战斗及完整状态令牌；任何变化都返回 `LiveStateChanged`。
6. 未决 Event、中间战斗、多人跑局和非 Windows 平台当前显式拒绝。worker 内关闭该 API，避免伴生 Mod 递归启动子进程。

## 对现有代码的影响

生产源码改动集中在新增的 `src/Api`。现有 `src/Runtime`、`src/Search`、`src/Engine`、战斗内 UI 和部署逻辑没有被改写；worker 通过现有无人测试入口恢复跑局并调用既有求解流程。

当前 Draft 分支同时带有 worker 复用、资源状态、可配置保活和纯模拟能力。若维护者认可公开 API 方向，我可以按维护偏好拆成：

1. 最小确定战前预测 API 与进程隔离；
2. worker 复用、状态和生命周期控制；
3. 显式假设战斗模拟。

本地 `0.29.5` 包版本、`API v5` 编号和本地发布日志只是开发过程标识；合并前可以移除版本提升，并按维护者选择重置首个上游 API 版本。

## 验证

- Combat Solver 与调用端 Release 构建：0 warnings / 0 errors；
- Windows 结构边界检查通过；
- 四 Mod 组合（RitsuLib、Combat Solver、Random Foreseer、Seed Oracle）完成真实 headless API 往返；
- 两次确定预测返回相同结果，同一 worker 复用，调用前后 live 状态令牌一致；
- worker PID、工作集、私有内存和隔离静音状态可读；
- 2/10/30 分钟、一直维持、自动关闭和显式关闭均通过；
- 假设样本种子在 worker 内生效，样本完成后自动关闭，主跑局 RNG 未变化。

代表运行：

- `PRECOMBAT-API-V5-UPSTREAM-0291-013`，runId `e2236a4ec85041dc95c0b3417ed0b688`；
- `SEEDORACLE-PRECOMBAT-V5-LOCALIZATION-014`，runId `e15b744f36e84d85aa8771cf02328121`。

## 希望维护者确认

1. 是否接受为伴生 Mod 提供稳定的公开战前预测边界？
2. 是否认可“独立游戏进程 + 完整快照复核”作为第一版隔离方案？
3. 是否希望先只审查确定预测，把 worker 复用和纯模拟拆到后续 PR？
4. 公开契约的命名、版本号和兼容策略是否需要按项目习惯调整？
