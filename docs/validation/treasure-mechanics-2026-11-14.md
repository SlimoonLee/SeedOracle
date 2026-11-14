# 0.1.52 宝箱机制现场核实（藏宝图 / Blitz）

> 0.1.53 的极速商店结论沿用本文第 5 节的同一批反编译，0.1.54 追加了逐条目的抽取时机与"影子买一次"的预测方式。

## 证据来源

游戏 0.113.0，`D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll`，用 `ilspycmd 11.0.0.9375` 现场反编译，产物在 `.smoke/live-decomp/`。**没有**沿用 `.reference/sts2-v0.111.0` 的源码树——0.113 新增了 Blitz 分支和 `AfterCardChangedPiles`，照 0.111 读会得出相反结论（本文第 3 节就是一次实例）。

## 1. 宝箱没有"稀有度外观"

`ActModel.ChestSpineResource` / `ChestSpineSkinNameNormal` 按幕决定外观，与内容无关。全流程只有一次稀有度判定：`RelicFactory.RollRarity`（`MegaCrit.Sts2.Core.Factories.RelicFactory.decompiled.cs:118`）→ `NextFloat()` <0.5 Common / <0.83 Uncommon / 否则 Rare，**无 pity**。

## 2. 两条流、两个时间点

| 时点 | 内容 | RNG |
|---|---|---|
| 进房 `BeginRelicPicking` | 每人 1 件遗物（`ShouldGenerateTreasure` 决定是否给） | Run 流 `TreasureRoomRelics`，每人 1 次 `NextFloat` |
| 开箱 `DoTreasureRoomRewards` | 金币 42–52，A3 Poverty ×0.75，再结算藏宝图 | `PlayerRng.Rewards.NextInt(42,53)` |

`TreasureRoomRelicSynchronizer.decompiled.cs:113-114`：稀有度掷骰 + `_sharedGrabBag.PullFromFront`。
`OneOffSynchronizer.decompiled.cs:79-99`：金币与藏宝图。

两段之间插别的事（拿遗物、开事件、拿金币）只影响后者，不影响前者。

## 3. 双袋模型（本节是 0.1.51 文档措辞错误的更正）

- 宝箱**只读** `RunState.SharedRelicGrabBag`（共享池 118 件）：`TreasureRoomRelicSynchronizer` 的字段 `_sharedGrabBag` 就是它，全程没有引用玩家私有袋。
- 其它所有来源走 `RelicFactory.PullNextRelicFromFront(player, …)`：读 `player.RelicGrabBag`（共享池 + 角色池 8 件），取到后 `player.RunState.SharedRelicGrabBag.Remove(relicModel)`（`RelicFactory.decompiled.cs:63`）。
- 后果：宝箱掷不到角色专属遗物，精英/事件/商店能掷到；两者共用同一份共享池，所以互相"烧件"。
- 取件本身无 RNG，走 deque 队首；缺档回退 `Shop→Common→Uncommon→Rare`；仍空给 Circlet。共享袋 `refreshAllowed=true`，抽空后按模型声明顺序重填（不重洗，`RelicGrabBag.decompiled.cs:222`）。

## 4. 藏宝图（SpoilsMap）

`MegaCrit.Sts2.Core.Models.Cards.SpoilsMap.decompiled.cs`：

- `AfterCreated` 固定 `SpoilsActIndex = 1`（`:46`）；只有 `TheLegendsWereTrue` 会给它，而该事件 `IsAllowed` 限定 `CurrentActIndex == 0`，所以它只可能在"下幕"生效。
- `ModifyGeneratedMap` 在该幕把地图换成 `SpoilsActMap`（沙漏形，全路径汇聚中央宝箱，`SpoilsActMap.cs:138-147`）。
- `AfterMapGenerated` 给该点 `AddQuest(this)`（`:95`）；0.113 新增 `AfterCardChangedPiles`（`:128`）在卡回到牌库时补挂——这是任务标记能在读档后存活的原因。
- 结算：`OneOffSynchronizer.TryHandleSpoilsMap`（`:104`）先要求 `CurrentMapCoord` 上的点 `Quests` 含 SpoilsMap，然后对牌库里**每一张** SpoilsMap 调 `OnQuestComplete`：`GainGold(DynamicVars.Gold=600)` → `CompleteQuest` → `RemoveFromDeck`（`:150-155`）。

