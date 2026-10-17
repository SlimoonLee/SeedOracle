# 种子先知普通模式可预测性审查 · 2026-10-14

本报告记录修复前 0.1.25 工作区的审查结论。后续修复详见[0.1.26 修复与验证记录](non-plan-prediction-fixes-2026-10-14.md)。

## 结论与范围

本次“非全知规划模式”指关闭地图上的 **全知规划模式** 面板后，普通地图悬停、路线颜色、先古/Boss 总览，以及独立的战损与模拟面板。它不是 CombatSolver 的 blind 策略模式。

**普通模式依然读取真实跑局的隐藏队列及 RNG 状态副本。关闭全知规划只改变交互与选择推演方式，不会把预测器限制为玩家可见信息。** 因而要分别判断：信息是否已经储存；在给定状态下能否精确生成；是否尚有未决定的玩家输入；当前实现是否忠实于原版。

当前最可靠的是已经预生成的先古/Boss 身份、给定路线下的遭遇队列和怪物阵容。下一步怪物初始 HP、基础战斗掉落和单人宝箱有条件可精确生成。事件内容只有部分选项、部分结果结构受到支持。远端结果需要沿途条件，战损属于指定打法和求解器模型的结果。

**特别例外：当前普通模式的商店生成存在原版对照缺陷；即使标签是“当前世界线”，也不能把整份库存与价格视为精准。** 远端事件身份也有楼层筛选未推进的问题。以下矩阵已把这些例外纳入结论，而不是照抄 UI 标签。

审查对象是 `D:\FORSTS2` 的当前工作区（SeedOracle 清单版本 `0.1.25`，Git HEAD `da064a0de678405358b9a01a94dc353a7d40cd09`，含已有未提交修改），不是仅审查 HEAD，也不是核验实装 SeedOracle DLL。

## 1. 原版基线与证据强度

| 对象 | 本次确认 |
| --- | --- |
| 本机原版 DLL | `D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll` |
| 原版产品元数据 | `0.1.0+41cef1ea4657c524aa50e870df009e56337e8c32`，与 `.reference/sts2-v0.111.0/Properties/AssemblyInfo.cs` 一致 |
| 原版 SHA-256 | `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9` |
| 刷新反编译对照 | `RoomSet`、`UnknownMapPointOdds`、`MerchantInventory`、`RewardsSet`、`GoldReward`、`EventModel` 六类均与既有反编译源一致；比较忽略空白，并统一 ILSpy 对编译器生成类型名的转义形式 |
| Random Foreseer | 本机 `lib/0.13.18/RandomForeseer.dll`；SHA-256 `9CFF13FB40278D3E27E9B83DB249D2088E730420BEB43AB6B3A17B9F2CC3B239`；刷新注册表及五个相关预测器/辅助类 |
| CombatSolver | 本机清单 `0.50.1`；检查 SeedOracle 的 API 调用和 `.reference/CombatSolver-latest` 的 API/worker/采样实现。没有把该源码目录当成本机 DLL 的完整逐字等价证明 |
| 验证边界 | 本轮为源码与本机 DLL 反编译审查，没有执行游戏战斗、GUI、全事件端到端差分，也没有构建或部署业务代码。旧 self-test 和旧运行日志不作为本轮实机通过证据 |

刷新文件、基线记录及本次相关源码哈希在 [本次审查证据目录](<D:/FORSTS2/.local/seed-oracle-predictability-audit-20261014>)，入口为 [baseline.json](<D:/FORSTS2/.local/seed-oracle-predictability-audit-20261014/baseline.json>)。

## 2. 普通模式实际能够读取什么

入口为 [NMapPointPatches.cs:33](<D:/FORSTS2/src/UI/NMapPointPatches.cs:33>) → `MapForecastService.Predict`。只有开启规划面板时才调用 `NarrowToPlan`；普通模式保留从当前位置到目标的路线集合。

