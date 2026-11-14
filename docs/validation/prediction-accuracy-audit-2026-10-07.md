# 预言准确性排查 · 2026-10-07

针对用户报告的三类预言偏差做源码级排查：对照 `.reference/sts2-v0.111.0`（游戏反编译源码）、
`.reference/CombatSolver-latest`（上游 `main`@`7236330`，已同步 0.47.2；本机工坊实装 0.50.1）逐条核对。
两项定位为确凿缺陷并已修复，一项确认为已知能力边界（链路本身无缺陷）。

## 1. 白银熔炉：预言把所有宝箱都判空（已修复）

**游戏原版语义**（`SilverCrucible.cs`、`TreasureRoom.cs`、`TreasureRoomRelicSynchronizer.cs`、`OneOffSynchronizer.cs`）：

- `TreasureRoom.EnterInternal` 先 `Hook.AfterRoomEntered`（`SilverCrucible.TreasureRoomsEntered++`），再 `BeginRelicPicking`；
- `ShouldGenerateTreasure` 判定 `TreasureRoomsEntered > 1`，为 false 时不掷遗物、`DoTreasureRoomRewards` 也不给金币；
- 因此只有**打开的第一个宝箱**为空，之后照常出货。

**缺陷**：`PlanningPredictionService.GenerateTreasure`（规划模式与非规划模式的宝箱预测共用）只调用
`Hook.ShouldGenerateTreasure`，从不派发 `AfterRoomEntered`，影子遗物的 `TreasureRoomsEntered`
停留在实机当前值（通常为 0）。于是路线上的每个宝箱都被判成“还是第一个空箱”——无遗物也无金币。

**修复**：`GenerateTreasure` 在按玩家生成之前，对影子 `TreasureRoom` 派发一次原生
`AfterRoomEntered`（`IterateHookListeners`，与 `GenerateCombatRewards` 派发生命周期 Hook 同一模式）。
已核对 `AfterRoomEntered` 的全部 30 余个实现：其余均为 CombatRoom/RestSiteRoom 限定，
或检查 `RunState.BaseRoom == room`（影子房间不相等），对宝箱房无副作用。

## 2. 远端商店的遗物/卡牌预言漂移（已修复）

**缺陷**：非规划模式的商店预测走 `RandomForeseerAdapter.PredictMerchant(player, ordinal)`——
从当前 RNG 状态连续回放 N 次“纯进店生成”，**不推进沿途房间**。但：

- 每场战斗的卡牌奖励消耗 `PlayerRng.Rewards`（`CardFactory.CreateForReward`，
  含 `RollForUpgrade` 升级掷骰）；
- 战斗遗物掉落与商店遗物稀有度共用 `RelicFactory.RollRarity(player)` = `PlayerRng.Rewards`；
- 宝箱消耗 `Rewards`（金币）+ `TreasureRoomRelics` + 共享遗物袋。

因此从当前位置到第 N 家商店之间每多一场战斗/宝箱，Rewards RNG 偏移与遗物袋就多一分
未建模的漂移——与“越远的商店越不准”的现象完全吻合。同期战斗奖励与宝箱预测
（`PredictCombatRewards`/`PredictTreasureRoom`）早已按路线逐房推进，商店是唯一例外。

**修复**：

- 新增 `IRandomForeseerAdapter.PredictMerchantRoom(player, resolvedRooms)`，与宝箱预测同构：
  `PlanningPredictionService.CreateState` → `ShadowIsolation.Enter` →
  `AdvanceRoomsBeforeTarget`（推进沿途战斗奖励、宝箱、商店）→ `GenerateMerchant`。
- `MapForecastService` 改为按路线键（`RewardRouteKey`）取每条可行世界线的商店预测，
  不再按“未来第几次进店”归并；`BuildMerchantForecast` 的聚合从比较进店序号改为比较库存内容，
  不同路线推演出不同库存时如实降级为“路线相关（无单一值）”，旧逻辑会把其中一条路线的
  库存错当全体的结果。
- 保留 ordinal 版 `PredictMerchant` 供自测与无路线上下文的调用使用。

**注意**：修复后的远端商店预测仍是条件结果（沿途事件选择、购买、拾取会改变世界线），
悬窗标签保持“路线相关”。变化在于：在玩家不做计划外选择的假设下，预测现在与游戏
实际生成逐骰一致。

## 3. API 精准预测下一场战斗与实况有差别（链路无缺陷，属已知边界）

逐环节核对 SeedOracle → Combat Solver worker 的状态传递链，全部与游戏原版一致：

