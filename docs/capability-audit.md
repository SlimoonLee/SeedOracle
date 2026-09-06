# Seed Oracle 依赖能力审计

审计基线：2026-09-06。本文记录当前 live 版本的实现边界。

## 权威输入

| 组件 | 本地版本 | 本地/源码证据 |
|---|---:|---|
| Slay the Spire 2 | `0.111.0` / `41cef1ea` | live `sts2.dll`、`sts2.xml` 与 `release_info.json` |
| Random Foreseer | `0.13.11` / `a8ccb72953b9d300a4b39c67c28b73aba1b5c445` | Workshop `3747531952/lib/0.13.11`；仓库 release tag 与 build-info 一致 |
| Combat Solver | 作者主线 manifest `0.31.1` / `0552b33`，已合并 public API v5；带 API 的构建仍可能沿用 `0.31.1` manifest | `.reference/CombatSolver` 的 `origin/main` 与本地部署 DLL 均来自 `0552b33`；旧的 `v0.31.1` tag `6dcd698` 不含该接口 |

审计顺序采用 live 游戏程序集、live Mod 程序集、与 live 提交完全一致的源码。反编译仅用于确认当前 `sts2.dll` 的实际实现。

## 功能矩阵

| 功能 | 当前实现来源 | 直接调用 | Adapter | Seed Oracle 新代码 |
|---|---|---:|---:|---:|
| 下一普通/精英 Encounter | 游戏 `ActModel._rooms` / `RoomSet` | 是，只读 | 否 | 路线队列偏移 |
| Boss / 第二 Boss | 游戏 `ActModel.BossEncounter` / `SecondBossEncounter` | 是，只读 | 否 | 节点映射 |
| Encounter 怪物阵容 | 游戏 `EncounterModel.GenerateMonstersWithSlots` 的局部种子 | 在临时 mutable Encounter 上调用 | 否 | 目标 `TotalFloor` 与临时 RNG 注入 |
| 下一战怪物 HP | 游戏 `Creature.SetUniqueMonsterHpValue` + `RunRngType.Niche` | 在临时 Creature 与克隆 RNG 上调用 | Combat Solver public PreCombat API v5 | 安全的下一节点预测，并可手动补充整场战损 |
| `?` 实际房间类型 | 游戏 `UnknownMapPointOdds` | 不在 live Odds 上调用 | RF 未提供 | 克隆 RNG/赔率与只读 Hook 镜像 |
| 未来 Event 身份 | 游戏 Event 队列 | 在克隆计数与已访问集合上模拟 | RF 未提供地图 Event API | 路线逐项消费与 Hook 快照 |
| 战斗基础奖励 | 游戏 `RewardsSet` / Random Foreseer `CardRewardPrediction` | 只在克隆状态上生成 | 是，集中反射兼容 | 路线推进金币、药水赔率、卡牌、精英遗物及当前内置奖励 Hook |
| 宝箱内容 | 游戏 `TreasureRoomRelicSynchronizer` / `OneOffSynchronizer` | 只在克隆 RNG 与 GrabBag 上生成 | 是 | 路线推进共享遗物袋、宝箱 RNG 与本地金币 RNG |
| Event Option 随机内容 | Random Foreseer `EventOptionPrediction` / 31 类事件预测注册表 | 在序列化影子跑局的 mutable Event 上调用 | 是，集中反射兼容 | 按路线推进奖励状态，初始化事件 RNG、变量和初始选项，提取卡牌组及其他随机奖励 |
| 遗物拾取即时结果 | Random Foreseer `RelicPickupPrediction` | 已在原 UI 显示 | 否 | 否 |
| 休息点随机结果 | Random Foreseer `RestSitePrediction` | 已在原 UI 显示 | 否 | 否 |
| 信使补货 | Random Foreseer `MerchantRestockPrediction` | 已在当前商店商品 Hover 显示 | 否 | 否 |
| 未进入商店的初始库存 | RF 只提供补货预测，没有完整初始库存入口 | 部分复用 RF 的 `RunPredictionContext`、卡牌预测与 Hook Mirrors | 是，集中反射兼容 | 从当前起第 N 次进店的顺序生成、遗物、药水、价格与路线投射 |
| 当前战斗 Intent | Combat Solver `IntentForecaster` | 无公开 API | 仅能力探测 | 否 |
| 当前战斗 Root / 搜索 | Combat Solver `CombatRootSnapshot`、`CombatSearchCoordinator` | 仍不公开，均为 `internal` | public API 不暴露这些类型 | 否 |
| 战前战损、worker 状态与假设样本 | Combat Solver public PreCombat API v5 | 主进程只捕获/复核完整存档；开战、样本 RNG 与搜索位于静音、可复用的独立 headless 进程 | 是，编译期契约 | 确定路线逐项计算与缓存；显示 PID/工作集/私有内存；保活 2/10/30 分钟、一直维持、自动关闭、手动关闭及预热；0～10 次当前幕弱怪/强怪/精英/指定怪组/Boss 假设样本 |
| 准确度与依赖传播 | 无 | 否 | 否 | 是 |
| 地图 HoverTip | 游戏 `NMapPoint.OnFocus` | Harmony Postfix | 否 | 是 |
| 三幕先古 / Boss 总览 | 游戏跑局中已生成的 `ActModel.Ancient`、`BossEncounter`、`SecondBossEncounter` 与先古历史 | 只读；未访问先古在序列化影子跑局生成初始选项 | 否 | 地图顶部常驻三栏，未来状态相关选项明确标注条件预测 |
| 地图路线覆盖与 Boss 布局 | 游戏地图节点容器 / `NHoverTipSet` | 只创建显示节点 | 否 | 完整路线分色、Boss 终点连线、最多三面板紧凑布局与悬窗层级重排 |
| live state 纯净性检查 | 无通用局外实现 | 否 | 否 | 是 |