**给预测器的关键结论**：`MapPoint.Quests` 不进存档（`MapPoint` 的 `_quests` 没有序列化，`SerializableActMap` 只存拓扑），影子克隆必然读到空列表。所以影子里只能用"牌库里有 `SpoilsActIndex == 当前幕` 的 SpoilsMap"反推，并且必须要求目标是 `MapPointType.Treasure`——`?` 滚出来的宝箱走的不是任务点，不带标记。

`SpoilsCoord` 无 `[SavedProperty]`，读档后为 null；它只影响 `BeforeCardRemoved` 的摘标记，不影响结算判定。

## 5. Blitz（0.113 新增）

`GameMode` 新增 `Blitz`（`MegaCrit.Sts2.Core.Runs.GameMode.decompiled.cs`）。宝箱两处不同：

**遗物**（`TreasureRoomRelicSynchronizer.decompiled.cs:117-137`）

```
_roundsLeft = 1;
if (GameMode == Blitz) {
    extra = Players.Count > 1 ? 2 : 1;
    if (single player) _roundsLeft = 2;
    for (i < extra) { RollRarity(_rng); _sharedGrabBag.PullFromFront(...) ?? Circlet; }
}
```

- 单人：2 件遗物、2 次稀有度掷骰、选两轮（第一轮选完剩下的还能再选）。
- 多人：每人 1 件 + 共享追加 2 件，仍只选一轮。
- 追加件不走 `TryGetRelicForTutorial` 的 Gorget 特判。
- 追加发生在 `ShouldGenerateTreasure` 循环**之后**且不受其约束：即使每人补给都被拦下（白银熔炉首箱），Blitz 仍会追加。所以空箱判定必须按追加后的数量算。

**金币**（`OneOffSynchronizer.decompiled.cs:79-99`）

```
if (!ShouldGenerateTreasure(...)) return 0;
if (GameMode != Blitz) {
    gold = NextInt(42, 53); Poverty ×0.75; GainGold; gold += TryHandleSpoilsMap(...);
}
return gold;
```

Blitz 宝箱**不给金币、不结算藏宝图、完全不推进 `PlayerRng.Rewards`**。0.1.51 及更早的实现对 Blitz 会多掷一次 42–52，把 Rewards 流推前一步，属于实打实的流错位。

**商店**（`MerchantInventory.decompiled.cs`）

`CreateForBlitzMerchant`（`:132`）用 `GetBlitzEntries`（`:209`）从 `PlayerRng.Shops` 抽 5 项加权选品 + 1 项诅咒，`Setup` 置 `BlitzChoicesRemaining = 3`（`:154`），商品全部 `Cost => 0`，没有删牌位（`CardRemovalEntry = null`）。和标准商店的 `GetDefaultRelicEntries`（2 次 `RollRarity(Shops)`）消耗完全不同。

**货物本身可枚举**（`BlitzMerchantEntry.decompiled.cs`）：`Title`/`Description` 是 `merchant_room` 表里的 `MERCHANT.blitz.<snake>_entry.title|description`（`:57`/`:59`）；`IsCursed` 是 `MerchantEntryType >= Glam`（`:53`），`IsRare` 是命中 `RareBlitzEntries`（`:55`，`PrismaticCardReward`/`RareCardReward`/`UpgradedRareCard`）；`IsStocked` 等价 `Status == Available`（`:110`）。`Glam`（`:23`/`:33`）、`CardEnchant`、`PrismaticCardReward` 用子类覆写补占位符，所以 `GetFormattedText()` 的结果与图标悬停文字一致。