| 可读取/复制的信息 | 使用位置及意义 |
| --- | --- |
| 当前地图、历史、幕与楼层、解锁状态、玩家人数 | 路线可达性、教学特例、候选池限制、怪物生成种子与多人缩放 |
| 三幕预生成的先古、Boss、第二 Boss，以及当前幕普通/精英/Event 队列和已消费索引 | 直接读取未来身份；不是单靠种子猜测 |
| 已访问事件 ID、当前 Hook 监听器 | 跳过重复/不符合条件的事件，应用问号房间类型修正 |
| `RunRng.UnknownMapPoint`、问号房间当前赔率与基础赔率 | 精确回放问号房间 roll，并按路线推进保底赔率 |
| `RunRng.Niche` | 下一步怪物 HP；部分原生事件/遗物效果也会消耗它 |
| 玩家 `Rewards`、`Shops` 等 RNG，卡牌稀有度赔率、药水掉落赔率 | 商店与战斗奖励生成；不同系统会共用同一随机流 |
| 玩家及共享遗物袋、`TreasureRoomRelics` RNG | 商店从袋尾取遗物，战斗/宝箱按各自规则取遗物；一处生成或拾取可改变后续候选 |
| 当前牌组、升级/附魔、遗物计数器、药水、生命、金币等序列化状态 | 事件准入、事件选项与各种奖励 Hook 的条件 |
| 事件独立 RNG | 以跑局 seed + 玩家槽位（共享事件为 0）+ 事件 ID 的确定性 hash 初始化；同一事件在同一入场状态下可重现 |
| 战前 API 的完整快照 | 给隔离 worker 恢复战斗状态；普通预测调用没有传 blind 策略参数 |

代码依据：[CreateSerializableRun](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:315>)、[UnknownMapPointSimulation](<D:/FORSTS2/src/Forecasting/UnknownMapPointSimulation.cs:37>)、[RunSeedOverviewPredictor](<D:/FORSTS2/src/Forecasting/RunSeedOverviewPredictor.cs:81>)、[CombatSolverAdapter](<D:/FORSTS2/src/Integration/CombatSolverAdapter.cs:99>)。

它**没有预先得到**玩家将来的事件选择、拾取/跳过、删哪张牌、买哪些商品、是否补货、锻造/休息/其他篝火动作、实战出牌目标与用药顺序，以及其他玩家将来的操作。这些输入会决定随机流消费、候选池、遗物袋和状态，不能由当前 seed 唯一替玩家作出决定。

## 3. 四类结果的能力矩阵

这里“精准”只用于原版相同状态与相同生成步骤下的结果；“条件分支”是能够写出条件的确定结果，不等于概率预测；“部分预测”是只覆盖其中一些字段、选项或候选；“当前不可预测”包括尚缺输入和当前没有实现，两者在说明中区分。