## Random Foreseer 可复用入口

RF `0.13.11` 的主要预测类是 `internal`。对地图商店聚合有用的签名包括：

- `RunPredictionContext(Player)`：克隆 `Rewards`、`Shops`、`Niche`、玩家 `RelicGrabBag`、卡牌/药水赔率；
- `MerchantRestockPrediction.PredictCard(...)`：安全生成一张商店卡牌并执行 RF 已维护的卡牌 Hook Mirror；
- `OutOfCombat.Mirrors.HookMirrors.ModifyMerchantPrice(...)`：价格修改的 RF Mirror；
- `CardRewardPrediction` 与 `PredictionUtils`：RF 自己的无 live-state 写入卡牌构造与升级路径。
- `CombatEndEffectPrediction.FastForwardMonsterRoomCombatEndHooks(...)`：在克隆上下文中推进已知战后 RNG 消费者；
- `RunPredictionContext.PotionRewardOdds`：让每条路线独立推进药水掉落的动态赔率。
- `EventOptionPrediction.Registry`：复用 RF 已维护的 31 类事件选项预测器；Seed Oracle 只在序列化影子跑局中建立 mutable Event，并把预测 HoverTip 转成地图摘要。

因为没有 public integration API，所有内部签名访问都封装在 `RandomForeseerAdapter`。Adapter 启动时逐项验证类型和签名；上游变化时该能力会返回 `Unsupported`，不会退回到 live 商店生成。

RF 当前明确没有：

- 未进入商店时的完整 `MerchantInventory` 预测；
- 地图 `?` 房间类型预测；
- 地图未来 Event 身份预测。

RF 的事件选项预测原本只服务于已经进入的事件。Seed Oracle 的 Adapter 会先用 `RunManager.ToSave` / `RunState.FromSerializable` 建立独立影子跑局，再推进所选路线上的已建模战斗奖励、商店与宝箱状态。目标事件使用和游戏 `BeginEvent` 相同的事件局部 RNG 种子，但跳过可能改写玩家 UI 状态的 `BeforeEventStarted`，仅计算变量并生成初始选项。沿途事件选择、奖励取舍、休息操作和购买仍作为条件分支显示。