**买下后给什么也基本可算**（0.1.54 逐条核实 `OnTrySelect`）：内容在买下那一刻才掷，所以"先买它"是一个确定答案。逐类：

| 条目 | 内容何时掷 | 需要你选牌 |
|---|---|---|
| `CardUpgrade` / `CardRemoval` | 无 RNG | 是（选完才动手） |
| `CardTransform` | 选牌后 `PlayerRng.Transformations` | 是 |
| `CardEnchant` | **生成货架时**（构造器里 `Shops.NextItem` 选附魔） | 是（附魔已定） |
| `Glam` | 选牌后 `Shops.NextItem` 抽诅咒 | 是 |
| `RandomUpgrades` | `Shops.StableShuffle` 取 2 张可升级牌 | 否 |
| `CardRewards` | `Shops`（`CardRarityOddsType.Blitz`），**两组**三选一 | 否 |
| `RareCardReward` / `ColorlessCardReward` / `PrismaticCardReward` | `Shops` | 否 |
| `EnchantedCardReward` | `Shops`（卡一次、每张附魔再一次） | 否 |
| `UpgradedCardReward` / `UpgradedRareCardReward` | `Shops`，`AfterGenerated` 里统一升级 | 否 |
| `CardRewardOfEachRarity` | 无稀有度掷骰（均匀档三组），诅咒走 `Shops` | 否 |
| `UpgradedRareCard` | `Shops`，**直接进牌库** | 否 |
| `Relic` / `TwoRelics` | `Shops.RollRarity` + 袋尾取件（`PullNextRelicFromBack`） | 否 |
| `Potions` | `Shops` ×2（`PotionReward.SetRng`），另 +1 药水栏位 | 否 |
| `TwoMoreChoices` | 无（诅咒走 `Shops`） | 否 |
| `FullHealAndMaxHp` | 无（+6 最大生命、回满，诅咒走 `Shops`） | 否 |

`TwoMoreChoices` 走 `RunManager.Instance.MerchantSynchronizer.GetInventoryFor(_player)`，而该查询按**槽位**索引真实库存——影子玩家会改到真店的次数。0.1.54 起 `MerchantSynchronizer.GetInventoryFor` 在影子作用域内返回影子库存。

预测方式不是复写抽取规则，而是**在影子克隆上真的买一次**：快照当前状态 → 恢复 → 复制条目（见下）→ 走 `OnTryPurchaseUnsynchronized` → 用 `ShadowIsolation.UseRewardHandler` 拦下 `RewardsSet` → `GenerateWithoutOffering()` 让原生 `Populate` 把牌/遗物/药水掷出来，只记录不领取。牌库变化按 `(id, 是否升级)` 计数差分成"新进牌"与"被升级"。

**条目要复制，不能重新生成**：`BlitzMerchantEntry.Generate` 会走构造器，而 `CardEnchant`（`:42` 附近 `Shops.NextItem(shopEnchantmentOptions)`）和 `PrismaticCardReward`（`Shops.NextItem(characters)`）的构造器自己就掷一次。重新生成既会挑到不同的附魔/角色，又会把随后要读的流推走一格。0.1.55 改成 `MemberwiseClone` 浅拷贝 + 反射把 `_player` 换成克隆玩家 + 清空 `PurchaseCompleted`/`PurchaseFailed`/`EntryUpdated` 三个事件字段（否则探针的一次购买会去动真实库存的 `BlitzChoicesRemaining`）。用假库存 `new MerchantInventory(player, [],[],[],[], null, [replica])` + `Setup()` 承接购买事件。

**商店内的浮窗**（0.1.55）：`NBlitzMerchantSlot.CreateHoverTip`（`:184-187`）用 `_blitzEntry.HoverTips` 建提示，所以在 `BlitzMerchantEntry.HoverTips` getter 上加 postfix 即可把预测并进原生提示——用 `HoverTip(LocString title, string description)` 重建第一条，标题沿用 `entry.Title`，描述是原生文本加 `\n` 加收益行。缓存按 `BlitzChoicesRemaining` 失效，买过一件后剩余货物整体重算。