| 信息 | 应归类 | 精确成立的条件 / 当前限制 |
| --- | --- | --- |
| 三幕先古身份、Boss、第二 Boss 身份 | **精准** | 开局原版已生成并保存在各幕 RoomSet；总览直接读取。未来取得哪些遗物通常不改变已经生成的身份 |
| 已见先古选项与实际选中项 | **已知事实** | 取自历史记录，不是未来预测 |
| 当前幕普通、精英、Boss 遭遇身份 | **给定路线可精准；未选路线时有分支** | 普通战和精英分别按 visited + 沿途同类战斗数索引。问号变成战斗会占用对应队列，嵌套事件战斗不按普通地图房间消费该队列。穷举未完成时不能宣称覆盖所有路线 |
| 目标战斗的初始怪物种类/阵容 | **给定遭遇与楼层可精准** | 原版独立 RNG 为 `runSeed + targetTotalFloor + hash(encounterId)`，不直接依赖沿途抽牌随机流；换路线可能先改变遭遇身份 |
| 下一步 `?` 的房间类型 | **当前世界线可精准** | 用当前 UnknownMapPoint RNG、赔率、教学次数、商店黑名单及 Hook 快照；途中没有尚未结算的状态变更 |
| 多个远端 `?` 的房间类型 | **条件分支** | 沿途 `?` 数量、前一房是否商店、节点后继结构及相关遗物保持该路线推演条件。不同路线可分别得到确定类型；不是给节点永久固定一个房间 |
| 下一事件身份 | **当前状态可精准** | 原版队列、visited ID、`IsAllowed` 和 Hook 条件相同；下一步的原版筛选发生在新增目标历史条目前 |
| 远端事件身份 | **条件预测，且当前实现有缺陷** | 不仅看已消耗几个事件，还看到达时金币、牌组、药水、楼层等。当前模拟没有推进用于 `IsAllowed` 的 run，见 F4 |
| 下一步确定战斗的怪物初始 HP | **当前世界线可精准** | 地图允许移动、全部路径只有一步且同一遭遇；复制 Niche，调用原版不重复 HP 与多人缩放方法。不能把它理解为战损 |
| 隔着商店/篝火/宝箱的首场战斗 HP | **普通 HoverTip 当前不提供数值；战前 worker 可条件生成** | `BuildMonsterHpForecast` 实际要求 immediate。README 关于隔房首战 HP 的描述宽于当前普通浮窗实现；战损面板可建立这类首战场景 |
| 更远战斗 HP、未来洗牌、怪物行动及随机目标 | **普通地图界面当前不提供确定序列** | 沿途 Niche/战斗随机流消费与玩家动作未定。给定完整状态和动作在引擎中可以生成，不是数学上永远不可预测；当前没有这种普通模式分支树 |
| 下一场普通战/精英战的基础金币、卡牌候选、药水、遗物 | **基础生成可精准，整场掉落为部分/条件预测** | 必须胜利、基础奖励与 Hook 状态不变、没有逃跑导致的金币比例变化、没有战中新增奖励或改变奖励条件。当前只创建空的预测 CombatRoom，不能保证这些条件，见 F5 |
| 远端战斗奖励 | **条件分支，覆盖不完整** | 推进沿途基础战斗奖励、商店生成和宝箱生成；没有执行全部事件、篝火动作、奖励拾取和战斗过程。奖励组的生成与组内实际选择不同 |
| 最后一幕 Boss 战后奖励 | **按原版规则可确定不生成常规奖励** | 原版 `WithRewardsFromRoom` 在最后一幕 Boss 直接返回，不应额外承诺普通的三选一卡包/金币 |
| 下一步单人宝箱的基础遗物与金币、空箱状态 | **当前世界线可精准** | TreasureRoomRelics、共享遗物袋、Rewards RNG 与 treasure Hook 相同；已派发入房回调，白银熔炉仅第一个宝箱为空；教学首箱有专门处理。这里指基础宝箱生成内容 |
| 远端宝箱 | **条件分支** | 是否打开/跳过、沿途遗物袋变化与遗物获得效果会改变后续状态。当前推进会生成候选、按单人情形移除相关遗物，但没有把所有未来拾取效果完整执行 |
| 多人宝箱候选池 | **候选部分可精准，归属有分支** | 候选生成可回放；最终归谁、投票冲突和实际获利依赖各玩家选择。不能把显示的所有遗物当成本地玩家必得 |
| 下一步商店初始库存、半价、价格、删牌价 | **原版机制本可精准；当前实现不可信** | 当前 `PredictMerchantRoom → GenerateMerchant` 有漏掷价格骰、漏去重、漏卡牌结果 Hook 和会员卡折扣问题，见 F1–F3。不能沿用旧 ordinal 适配器的正确性结论 |
| 远端商店 | **条件分支，叠加当前商店实现缺陷** | 生成初始库存仍会消费 Rewards/Shops 与遗物袋；购买、补货、删牌与沿途拾取条件未定。不能只以“第 N 次进店”说明全部差异 |
| 未访问先古的初始交易选项 | **当前状态生成；未来幕为条件投影** | 身份固定不代表交易候选池固定。未来牌组、附魔资格、事件宠物等会改变选项；总览已标“按当前状态预测” |
| 事件初始选项附带的卡/遗物/药水提示 | **部分信息可精准** | 原生初始选项生成与入场状态相同的前提下，可知道提示中的具体对象；提示对象也可能是代价、删除对象或说明物，并非全部都代表得到的奖励 |
| 支持事件中某个已给定选项的随机物品 | **部分/条件预测** | RF 仅预测注册类型中匹配的 option key。某些是唯一结果，某些是选牌目标导致的候选并集，某些是继续点击后的序列；不能统一解释成一次选项的必得结果，见 F8 |
| 多页事件的后续选项、小游戏布局与完整终局 | **普通模式当前不完整或不支持** | 普通适配器只初始化一次事件、遍历初始未锁选项；不执行选择后进入下一页。水晶球专用布局属于全知规划执行适配器，普通预测链没有调用它 |
| 战损、最终 HP、回合与用药 | **打法条件下的求解结果/近似评估** | 搜索看确定化 RNG；当前普通战前入口没有 blind 策略。玩家不同打法会产生不同结果；模型覆盖与预算还有限。Complete 表示找到结束战斗路线，不是证明全局最优或玩家必然承受该战损 |
| “咱俩碰一碰”的最低/平均/最高战损 | **假设样本统计** | 采样改变怪物生成和九条战斗流 RNG；不是当前世界线预言，也不自动等于遵守玩家可见信息边界的 blind 决策策略 |
| 任意未支持 Mod 内容、未登记事件、其他玩家未来操作 | **当前不可给唯一预测** | 有的需要补生成契约/Hook 支持，有的缺尚未发生的外部输入。缺信息应显示不可用或有条件，不能把缺预测数据解释成结果为空 |