## Combat Solver 可复用入口

历史 Workshop CS `0.28.3` 只有 `CombatSolver.Entry` 与 Mirror 描述类型是 public。当前作者主线仍将以下求解器类型保持为 `internal`，仅通过 v5 契约暴露隔离的战前边界：

- `CombatRootSnapshot.Capture(CombatState)`；
- `IntentForecaster.Forecast(...)`；
- `CombatSearchCoordinator.Solve(...)`；
- `SolverResult` / `SolverOverlaySnapshot`。

作者主线中的 `CombatSolver.Api.PreCombatForecastApi` v5 已在合并提交 `0552b33` 中可用。它没有尝试从未来 Encounter 手工拼装不完整 Root，而是把规范化完整跑局、Encounter、目标楼层/列坐标、节点类型和已确定的连续非战斗地图历史交给独立游戏进程；worker 精确恢复跑局、补记结构性路径历史并应用可选入战 HP 后，通过原生入口建立战斗，再复用上述内部 Root 与搜索管线。独立的 `SimulateAsync` 在精确恢复校验后才替换隔离战斗 RNG；状态接口只读返回 worker 进程、内存信息和可空空闲期限。

`CombatSolverAdapter` 直接依赖该编译期契约，并在启动时检查 `ApiVersion >= 5` 与 `IsAvailable`。当前 Seed Oracle 构建应与作者主线 `0.31.1` 的 API 构建（合并提交 `0552b33` 或更新）成对使用；仅有旧 `v0.31.1` 标签内容的无 API 构建会保持不支持，不会退回到不安全的内部入口。

## 已确认的游戏行为

- `RoomSet.NextNormalEncounter` 与 `NextEliteEncounter` 只按已访问计数读取预生成队列；
- `ActModel.PullNextEncounter` 不消耗 UpFront RNG；
- `EncounterModel.GenerateMonstersWithSlots` 在 `_rng == null` 时使用 `run seed + target TotalFloor + encounter id hash`；
- `CombatState.CreateCreature` 用 `RunState.Rng.Niche` 决定同阵营不重复的初始 HP；
- 怪物自己的局部 RNG 种子还包含当前幕与当前地图坐标，因此远端战斗必须用目标坐标进入，不能只改楼层；
- `Shuffle`、`MonsterAi`、战斗牌/药水/选牌/目标等序列彼此独立。篝火、宝箱和未购买的商店本身不直接推进这些战斗序列，但玩家状态、地图历史和 Mod Hook 仍可能改变开战结果；
- Event 选项可能推进 `Niche` 等战斗相关序列，选项未确定前不能把事件后战斗当成同一精确世界线跳过求解；
- `UnknownMapPointOdds.Roll` 同时推进独立 RNG 并修改赔率，因此预测必须使用两者的副本；
- `MerchantInventory.CreateForNormalMerchant(livePlayer)` 会推进多组 RNG、修改 GrabBag，并通过商品构造触发 Seen 状态，禁止用于预测。
- 普通/精英/Boss 的基础奖励顺序是药水赔率、金币、可选药水、卡牌，精英随后从玩家遗物袋正面抽取遗物；
- 宝箱遗物使用 `RunRngSet.TreasureRoomRelics` 和共享遗物袋，宝箱金币另用本地玩家的 `Rewards` RNG；
- 商店展示过的遗物与精英奖励生成出的遗物会同步影响共享遗物袋，因此宝箱路线预测必须同时推进玩家与共享遗物袋。
- 三幕的先古身份和 Boss（包括双 Boss）在跑局生成时已经写入 `ActModel`，可以直接准确显示；先古自身使用由跑局种子、玩家槽位和事件 ID 派生的独立 RNG。未访问先古的选项可以在影子跑局重建，但部分候选池读取进入先古时的牌组、遗物或进度，因此未来幕只承诺“按当前状态预测”。