**战斗奖励不需要额外适配**：`RewardsSet.WithRewardsFromRoom` 在 `:89` 调 `Hook.TryReplaceDefaultRoomRewards`，`BlitzRunModel.TryReplaceDefaultRoomRewards`（`:90`）/`TryModifyCardRewardOptionsLate`（`:46`）经 hook 监听器生效。影子走的就是 `RewardsSet`，所以 Blitz 的定稀有度 + 自动升级奖励是原生带出来的。

## 6. 白银熔炉

`SilverCrucible.ShouldGenerateTreasure` 返回 `TreasureRoomsEntered > 1`（`:146`），计数在 `AfterRoomEntered` 自增（`:135`）。空箱在掷骰之前返回，**不消耗任何 RNG**。`?` 滚出来的 `TreasureRoom` 同样计数。

## 7. 本轮改动与覆盖

`src/Forecasting/PlanningPredictionService.cs`

- `GenerateTreasure(state, isPriorRoom, mapPoint)`：新增任务点入参；Blitz 追加遗物；Blitz 跳过整段金币；藏宝图 +600 并 `RemoveFromDeck`；中转宝箱按实际件数从私有袋扣除（原实现写死 1 件）。
- `IsSpoilsChest` / `CompleteSpoilsMapQuests`：点标记优先，影子退化为"牌库 + 幕号"。
- `AdvanceRoomsBeforeTarget` / `AdvanceRoom`：新增可选坐标，供中转宝箱识别任务点。
- `PlanningMerchantSession` 按 `GameMode` 选 `CreateForBlitzMerchant`。（0.1.52 时 `DescribeMerchant` 对 Blitz 商店返回空库存并写"未建模"；0.1.53 改为 `DescribeBlitzEntries` 逐条读 `Title`/`Description`，见下。）
- 调用点补坐标：`MapForecastService`、`RoutePlanForecastService`、`MerchantChoiceBranchService`（`PredictTreasure`/`ExpandTreasure`/跳过宝箱分支）、`RandomForeseerRewardAdapter.PredictTreasureRoom`。

`src/Validation/TreasureModeRegressionTests.cs`（`-SelfTest` 内执行）