阵容/队列依据：[RouteWorldlinePredictor](<D:/FORSTS2/src/Forecasting/RouteWorldlinePredictor.cs:123>)、[EncounterCompositionPredictor](<D:/FORSTS2/src/Forecasting/EncounterCompositionPredictor.cs:21>)；HP 依据：[MapForecastService](<D:/FORSTS2/src/Forecasting/MapForecastService.cs:312>)、[MonsterHpPredictor](<D:/FORSTS2/src/Forecasting/MonsterHpPredictor.cs:12>)；奖励/宝箱依据：[RandomForeseerRewardAdapter](<D:/FORSTS2/src/Integration/RandomForeseerRewardAdapter.cs:35>)、[PlanningPredictionService](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:418>)；原版对照：[RoomSet](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Rooms/RoomSet.cs:70>)、[EncounterModel](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Models/EncounterModel.cs:263>)、[RewardsSet](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Rewards/RewardsSet.cs:206>)。

## 4. 已明确枚举的先古条件分支

以下分支保留同一事件 seed，分别改变一个准入条件，其他条件保持当前值。它们不是每条未来牌组世界线的组合穷举。

| 先古 | 条件 |
| --- | --- |
| 特兹卡塔拉 `Tezcatara` | 进入时是否仍有带 Strike 标签且稀有度为 Basic 的基础攻击牌 |
| 诺努佩佩 `Nonupeipe` | 是否至少有 4 张可附魔 Swift/迅捷的牌 |
| 坦克斯 `Tanx` | 是否至少有 3 张可附魔 Instinct/本能的牌 |
| 帕尔 `Pael` | 是否至少有 3 张可附魔 Goopy/黏糊的牌 |
| 帕尔 `Pael` | 是否至少有 5 张可移除牌 |
| 帕尔 `Pael` | 是否尚未拥有事件宠物 |

只有两次生成的选项真的不同才显示替换。实现还用当前条件回放核对原生选项；若回放不匹配则不显示条件替换文字。这能防止已知规则漂移时继续显示旧分支，但不能证明没有其他未来条件。

依据：[GetConditionalRules / IsConditionMet](<D:/FORSTS2/src/Forecasting/RunSeedOverviewPredictor.cs:291>) 与四个原版事件的 `GenerateInitialOptions`。

## 5. 事件内容的实际覆盖

本机 RF 0.13.18 注册表确认 **31 个类型**：