| 环节 | 结论 |
| --- | --- |
| 快照恢复 | worker 端 `RestoreRunSnapshotDirectlyAsync` 对恢复后的 `ToSave` 做 SHA256 对比，不一致即拒绝预测（`DirectRunSnapshot:ExactStateRestored`） |
| 遭遇队列 | `RouteWorldlinePredictor.SelectEncounter` 与 `RoomSet.NextNormal/Elite/BossEncounter` 逐条一致（含双 Boss 判定） |
| 遭遇独立 RNG | 种子 = `runSeed + TotalFloor + hash(encounterId)`；`TotalFloor` = 地图历史条目和，主进程与 worker（含中间点补历史）在生成怪物时逐条相同 |
| 怪物 HP | 悬停预测与 `CombatState.CreateCreature` 同序同流：Niche 克隆、`SetUniqueMonsterHpValue` 的不重复 HP 集合语义、多人缩放条件均一致 |
| 战斗奖励 | `GenerateCombatRewards` 与 `RewardsCmd.GenerateForRoomEnd` 同构（`WithRewardsFromRoom` + `GenerateWithoutOffering`，含前置 Hook 派发） |
| 面板目标 | 含中间篝火/宝箱/商店的路线会传入 `InterveningMapPoints`；中间战斗/事件的路线被排除（其结果依赖未定选择） |
| 中间房间影响 | 篝火/宝箱/商店不消耗战斗流 RNG（Shuffle/MonsterAi/Niche/Combat*），worker 只补历史不补这些流是忠实的 |

剩余偏差来源是 Combat Solver 的已知设计边界，不是 SeedOracle 的状态错误：

1. **游戏内 UI 求解器是全知搜索**：搜索捕获并推进真实 RNG 流（已知名牌序、未来意图），
   预测战损 = “按全知最优打法”的结果。玩家盲打、换目标或换用药顺序都会产生差异；
   目标随机（`CombatTargets` 流）类卡牌尤其如此。上游决策网络工作（blind 模式，
   `docs/strategy/blind-pool-policy-20260928.md`）正是为消除这一估值偏差，但尚未接入
   游戏内战前 API。
2. **镜像覆盖**：未登记的内容以 `Inferred`/`Unsupported` 记风险（红字仅显示），对应战斗的
   数值会偏离。
3. **搜索预算**：预算内的“生还下界/战损”本身随预算变化；面板显示的置信度
   （Complete/Bounded/DeathOnly）即为此标注。

结论：维持现状，无需代码改动。若要把“实况可比”的战损带进 Mod 面板，需要上游把
blind/逐步观察入口暴露到 PreCombatForecast API（可另立工作项）。

## 环境修复（非逻辑改动，随本次提交）

- `local.props`：Random Foreseer 指向工坊已更新的 `lib\0.13.18`（0.13.15 已被清理）。
- `SeedOracle.csproj`：把 `.godot/**`、`sts2-cli/**`、`turn-solver/**`、`.local/**` 加入
  Compile/EmbeddedResource/None 移除列表——`sts2-cli`（9 月 28 日）加入仓库后，其 obj
  生成文件会被项目通配符重复编译，任何全新构建都会失败。
- `RandomForeseerAdapter.cs`：游戏更新后 GodotSharp 4.5 新增 `Godot.MethodInfo/PropertyInfo`，
  为反射类型加 using 别名消除 CS0104。

## 验证

- `dotnet build -c Release` 通过（0 警告 0 错误）。
- `smoke/run-planning-smoke.ps1 -SelfTest` 通过（exit 0）：`[SelfTest] PASS map_types=8
  tooltips=7 merchant_items=13 merchant_second_visit=passed route_variants=175 ...
  combat_reward_cards=1 treasure=relics=1`；同轮 `planning-potion-roundtrip/chain PASS`、
  `native-combat-reward-groups PASS`、`event-audit ok=108 degraded=13 nondeterministic=0
  failed=0`、`planning-combat-simulation PASS`。self-test 的 8 类地图节点预测
  （含商店/宝箱/战斗奖励）已实际走新 `PredictMerchantRoom` 推进路径与修复后的
  `GenerateTreasure`。
- 边界：self-test 的随机种子开局不带白银熔炉，修复的计数器行为由上表源码对照保证，
  未做带遗物的端到端复现；可在实机用先古“白银熔炉”开局悬停第二个宝箱复核
  （预期：显示遗物+金币，只有第一个宝箱为空）。

## 版本

- Seed Oracle `0.1.25`。