- 标准宝箱：1 次稀有度掷骰、1 件遗物（对照基线）。
- 藏宝图：任务点 +600 且卡离库；同影子第二箱不重复付；`?` 点不付且卡留库；异幕卡不付。
- Blitz：单人 2 件遗物、2 次稀有度掷骰、金币 0、`Rewards.counter` 不变。
- Blitz 商店（0.1.53）：6 件货物、可带走 3 件、标题非空、含 1 项诅咒、不出标准四栏；同一份 `Shops` 前置状态连开两次货架必须一致（快照取自生成前，否则等于拿已经走过的流去复现）；浮窗含件数与货物标题。报告里同时打印渲染后的每一行，用于核对本地化与排版。
- Blitz 收益（0.1.54）：6 项全部可预测、每组卡牌奖励恰好 3 张、需要选牌的项与可选项数自洽、浮窗含 `→` 行。实测 `screens=2 relics=1 potions=2 gained=1 decks=2 probe_ms=23`（6 次探针共 23 ms）。
- 店内悬浮（0.1.55）：6 项都有预测、同一件连问两次一致、合成后仍是一个提示且保留原生标题与描述。`blitz-hover` 行打印到报告里。
- 顺序机制（0.1.58）：逐条目实测购买前后各流计数器（`blitz-stream` 行）。结论：**每次成功购买至少推 `Shops` +1**，条目间消耗差异很大（卡牌奖励 +18、稀有/棱镜/无色 +9、附魔 +12、升级稀有 +10、药水 +4、遗物/+2 次/满血 +1）；`TwoRelics` 额外动共享遗物袋；`CardRewardOfEachRarity` 还走 `Rewards` +9。需要选牌的五类（升级/变化/删牌/附魔/Glam）在被拒选时**完全不消耗**。两个“看起来固定”的条目其实都掷诅咒：`TwoMoreChoices` 与 `FullHealAndMaxHp` 的 `OnTrySelect` 都先 `GiveCurse()`（诅咒取自 `Shops`）——所以它们也随顺序变。
- 顺序预测（0.1.58）：`BlitzMerchantProbe.PredictAfter(state, first, second)` 在同一克隆上先买 `first` 再买 `second`；`BlitzShopYield.Afterwards` 对悬浮的那件逐个枚举“其余可用货物”，按渲染结果分组，只列**真的不同**的：`→ 先买 药水 后：…`；全部相同时写 `→ 先买哪一件都不会改变这一项`。被拒选的先行件跳过（它的流没动，无从断言）。整架 6 件的悬浮预测共 45 ms（`blitz-hover-ms`）。自检 `blitz-order` 断言：卡牌奖励/满血先行都会改变药水；+2 次选择件先行后药水也变；满血与 +2 次选择件自身的诅咒随先行件变。
- 变化预测（0.1.57）：`CardTransform` 走 `CardCmd.TransformToRandom(card, PlayerRng.Transformations)`（`:26-35`），单次抽取；选项由 `CardFactory.GetDefaultTransformationOptions` 给出——本角色卡池、稀有度落在 2–4、`FilterForRun` 之后再排除来源牌本身（`CardFactory:190-212`）。因为每次都是同一 RNG 位置、同一长度，`NextItem` 选中的下标对所有候选相同，所以同一池子的牌基本落到同一张，最多差一位。自检用 `CardFactory.CreateRandomCardForTransform` 逐张算，并**真买一次**做 A/B（首个与最后一个候选），落地牌必须与表内一致。`blitz-transform` 行打印卡片数、结果数与被选中的结果。
- 店内悬浮链路（0.1.56）：`NBlitzMerchantSlot.CreateHoverTip`（`:184-187`）在鼠标进入 / 聚焦时调用（`NMerchantSlot` `:385`），`_blitzEntry.HoverTips`（`BlitzMerchantEntry` `:95-106`，非虚属性，直调可 patch）是唯一来源；货架由 `NMerchantInventory.Refresh` 按 `BlitzEntries` 下标逐格 `FillSlot`（`:385-390`），`NMerchantInventory.Inventory` 就是同步器给出的那份库存（`NMerchantRoom` `:169` `GetLocalInventory()`）。因此槽位落位那一刻可以从节点树拿到库存，不必再从 `MerchantSynchronizer` 反查。自检同时断言两个补丁经 `Harmony.GetPatchInfo` 确实挂在 `BlitzMerchantEntry.get_HoverTips` 与 `NBlitzMerchantSlot.FillSlot` 上，以及影子条目悬浮时原生描述逐字不变。

## 8. 已知未覆盖

- Blitz 商店里还需要选牌的三类（升级 / 删牌 / 附魔、Glam）只报数量不报结果：克隆不能替玩家选目标。这三类的结果是确定的（升级就是原牌 +），列出来只是噪声；变化已由 0.1.57 覆盖。
- 收益按“这一项是下一件买的”推算。买过一件之后，后续项的结果会随 `Shops` 流前移而改变，浮窗不枚举 `6×5×4` 种购买顺序。
- 规划面板遇到极速商店只列货架并注明"需进店后逐项选择，规划不代选"，不推演购买与删牌（Blitz 没有删牌位，`CardRemovalEntry = null`）。
- 中转宝箱的 42–52 金币仍不进规划账本（`AdvanceRoom` 只消耗 RNG，不改 `Player.Gold`），与 0.1.51 一致；商店库存不依赖金币，影响仅限"能不能买"的展示。
- 多人宝箱投票（`AwardRelics` 的石头剪刀布、安慰奖）仍未枚举，沿用原有"多人投票尚未枚举"提示。