`AromaOfChaos`, `BattlewornDummy`, `BrainLeech`, `ColorfulPhilosophers`, `DenseVegetation`, `DollRoom`, `DoorsOfLightAndDark`, `EndlessConveyor`, `InfestedAutomaton`, `LuminousChoir`, `MorphicGrove`, `PotionCourier`, `PunchOff`, `RanwidTheElder`, `Reflections`, `RoomFullOfCheese`, `RoundTeaParty`, `SlipperyBridge`, `Symbiote`, `TabletOfTruth`, `TheFutureOfPotions`, `TheLegendsWereTrue`, `ThisOrThat`, `TinkerTime`, `TrashHeap`, `Trial`, `UnrestSite`, `WarHistorianRepy`, `WelcomeToWongos`, `Wellspring`, `WhisperingHollow`。

这表示存在某些选项的预测器，不表示 31 个事件的所有页、所有选项、最终净收益都已覆盖。未注册事件仍可能通过原生初始 HoverTip 提取出个别具体对象；无提取结果时返回 `null`，普通浮窗不一定出现“不支持”提示。

| 例子 | 普通模式应该如何理解 |
| --- | --- |
| 滑脚木桥 `SlipperyBridge` | 初始随机删牌对象来自原生选项；继续 HOLD_ON 的预测是有限次重抽序列，次数取 RF 的 `SlipperyBridgeRerollPreviewCount`，不是一次同时删除所有列出的牌 |
| `AromaOfChaos` / `Symbiote` / `WhisperingHollow` 的变牌 | 对每个可选目标从同一 RNG 副本计算结果再去重，是“选择不同原牌后可能得到的集合”，不是一个无条件唯一结果 |
| `Trial` 的多张变牌 | RF 的不同组表示不同变换位置的候选；组间并非任意组合都可同时成立。普通适配器也不推进初始选择进入所有后续审判页 |
| 旺戈 `WelcomeToWongos` | 精选遗物可从原生初始提示读取；廉价池和离开时降级另有对应 option key 的预测，不能外推后续商店购买结果 |
| `TheFutureOfPotions` | 每个当前药水选项对应候选卡包，结果依赖当前药水、对应类型与所选槽位；玩家先用药后重新预测可能改变结果 |
| `TinkerTime` | 本机预测器匹配 CHOOSE_CARD_TYPE 页；普通适配器不会自动执行初始选项来到该页，不能把注册本事件当成完整改造卡预测 |
| `PunchOff` 与 `BattlewornDummy` 的战斗后内容 | 部分预测还受 RF fairness 设置限制；即便有物品预览，也不保证打赢、战损或完整战斗后的状态 |
| 水晶球 `CrystalSphere` | 不在上述注册表；普通事件预测没有进入全知规划的 minigame 专用布局服务 |

依据：[普通事件适配器](<D:/FORSTS2/src/Integration/RandomForeseerEventAdapter.cs:82>)、[本机 RF 注册表](<D:/FORSTS2/.local/seed-oracle-predictability-audit-20261014/RandomForeseer.EventOptionPrediction.cs:18>) 及同证据目录中刷新的预测器。

## 6. 代码审查发现

以下是源码已确认的生成差异或表达问题；本轮没有把它们包装成已经做过实机种子复现的结论。

### F1 · P1 · 普通商店漏掉半价牌第二次价格骰

普通悬停当前调用 `PredictMerchantRoom`，使用 `PlanningPredictionService.GenerateMerchant`，而不是旧的 ordinal 版 `PredictMerchant`。

原版 `MerchantInventory.PopulateCharacterCardEntries` 先 `Populate()`（包含一次 CalcCost），半价项再 `SetOnSale()`，该方法再次 `CalcCost()`，因此额外消费一次 Shops RNG。当前生成器只取一次价格 roll 后直接除以二。

结果：半价牌使用了错误价格；半价项之后的 Shops 流落后一骰，后续角色卡、无色卡、药水及相关价格可能改变；后续商店继续累积漂移。旧 `RandomForeseerAdapter.PredictCard` 已明确处理这第二骰，不能用旧适配器的自测替代当前路线适配器的差分验收。

位置：[PlanningPredictionService.cs:587](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:587>)；原版 [MerchantInventory.cs:100](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Entities.Merchant/MerchantInventory.cs:100>)、[MerchantCardEntry.cs:134](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Entities.Merchant/MerchantCardEntry.cs:134>)；旧适配器 [RandomForeseerAdapter.cs:291](<D:/FORSTS2/src/Integration/RandomForeseerAdapter.cs:291>)。

建议直接复用隔离环境下的原生上架语义，或完整补齐 RNG 账目；验收要逐项比较原生初始库存/价格与生成后 RNG 状态。

### F2 · P1 · 商店卡牌漏已上架去重和卡牌结果 Hook

当前循环每次把完整 `characterPool` 传给 CardFactory，没有排除前一槽已经上架的卡。原版 `MerchantCardEntry.Populate` 先构造库存 blacklist 再 `Except`，因此当前同类型双槽可能重复或与原版候选不同。

同时 CardFactory 只生成基础 `CardCreationResult`；原版上架步骤还调用 `Hook.ModifyMerchantCardCreationResults`，当前方法未调用。MoltenEgg/ToxicEgg/FrozenEgg 在这个 Hook 中升级商店相应类型的牌。持蛋时当前预览的“+”状态可以不符合原版。

位置：[PlanningPredictionService.cs:585](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:585>)、[无色卡同一路径](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:602>)；原版 [MerchantCardEntry.cs:81](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Entities.Merchant/MerchantCardEntry.cs:81>)、[MoltenEgg.cs:35](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Models.Relics/MoltenEgg.cs:35>)。

### F3 · P1 · 影子 LocalContext 导致会员卡折扣被关闭

`PredictMerchantRoom` 以默认参数进入 `ShadowIsolation`，`AllowLocalContext=false`。本项目对 `LocalContext.IsMe(shadowPlayer)` 的后置补丁将结果强制置 false；而原版 MembershipCard 的 `ModifyMerchantPrice` 要求 `LocalContext.IsMe(Owner)` 为 true 才给 50% 折扣。当前 `ApplyMerchantPrice` 直接走原生 Hook，因此在该路径下会员卡折扣被跳过。

位置：[RandomForeseerRewardAdapter.cs:86](<D:/FORSTS2/src/Integration/RandomForeseerRewardAdapter.cs:86>)、[ShadowIsolation.cs:374](<D:/FORSTS2/src/Integration/ShadowIsolation.cs:374>)、[ApplyMerchantPrice](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:677>)；原版 [MembershipCard.cs:18](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Models.Relics/MembershipCard.cs:18>)。

需要在保持 UI/存档副作用隔离的前提下恢复“本地玩家价格语义”，并差分核对会员卡与快递员组合；不能只用无折扣玩家验证价格。

### F4 · P1 · 远端事件筛选使用冻结的当前 run

`EventQueueSimulation` 推进自身 `_eventsVisited` 与 visited ID，但始终调用 `candidate.IsAllowed(run)`，`run` 是当前真实跑局。`RouteWorldlinePredictor` 没有给它逐房推进楼层/历史或玩家状态。

原版 `PunchOff.IsAllowed` 要求 `TotalFloor >= 6`，`SlipperyBridge.IsAllowed` 要求 `TotalFloor > 6` 且有可移除牌；原版选事件前，沿途已经到达的房间均已加入历史。当前预测会一直以悬停时的 TotalFloor 检查。

可构造的源码反例：当前 TotalFloor=5，剩余 Event 队列首项是 PunchOff；目标 `?` 前经过一间休息房且不改变牌组。真实目标筛选看到沿途新增历史后的 TotalFloor=6，可以选 PunchOff；当前远端预测仍看到 5，跳过它。这个偏差不需要任何删牌/购买选择。

位置：[EventQueueSimulation.cs:26](<D:/FORSTS2/src/Forecasting/EventQueueSimulation.cs:26>)、[RouteWorldlinePredictor.cs:34](<D:/FORSTS2/src/Forecasting/RouteWorldlinePredictor.cs:34>)；原版 [PunchOff.cs:42](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Models.Events/PunchOff.cs:42>)、[RunManager.cs:914](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Runs/RunManager.cs:914>)。

金币、药水数量、牌组等 `IsAllowed` 条件也不会在此事件队列模拟器中随沿途变化。需要影子 run 按原版筛选时点推进；未选择的玩家效果保留为条件分支。

### F5 · P1 · 下一场“当前世界线”战利品缺少战斗条件

奖励预测新建 CombatRoom，其 `GoldProportion` 默认 1，没有执行战斗或带入将来产生的 ExtraRewards；下一步战斗却直接返回 `CurrentWorldline` 标签。

原版普通战金币比例来自战斗结束时 `Encounter.CalculateGoldProportion`，会考虑逃跑怪物；GremlinMercNormal 还区分抢走金币的情况。原版 TheHunt 打出击杀会给该 CombatRoom 添加额外 CardReward，当前预测不会知道玩家将如何使用它。额外奖励生成又会消耗 Rewards RNG，进而改变后续世界线。

位置：[PlanningPredictionService.cs:433](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:433>)、[RandomForeseerRewardAdapter.cs:51](<D:/FORSTS2/src/Integration/RandomForeseerRewardAdapter.cs:51>)；原版 [CombatRoom.cs:51](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Rooms/CombatRoom.cs:51>)、[CombatRoom.cs:230](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Rooms/CombatRoom.cs:230>)、[TheHunt.cs:47](<D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Models.Cards/TheHunt.cs:47>)。

应明确为“胜利、按当前模型的基础掉落”；战斗可改写奖励时进一步降级，或使用具体战斗终局状态生成奖励。

### F6 · P2 · 单结果视图遗漏状态条件和路线截断提示

路线搜索限制 8192 个状态、512 条路径。`BuildEncounterForecast` 判断“所有路线相同”时只看已枚举 variants，没有检查截断；问号和事件单结果也会返回 CurrentWorldline。

UI 只有在结果分组数 >1 时进入路线展开视图；`RoutesTruncated` 与 `HasUnmodeledStateDependency` 提示位于该展开分支及 Boss 分支。若目前枚举结果都相同，普通单结果视图不会显示这两条重要条件。这可能把“已计算部分相同”显示为“确定”，并隐藏沿途状态尚未建模的事实。

位置：[RouteStateExplorer.cs:10](<D:/FORSTS2/src/Forecasting/RouteStateExplorer.cs:10>)、[MapForecastService.cs:296](<D:/FORSTS2/src/Forecasting/MapForecastService.cs:296>)、[MapForecastTooltipBuilder.cs:172](<D:/FORSTS2/src/UI/MapForecastTooltipBuilder.cs:172>)、[展开分支提示](<D:/FORSTS2/src/UI/MapForecastTooltipBuilder.cs:278>)。

所有视图应统一显示截断/状态条件；截断时不可据已枚举样本给出全路线 Exact 结论。

### F7 · P2 · 路线分组未纳入商店内容，部分卡牌状态也被丢失

`RouteMarkerPlanner.OutcomeKey` 对商店只包含 `FutureVisitOrdinal`，没有包含库存/价格。两条路线都到第一家店但分别经过普通战和精英战，可以因为 Rewards 与遗物袋不同而库存不同；MapForecastService 会识别不同库存，但路线分组仍可能合并，导致没有颜色展开可查看库存，只剩“见路线条件”。

此外卡牌奖励/事件分组 key 主要取 ModelId，未包含升级或附魔等展示差异。同 ID 不同“+”可能被合并为代表路线内容。

位置：[RouteMarkerPlan.cs:112](<D:/FORSTS2/src/UI/RouteMarkerPlan.cs:112>)、[CombatRewardKey](<D:/FORSTS2/src/UI/RouteMarkerPlan.cs:148>)；库存比较已存在于 [MapForecastService.cs:367](<D:/FORSTS2/src/Forecasting/MapForecastService.cs:367>)。

应以完整展示内容及准确度分组，商店至少包括所有条目、价格、半价与删牌价；卡牌包括升级/附魔等状态。

### F8 · P2 · 事件候选集合与顺序结果被统一显示成“选项结果”

RF 的 `PredictDistinctDeckTransformResults` 为每个可选原牌克隆同一 RNG 并生成对应变牌，再按结果 ID 去重。普通适配器把它们装入一组 Items；浮窗的单组文案是“选项结果：A/B/...”，没有保留“选哪张原牌 → 哪张结果牌”的映射。类似滑脚木桥的重抽预览又属于顺序序列。它们不能共用“无条件精确结果”的语义。

位置：[本机 RF helper](<D:/FORSTS2/.local/seed-oracle-predictability-audit-20261014/RandomForeseer.RandomForeseerCode.OutOfCombat.OutOfCombatPredictionUtils.cs:22>)、[RandomForeseerEventAdapter.cs:196](<D:/FORSTS2/src/Integration/RandomForeseerEventAdapter.cs:196>)、[MapForecastTooltipBuilder.cs:801](<D:/FORSTS2/src/UI/MapForecastTooltipBuilder.cs:801>)。

应给结果附加种类：确定物品、目标选择候选、序列预览、卡包待选、未覆盖；候选集合显示为条件/部分预测，不能仅以整个事件的 CurrentWorldline 标签表达。

## 7. 当前模式不提供的保证

1. **不保证真实战损等于预估战损。** 普通 `ForecastAsync` 没有 blind 参数；使用已捕获世界线搜索一条打法。搜索预算与镜像覆盖也会影响数值，Complete 只表示找到终局路线。
2. **不保证远端彩色路线覆盖全部选择分支。** 它枚举地图路径，未穷举每个事件、战斗、商店和奖励页的选择；而且地图路径本身有上限。路线条数不是概率权重。
3. **不保证“当前世界线”始终表示完整精准。** 该标签当前也用于基础奖励、候选集合、未来状态投影，以及存在实现缺陷的商店路径，应按上面的条件理解。
4. **不保证未显示的事件选项没有效果。** 普通适配器会略过 locked、未匹配或无可提取提示的选项，且不自动进入下一页。
5. **不保证未来的牌组、药水、HP、金币或宝箱遗物最终归属。** 尚未选择的输入只能列条件；目前部分条件推进也没有完整建模。

战前面板会排除目标前还有中间战斗或事件的路线，只允许当前路线的首战；中间商店/宝箱/篝火的基线是“沿途状态保持当前值”，另有按当前规则的篝火休息 HP 场景。购买、拿宝箱遗物、锻造或其他触发仍需选择后重算。见 [BuildPointTargets](<D:/FORSTS2/src/UI/PreCombatForecastPanel.cs:331>)。

## 8. 建议后续验收顺序

| 顺序 | 有限验收目标 |
| --- | --- |
| 1 | 普通路线商店 vs 原生 `CreateForNormalMerchant`：逐商品、升级状态、半价、价格及 RNG 终态对比；加入三种蛋、会员卡/快递员组合和连续进店 |
| 2 | 事件准入时点：跨过 TotalFloor 6/7 的固定路径，并加入金币、药水、可移除牌条件；比较预测和原生目标事件 ID |
| 3 | 所有单结果/多结果/Boss 视图统一条件提示；构造超过路线枚举上限的地图，确保不标全路线确定 |
| 4 | 战斗可改写奖励：逃跑金币、TheHunt 额外奖励及奖励 RNG 续用；标签必须指明基础/终局结果 |
| 5 | 事件结果类型与商店分组：候选映射、重抽序列、多页未覆盖、不同库存同进店序号、同 ID 不同升级状态 |

本轮仅交付审查与证据文件，未修改已有业务实现。上述源码反例尚需对应修复后的原生差分/游戏验证，不能由旧“生成非空、自测通过”记录替代。
