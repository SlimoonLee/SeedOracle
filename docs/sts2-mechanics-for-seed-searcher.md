# STS2 机制逆向笔记：搜种器（Seed Searcher）准备

调研日期：2026-10-08。游戏版本：`0.111.0`（源码权威来源：`.reference/sts2-v0.111.0/` 全量反编译源码）。
目标：为"按条件搜索种子"（seed searcher）梳理三块机制的精确算法：**地图生成、卡牌掉落、开局捏奥（Neow）选项**，并给出搜种器架构结论。

---

## 0. 结论摘要（TL;DR）

**种子决定性分层**——每一层内容的决定因素与"是否需要模拟战斗"：

| 内容 | 决定因素 | 需要战斗模拟？ |
|---|---|---|
| 捏奥（Neow）选项 | `seed + playerSlot`（完全独立的事件 RNG） | 否 |
| 每幕地图（拓扑+节点类型） | `seed + 幂数 + 多人 + 飞升(A1)`，专用 `act_N_map` RNG | 否 |
| Boss / 先古身份 / 遭遇队列 | `UpFront` 流（纯种子派生） | 否 |
| 遭遇怪物阵容 / HP | `seed + TotalFloor + hash(encounterId)`；HP 用 `Niche` 流 | 否 |
| `?` 房间类型 | `UnknownMapPoint` 流 + 动态赔率 | 否 |
| 战斗奖励（金币/药水/卡牌/遗物） | `PlayerRng.Rewards` 顺序流（与打法无关，GoldProportion 除外） | 否 |
| 商店 / 宝箱 | `Shops` / `Rewards` / `TreasureRoomRelics` 流 + 遗物 deque | 否 |
| 事件内容与选项 | 事件本地 Rng = `seed(+slot) + XxHash64(eventId)` | 否 |
| 战斗胜负 / 战损 | 九条战斗 RNG 流 | **是**（CombatSolver 注入式 rollout） |

**核心结论**：搜种器的第一阶段可以是**纯托管 RNG 重放过滤器**——不启动游戏/Godot、不模拟战斗，只从种子推 RNG 流即可过滤"捏奥选项 / 地图结构 / Boss / 先古 / 遭遇队列 / 掉落序列"六大类条件。这比无头引擎单局 ~2 分钟快几个数量级。第二阶段才把候选种子交给 `sts2-cli` / CombatSolver 做可打性验证。

所有流共享同一底层 PRNG：**Xoshiro256\*\***（Splitmix64 播种），字符串种子经 **XxHash64** 哈希。移植搜种器只需 `System.IO.Hashing`（或自移植 XxHash64）+ ~300 行核心算法转写。

---

## 1. RNG 体系总览

### 1.1 种子格式与主种子
- 种子字符串：12 字符，字符表 `0-9A-Z` 去 `O`/`I`（O→0、I→1 规范化），含坏词检查（`Helpers/SeedHelper.cs:12-43`）。
- **RunSeed** = `StringHelper.GetDeterministicHashCode(seedString)` = `XxHash64.HashToUInt64(UTF8(bytes), seed: 0)`（`Helpers/StringHelper.cs:139-152`；`Runs/RunRngSet.cs:104-122`）。
- `old` 前缀种子走旧哈希 `GetDeterministicHashCodeOld`（FNV 风格，`StringHelper.cs:157-170`）——移植时要特判。
- 命名派生：`new Rng(ulong seed, string name)` = `seed + GetDeterministicHashCode(name)`，**ulong 加法允许回绕**（`Random/Rng.cs:54-57`）。

### 1.2 流划分
- **Run 级 12 条**（`Runs/RunRngType.cs:3-17`，`Runs/RunRngSet.cs:104-143`）：`UpFront`（开局队列/遗物袋/先古/Boss 洗牌）、`Shuffle`、`UnknownMapPoint`、`CombatCardGeneration`、`CombatPotionGeneration`、`CombatCardSelection`、`CombatEnergyCosts`、`CombatTargets`、`MonsterAi`、`Niche`、`CombatOrbs`、`TreasureRoomRelics`。每条 = `RunSeed + XxHash64(SnakeCase(type))`。
- **Player 级 3 条**（`Entities.Players/Player.cs:328-332` + `Random/PlayerRngSet.cs:19-47`）：`Rewards` / `Shops` / `Transformations`。玩家流基种子 = `XxHash64(seedString) + playerSlotIndex`；每条再 `+ XxHash64(SnakeCase(name))`（如 `"rewards"`、`"combat_potion_generation"`）。
- **内容级**（事件/地图）：`Rng(RunSeed(+slot) + XxHash64(内容Id))`。

### 1.3 底层算法（`Random/MegaRandom.cs`，Xoshiro256\*\*）
- 播种：Splitmix64 连续 4 次生成 s0..s3（`MegaRandom.cs:87-105`）。
- `NextDouble()` = `(NextULongInner() >> 11) * 2^-53`（:234-237）；`NextFloat()` = `(float)(NextULongInner() >> 40) * 5.9604645E-08f`（:291-294）；`NextInt(min,max)` = `min + (int)(NextDouble()*range)`（:296-304）；`NextBool()` = `Next(2)==0`；`NextItem` = `NextInt(0, count)`。
- **Box-Muller 陷阱**：`NextGaussianInt` 用 **Sin**、`NextGaussianDouble` 用 **Cos**（`Rng.cs:275` vs `:244`），均为拒绝采样，每次尝试消耗 2 个 `NextDouble`。
- `Rng._counter` 只用于存档校验，不影响随机序列。
- 洗牌（`Extensions/ListExtensions.cs:22-60`）：
  - `UnstableShuffle`：Fisher-Yates，从尾向头 `NextInt(i+1)`，**依赖输入顺序**，N 个元素消耗 N-1 次抽取。
  - `StableShuffle`：先按 `MapPoint.CompareTo`（`(col,row)` 元组序）Sort 再 UnstableShuffle——**输出与输入顺序无关**。

---

## 2. 地图生成机制

### 2.1 入口与依赖（关键：地图不依赖角色、不依赖其他 RNG 状态）

```csharp
// MegaCrit.Sts2.Core.Map/StandardActMap.cs:111-114
public static StandardActMap CreateFor(RunState runState, bool replaceTreasureWithElites)
    => new StandardActMap(new Rng(runState.Rng.Seed, $"act_{runState.CurrentActIndex + 1}_map"),
        runState.Act, runState.Players.Count > 1, replaceTreasureWithElites, runState.Act.HasSecondBoss);
```

**mapRng = RunSeed + XxHash64("act_{N+1}_map")**（N 为 1-based 幕序号）。构造依赖仅 6 个标量：幕模型（房间数+节点数表）、是否多人、宝箱换精英（生产恒 false，见 §2.6）、双 Boss、countsOverride（仅 BigGameHunter）、enablePruning（恒 true）。

### 2.2 网格与路径
- 网格 `MapPoint[7, rooms+1]`，**列数恒为 7**。各幕 `BaseNumberOfRooms`：Overgrowth 15、Underdocks 15、Hive 14、Glory 13；**多人 −1**（`Models/ActModel.cs:261-269`）。Grid row 0 恒空；起点 `StartingMapPoint=(3,0)`、Boss `=(3, rows)` 不入 Grid。
- **7 条路径**（`StandardActMap.cs:17, 207-234`）：每条从 row 1 随机列 `NextInt(0,7)` 出发；**只有第 2 条路径（i==1）强制起点不重复**（重摇直到不撞已有起点），i=2..6 允许共享起点。末行所有点连 Boss；row 1 所有点连起点。
- 每步列偏移（`GenerateNextCoord`，:145-182）：候选 `[-1,0,1]` 一次 `StableShuffle(mapRng)`（**固定消耗 2 次 NextInt**，与是否被拒无关），按序尝试并夹紧到 [0,6]；`HasInvalidCrossover`（:184-205）拒绝对向边（交叉）；全失败抛异常。

### 2.3 节点类型分配
类型枚举 9 值：`Unassigned, Unknown, Shop, Treasure, RestSite, Monster, Elite, Boss, Ancient`。

**数量先掷**（`ActModel.GetMapPointTypes(mapRng)`，在构造最早期、路径生成**之前**消耗 mapRng）：

| 幕 | Rest 数 | Unknown 数 |
|---|---|---|
| Overgrowth / Underdocks | `NextGaussianInt(7,1,6,7)` | `NextGaussianInt(12,1,10,14)` |
| Hive | `NextGaussianInt(6,1,6,7)` | Standard−1 |
| Glory | `NextInt(5,7)`（均匀） | Standard−1 |

Shop 恒 3；Elite = 5，**飞升 A1（SwarmingElites）→ round(5×1.6)=8**（`Map/MapPointTypeCounts.cs:14`）。

**固定行规则**（`AssignPointTypes`，:248-307，严格按序，强制赋值不消耗 RNG）：
1. 最上行（Boss 前一行）全部 RestSite；
2. **宝箱行 = GetRowCount()−7**（倒数第 7 行）全部 Treasure（Warden 开关则 Elite）；
3. row 1 全部 Monster；
4. 剩余点按 Rest→Shop×3→Elite→Unknown 队列分配：**最多 3 轮**，每轮把全部 Unassigned 点（col-major 枚举）`StableShuffle`（每轮 1 次）后逐个 `GetNextValidPointType`；无效则回队尾；
5. 仍 Unassigned → Monster；Boss/Starting(Ancient)/SecondBoss 固定。

**有效性规则**（:405-485，搜种器剪枝可直接利用）：
- row < 6 不允许 Rest/Elite（火堆/精英只在上半区）；
- 最上 3 行不允许 Rest（强制火堆行除外）；
- Elite/Rest/Treasure/Shop 的 parent 或 child 不得同类型；
- Rest/Monster/Unknown/Elite/Shop 互不能是 sibling（同 parent 的其它 child）。

### 2.4 剪枝与后处理（顺序敏感）
构造内顺序（:100-108）：`GenerateMap → AssignPointTypes → PruneAndRepair → CenterGrid → SpreadAdjacentMapPoints → StraightenPaths`。

- `MapPathPruning.PruneAndRepair`（`Map/MapPathPruning.cs:22-32`）：最多 3 轮 `PruneDuplicateSegments → RepairPrunedPointTypes`。删除"同类型序列"的重复路径段：DFS 枚举全部路径切段，段 key = 端点坐标 + 类型序列；每组重复段先 `UnstableShuffle(rng)`（**消耗 RNG**），先试"删到只剩最后一段"，失败则断开多父节点的一条边。修复按 **Shop→Elite→Rest→Unknown** 顺序清点，不足则把可修改的 Monster 点 `StableShuffle(rng)`（**消耗 RNG**）后补齐，有修复则重新剪枝。
- `MapPostProcessing`（`Map/MapPostProcessing.cs`，全静态、**无 RNG 但改坐标**）：`CenterGrid`（整体左/右移 1 列使居中）、`SpreadAdjacentMapPoints`（逐行贪心拉开间距至不动点）、`StraightenPaths`（单亲单子点向 parent/child 中间对齐）。

### 2.5 飞升与多人
- **A1 SwarmingElites**：唯一影响地图结构的飞升（Elite 5→8）。注意 `MapPointTypeCounts` 通过 `AscensionHelper.HasAscension` 读**全局单例** `RunManager.Instance`——离线重放必须把它参数化。
- **A10 DoubleBoss**：不消耗 mapRng。第二只 Boss 由 `UpFront.NextItem` 另选（`Runs/RunManager.cs:761-765`），地图上 `Boss→SecondBoss` 多一条边、拓扑 RNG 序列与无双 Boss 完全一致。
- **多人**：全场共享一张地图；`Players.Count > 1` 使房间数 −1 → 网格变小 → RNG 消耗序列不同。
- 其它飞升（WearyTraveler/Scarcity/Inflation 等）不影响地图。`StartedWithNeow=false` 时 Act1 起始点类型改为 Monster（`RunManager.cs:823-826`），不消耗 mapRng。

### 2.6 非标准局 Hook（标准搜种器可忽略，但检测到要跳过）
`Hook.ModifyGeneratedMap` 实现者：BigGameHunter（同种子重建 + Elite×2.5 无视规则）、GoldenCompass 遗物（GoldenPathActMap）、SpoilsMap 卡（`"spoils_map"` 种子）、FurCoat 遗物（标记房间）。`replaceTreasureWithElites` 在生产代码恒为 false（`RunManager.cs:820`）。

### 2.7 离线重放最小依赖集（转写清单）
输入：`(seedString|RunSeed, actIndex, isMultiplayer, hasSecondBoss, ascensionLevel)`。
必须转写（文件均在 `.reference/sts2-v0.111.0/` 下）：

| 类 | 需要成员 | 规模 |
|---|---|---|
| `Random/MegaRandom.cs` | Splitmix64、NextULongInner、NextDouble、NextFloat、Next(int)、Next(int,int) | ~80 行 |
| `Random/Rng.cs` | ctor×2、NextInt、NextDouble、NextGaussianInt/Double | ~90 行 |
| `Helpers/StringHelper.cs` | GetDeterministicHashCode（XxHash64）、Old 版 | ~30 行 |
| `Extensions/ListExtensions.cs` | StableShuffle、UnstableShuffle | ~40 行 |
| `Map/MapCoord.cs`、`Map/MapPoint.cs` | CompareTo、构造、AddChildPoint（**勿重写 Equals/GetHashCode**） | ~120 行 |
| `Map/MapPointTypeCounts.cs` | 全部（NumOfElites 飞升参数化） | 47 行 |
| `Models/ActModel.cs` | 仅 GetNumberOfRooms + 四幕硬编码表 + GetMapPointTypes | ~40 行 |
| `Map/ActMap.cs` | startMapPoints、GetAllMapPoints（col-major） | ~60 行 |
| `Map/StandardActMap.cs` | 核心生成/分配/有效性 ~300 行 | |
| `Map/MapPathPruning.cs` | **全部**（377 行） | |
| `Map/MapPostProcessing.cs` | 全部（273 行） | |

**RNG 消费顺序（重放必须逐位一致）**：`GetMapPointTypes(高斯×2) → 7×[起点 NextInt + 每步 StableShuffle(2×NextInt)] → AssignPointTypes(每轮 1 次 StableShuffle, ≤3 轮) → PruneAndRepair(组 UnstableShuffle + 修复 StableShuffle) → 后处理无 RNG`。

**移植大坑**：
1. XxHash64 必须用 System.IO.Hashing 包或自行移植（不是 .NET `GetHashCode`）；
2. `seed + hash(name)` 按 ulong 回绕；
3. StableShuffle 先 Sort（MapCoord 元组序）——Sort+洗牌共同决定消费序列；
4. **.NET `HashSet<T>`/`Dictionary<TKey,TVal>` 按插入序迭代**（删除不打乱剩余顺序）且 MapPoint 未重写 Equals/GetHashCode——跨语言移植必须用"保序集合/插入序列表"模拟，不能用真哈希集合；
5. Sin/Cos 陷阱（§1.3）。

---

## 3. 卡牌掉落机制（战斗奖励）

### 3.1 奖励构成与 RNG 归属
触发链：战斗结束 → `CombatRoom.OfferRoomEndRewards`（`Rooms/CombatRoom.cs:258-271`）→ `RewardsCmd.GenerateForRoomEnd` → `new RewardsSet(player).WithRewardsFromRoom(room)`。

**全部消耗 `PlayerRng.Rewards` 同一条流**（稀有度/药水/金币/升级/遗物稀有度 odds 构造时都绑定 `rng.Rewards`，`Odds/PlayerOddsSet.cs:22-26`）。

各房型的奖励列表（构造顺序）与 Populate 顺序：

| 房型 | 顺序 |
|---|---|
| 普通战斗 | ① 金币（×GoldProportion）→ ② 药水掷骰 → ③ 卡牌奖励×3 张 |
| 精英 | ① 金币 → ② 药水掷骰 → ③ 卡牌奖励×3 张 → ④ 遗物 |
| Boss | ① 金币 → ② 药水掷骰 → ③ 卡牌奖励×3 张，**无遗物** |

- **药水掉率判定在列表构造时**（`Rewards/RewardsSet.cs:229-240`），金币数额与卡牌在之后的 `GenerateWithoutOffering`（:125-147）按列表序 Populate——顺序即 RNG 消耗顺序。
- 末幕 Boss 无奖励（:88-91）。教学局（`NumberOfRuns==0` 的 Ironclad）前 7 场/精英/Boss 奖励写死查表（:258-386）——**元进度输入，搜种器需检测**。
- `Hook.ModifyRewards`：PrayerWheel/DreamCatcher 等可追加卡牌奖励，在主奖励 Populate 之后继续消耗同一流。
- 显示排序无关 RNG：Gold=1、Potion=2、Relic=3、Card=5。

### 3.2 卡牌奖励（`Factories/CardFactory.cs:89-109, 214-261`）
每张卡三步（每张卡选完后加入 blacklist，**三张互不相同**；稀有度逐张独立重掷）：

1. **稀有度**（`Odds/CardRarityOdds.cs`）：战斗奖励走带 pity 的 `Roll(type)`：

   | OddsType | Common | Uncommon | Rare |
   |---|---|---|---|
   | RegularEncounter | 0.60（A7 Scarcity 0.615） | 0.37 | 0.03（0.0149） |
   | EliteEncounter | 0.50（0.549） | 0.40 | 0.10（0.05） |
   | BossEncounter | 0 | 0 | **1.0**（offset 固定 0） |
   | Shop | 0.54（0.585） | 0.37 | 0.09（0.045） |

   掷骰：`NextFloat()`，`< rareOdds+offset` → Rare，`< uncommonOdds+rareOdds+offset` → Uncommon，否则 Common。
   **pity 机制**：offset 初始 **−0.05**，每次非稀有 `+RarityGrowth`（0.01；A7 0.005），上限 0.4；出稀有重置 −0.05。**初始 −0.05 意味着开局前若干张稀有阈值为负（必不出稀有）**。Boss 掷骰用 offset=0 且把 pity 重置 −0.05。pity 状态随存档持久化（`Odds/PlayerOddsSet.cs:28-35`）。
   - 商店用 `RollWithoutChangingFutureOdds(Shop)`（读 offset 不更新）、事件用 `RollWithBaseOdds`（不用 offset 不更新）——都消耗同一流。
2. **选卡**：`rng.NextItem(pool)` = `NextInt(0, n)`。池子 = **单一 `player.Character.CardPool`**（无共享中性池），内容 = 该角色池声明顺序（如 `Models.CardPools/IroncladCardPool.cs:24-115`，**数组顺序固定**）经 Epoch 解锁过滤（`FilterThroughEpochs`，:121-138）后去除 blacklist。PrismaticGem 遗物把所有已解锁角色池并入。
3. **升级**（`CardFactory.cs:283-305`）：`NextFloat()` ≤ `0.25 × CurrentActIndex`（A7 减半 0.125；act0=0%、act1=25%、act2=50%）则升级；**稀有卡恒 0%**；MoltenEgg/FrozenEgg 等 hook 改阈值。商店传 −999999999 → 必不升级**但仍消耗一次 NextFloat**。

### 3.3 药水（`Odds/PotionRewardOdds.cs`）
- 掉率：初始 **0.4**，精英有效概率 **+0.125**；`NextFloat() < value` → 掉且 `value −= 0.1`，否则 `value += 0.1`（**双向 pity**）；WhiteBeastStatue 强制掉且**不消耗 RNG**。
- 种类（`Factories/PotionFactory.cs:76-95`）：`NextFloat()` ≤0.1 Rare / ≤0.35 Uncommon / 否则 Common；`NextItem` 从（角色药水池+共享池，仅已解锁）按稀有度过滤的子集抽取，单次奖励内不重复。共 2 次抽取。

### 3.4 金币（`Models/EncounterModel.cs:64-100`）
- 范围：普通 **10–20**、精英 **35–45**、Boss **100**；A3 Poverty `×0.75` 后截断；普通战斗再 × `GoldProportion`（=1−逃逸敌人数/总敌人数）后 `Math.Round`；最终 `NextInt(min, max+1)`（**两端含**）。
- **唯一非种子决定的输入是 GoldProportion**（战斗结果依赖）；按"全杀"假设即可精确预测。
- 宝箱金币：`NextInt(42,53)`，Poverty ×0.75；**Blitz 模式除外，Blitz 宝箱没有任何金币**（见 §3.6.1）。

### 3.5 遗物（精英奖励 / 宝箱）
- 稀有度 `RelicFactory.RollRarity`：`NextFloat()` <0.5 Common / <0.83 Uncommon / 否则 Rare（**无 pity**）。精英奖励用 `PlayerRng.Rewards`；宝箱每人一个稀有度用 Run 流 `TreasureRoomRelics`。
- 取件 `PullFromFront` **无 RNG**，走 deque 队首；缺档回退链 `Shop→Common→Uncommon→Rare`，仍空给 Circlet；整局不再出现。
- **两个袋子必须分清**（v0.113.0 核实）：
  - 宝箱**只读** `RunState.SharedRelicGrabBag`（共享池 118 件），完全不碰玩家私有袋。
  - 其它所有来源（精英/事件/商店）读 `player.RelicGrabBag`（共享池 + 角色池 8 件），取到后按 ID `SharedRelicGrabBag.Remove`。
  - 后果：宝箱不会掷到角色专属遗物，其它来源会。预测器算宝箱时用共享袋，算精英/商店时用玩家袋。
- **宝箱没有"稀有度外观"这一层**：外观只由 `ActModel.ChestSpineResource` / `ChestSpineSkinNameNormal` 按幕决定，与内容无关；稀有度只有上面那一次 `NextFloat`。
- 宝箱两条流**时序不同**：进房每人掷一次 `TreasureRoomRelics`（稀有度）；开箱才掷 `PlayerRng.Rewards` 得金币。中间插了别的事（拿遗物、开事件）只影响后者。

### 3.6 其它来源速查

| 来源 | 稀有度方式 | RNG 流 |
|---|---|---|
| 商店 | `RollWithoutChangingFutureOdds(Shop)`（Rewards 流）+ 选卡/价格用 **Shops** 流 | Rewards + Shops |
| 商店（Blitz） | 无稀有度掷骰：`WeightedNextItem` 抽 5 项（`PrismaticCardReward`/`RareCardReward`/`UpgradedRareCard` 权重 0.5，其余 1.0，抽过即移除）+ `NextItem` 抽 1 项诅咒（`>= Glam`）；全部免费、可带走 3 件、无删牌位。**条目内容在买下时才掷**（卡牌奖励、遗物稀有度、药水、诅咒都取自同一 `Shops` 流），所以先买哪件会改变后面几件的结果。实测**每一次成功购买都至少推 `Shops` +1**（连“+2 次选择”和满血+最大生命都推），各条目消耗：卡牌奖励 +18、稀有卡牌奖励/棱镜/无色 +9、附魔卡牌奖励 +12、升级稀有卡牌奖励 +10、药水 +4、遗物 +1；`TwoRelics` 还动共享遗物袋、`CardRewardOfEachRarity` 另走 `Rewards` +9 | 仅 Shops（另 TwoRelics→遗物袋、CardRewardOfEachRarity→Rewards、CardTransform→Transformations） |
| 事件卡奖 | `RollWithBaseOdds`（Regular 赔率，不动 pity） | Rewards |
| 变形 | 无稀有度掷骰，同稀有度档抽 | 遗物→`Niche`；战斗内→`Transformations` |
| 战斗内生成（Discovery 等） | 无 | Run 流 `CombatCardGeneration` |
| 宝箱 | 每人 1 次 `RollRarity(TreasureRoomRelics)` + 共享袋队首 | TreasureRoomRelics + Rewards（开箱时） |
| 宝箱（Blitz） | 上面之外**再加** 1 次（单人）/ 2 次（多人）`RollRarity` | 仅 TreasureRoomRelics（无金币） |

### 3.6.1 宝箱的三个特殊规则（v0.113.0）
- **白银熔炉**：`AfterRoomEntered` 计 `TreasureRoom` 实例数，第 1 个空箱、第 2 个起正常；空箱不消耗任何 RNG（`ShouldGenerateTreasure` 拦在掷骰之前）。`?` 滚出来的宝箱同样计数。
- **藏宝图（SpoilsMap）**：只由第 1 幕事件"传说属实"选"拿走地图"获得，`AfterCreated` 把 `SpoilsActIndex` 钉成 1；第 2 幕地图换成沙漏形 `SpoilsActMap`，全路径汇聚中央宝箱。开箱时若当前点带任务标记，`OnQuestComplete` 额外 `GainGold(DynamicVars.Gold=600)`、记 `CompletedQuests`、`RemoveFromDeck`。**任务标记 `MapPoint.Quests` 不存档**（靠 v0.113 新增的 `AfterCardChangedPiles` 在重载后补挂），所以影子克隆只能反查"牌库里有 `SpoilsActIndex == 当前幕` 的 SpoilsMap"。
- **Blitz**：`_roundsLeft=1`；`GameMode.Blitz` 时再追加共享遗物——单人 1 件（并令 `_roundsLeft=2`，即选两轮），多人 2 件；追加件各自掷一次稀有度、不走首次宝箱的 Gorget 特判，且**即使每人补给都被拦下也会追加**。金币侧完全不同：`DoTreasureRoomRewards` 整体被 `GameMode != Blitz` 包住，**Blitz 宝箱不给金币、不结算藏宝图、也完全不推进 `PlayerRng.Rewards`**。只推进 `TreasureRoomRelics`（单人 2 次 / 多人 1+N 次）。


### 3.7 离线预测一场普通战斗奖励的精确步骤
输入：种子+slot、角色+Epoch 解锁、飞升、幕数、pity 两标量、遗物清单（hook）、遗物 deque。
全部在 `PlayerRng.Rewards` 流上：
1. 药水判定 `NextFloat()`（更新 Potion pity ±0.1；Elite +0.125）；
2. 金币 `NextInt(min', max'+1)`（min'/max' 为 round 后范围）；
3. （若掉药水）`NextFloat()`（稀有度）+ `NextInt(0,n)`（选药水）；
4. 卡牌 ×3：`NextFloat()`（稀有度+更新 pity）→ `NextInt(0,poolCount)`（选卡）→ `NextFloat()`（升级，阈值 0.25×act）；
5. （Hook 追加奖励再走 4）；（精英）遗物稀有度 `NextFloat()` + deque 无 RNG 取件。

### 3.8 玩家状态依赖清单
**必须的 live/账号状态**：角色（卡池/药水池）、Epoch 解锁（卡池内容、教学局判定 `NumberOfRuns`/`EpochUnlockCount`）、飞升（A3/A7）、幕数、pity 两标量（可从种子全量模拟，也可从存档直读）、拥有遗物（hook 列表）、遗物 deque（可由 UpFront 全量模拟）。
**纯种子+固定输入可定**：RNG 序列本身、卡池快照、pity 演化、遗物 deque、掉落序列（除 GoldProportion）。

---

## 4. 开局捏奥（Neow）选项机制

### 4.1 是否遇到 Neow：账号解锁布尔
- `StartedWithNeow = UnlockState.IsEpochRevealed<NeowEpoch>()`（`Runs/RunManager.cs:577-582`）。NeowEpoch 属时间线剧情 "The Reopening"，**首次打开时间线界面自动获得、点击槽位揭示**（`Timeline/NTimelineScreen.cs:341-357`）——几乎所有正常账号都为真。多人取所有玩家 UnlockState **并集**（`RunState.cs:350`）。
- **没有 Neow 时**：Act1 起始点类型直接改为 Monster（`RunManager.cs:823-826`），不进 Neow 事件。起始遗物与 Neow 无关、不走 RNG（角色固定自带：Ironclad=BurningBlood 等，`Player.cs:743-745`）。

### 4.2 候选池（`Models.Events/Neow.cs:66-108`）
Neow : AncientEventModel；全部 30 个候选遗物为 `RelicRarity.Ancient`，**等概率、无权重**：
- **正面 14**：ArcaneScroll, BoomingConch, FishingRod, GoldenPearl, Kaleidoscope, LeadPaperweight, LostCoffer, MassiveScroll, NeowsTorment, NewLeaf, PhialHolster, PreciseScissors, ScrollBoxes, WingedBoots
- **诅咒 10**：CursedPearl, DowsingRod, HeftyTablet, LargeCapsule, LeafyPoultice, NeowsBones, NeowsSacrifice, PrecariousShears, SilkenTress, SilverCrucible
- **固定 6（3 对二选一）**：LavaRock↔SmallCapsule；NutritiousOyster↔StoneHumidifier；NeowsTalisman↔Pomander

**IsAllowedAtNeow 条件过滤**（账号状态！）：Kaleidoscope 需全角色解锁（`Kaleidoscope.cs:24-31`）；ScrollBoxes 需该角色卡池 Common≥4 且 Uncommon≥2（`ScrollBoxes.cs:23-30`）；WingedBoots 仅单人、MassiveScroll 仅多人、SilverCrucible 仅单人。

### 4.3 选项生成算法（`Neow.cs:217-301`，已逐行核实）
前提：`RunState.Modifiers.Count == 0`（自定义模式走替换分支，见 §4.6）。

```
① 诅咒池(10, 去 IsAllowed 过滤).NextItem(rng)                → 诅咒 C   [1 次抽取]
② 正面池排除互斥对（见下）；若 C ≠ LargeCapsule:
      NextBool() → true 加 LavaRock / false 加 SmallCapsule   [1 次抽取，LargeCapsule 跳过!]
③ NextBool() → true 加 NutritiousOyster / false 加 StoneHumidifier  [1 次]
④ NextBool() → true 加 NeowsTalisman / false 加 Pomander            [1 次]
⑤ 正面池再过 IsAllowedAtNeow 过滤 → UnstableShuffle(rng) → Take(2)  [N−1 次]
⑥ 返回 [正面A, 正面B, C]  —— 诅咒永远排第 3 位
```

**互斥对**：CursedPearl↔GoldenPearl、HeftyTablet↔ArcaneScroll、LeafyPoultice↔NewLeaf、PrecariousShears↔PreciseScissors、NeowsSacrifice↔{PhialHolster, LostCoffer}。
**关键分叉**：诅咒抽中 **LargeCapsule 时跳过 ②的 NextBool**，后续所有抽取前移一位——预测器必须模拟此分支。

### 4.4 RNG 派生（与地图/队列/奖励完全独立）
`EventModel.BeginEvent`（`Models/EventModel.cs:234`）：

```
neowRng = Rng( RunSeed + playerSlotIndex + XxHash64("NEOW") )
```

Neow `IsShared=false`，单人 slot=0。**即捏奥选项 = f(seedString, slot, 账号解锁标志)，不需要模拟任何跑局内容**，可独立实现、独立验证（与 SeedOracle 现有 `EventQueueSimulation` 同一套派生公式）。

### 4.5 选项执行效果
- 进入任意 Ancient 事件**回满血**（A2 WearyTraveler 治疗量 ×0.8）；选项执行 = `RelicCmd.Obtain` → `AfterObtained()`，无额外 RNG（除 NeowsBones 用 `PlayerRng.Rewards` 洗池、NewLeaf 变形用 `Niche`）。
- 30 个遗物效果速查：正面——ArcaneScroll 加 1 张随机稀有卡；BoomingConch 每回合+2 抽；FishingRod 每 3 战给奖励；GoldenPearl +150 金币；Kaleidoscope 2 个跨角色卡奖；LeadPaperweight 2 张无色卡选 1；LostCoffer 卡奖+药水奖；MassiveScroll 多人限定卡选 1；NeowsTorment 加 NeowsFury 牌；NewLeaf 变形 1 卡；PhialHolster +1 药槽+2 药；PreciseScissors 移除 1 卡；ScrollBoxes 2 组捆绑选 1；WingedBoots 3 次免费移动。诅咒——CursedPearl +333 金币+诅咒 Greed；DowsingRod 加 Dowsing 牌；HeftyTablet 3 稀有选 1+Injury；**LargeCapsule 直接从遗物 deque 抽 2 个遗物**+起始打击/防御各+1；LeafyPoultice −12 MaxHP+变形起始牌；NeowsBones 2 个 Neow 池遗物选 1+随机诅咒；NeowsSacrifice Ambergris 药+Guilty 诅咒牌；PrecariousShears 移除 2 卡+自伤 16；**SilkenTress 清空金币**；SilverCrucible 3 次改写卡奖。固定——LavaRock 之后 2 个宝箱追加遗物；SmallCapsule 立即 1 遗物奖；NutritiousOyster +11 MaxHP；StoneHumidifier 每次营火+5 MaxHP；NeowsTalisman 升级起始打击/防御；Pomander 升级 1 卡（自选）。

### 4.6 边界情况
- **自定义模式（Modifiers 非空）**：整个遗物分支被替换——只展示第一个实现了 `GenerateNeowOption` 的 modifier 选项，**完全不消耗事件 RNG**（`Neow.cs:287-300`）。
- **多人**：每玩家独立实例、独立选项（`Multiplayer.Game/EventSynchronizer.cs:98-124`），各 = `seed + slot + XxHash64("NEOW")`。

### 4.7 其它先古（Ancient）与 Neow 的关系
同一框架：Act2 = Orobas/Pael/Tezcatara、Act3 = Nonupeipe/Tanx/Vakuu、共享 Darv；具体遇到谁由 **UpFront 流**在开局 `GenerateRooms` 洗牌/抽取决定（`RunManager.cs:743-752`、`Models/ActModel.cs:331-386`）。每个 Ancient 自己实现 `GenerateInitialOptions`（结构各异，但都用同一事件 Rng 派生公式，事件 Id 不同），如 Tezcatara 3 池各 NextItem、Nonupeipe/Tanx 条件加选项+UnstableShuffle+Take(3)、Pael 有池复制加权、Orobas `NextFloat()<1/3` 出 PrismaticGem。搜种器按同样模式逐个转写即可。

---

## 5. 对搜种器的架构建议

### 5.1 实现分层
- **Phase 1：纯托管重放过滤器（离线、无游戏依赖）**
  - 依赖仅：.NET + `System.IO.Hashing`（XxHash64）+ §2.7 清单的地图像法 + §1.3 的 RNG 核心 + 各内容表（卡池/遗物池/遭遇表转写）。
  - 过滤维度（可独立开关、逐个递增）：
    1. 捏奥选项（最简单，先做——纯 `(seed, slot)` 函数，秒级可验证）；
    2. 各幕 Boss / 先古身份 / 双 Boss（`UpFront` 流重放）；
    3. 地图结构（节点类型布局、宝箱行位置、路线连通性——固定行规则 §2.3 可做快速预剪枝）；
    4. 遭遇队列（弱怪前 N 场、事件表）；
    5. 奖励序列（沿固定路线推 `Rewards` 流，假设全杀；GoldProportion 按 1 处理）；
    6. 宝箱/商店遗物（`UpFront` 洗的 deque 顺序）。
  - 吞吐：每种子 ~微秒-毫秒级（对比无头引擎单局 ~2 分钟），可在 C# 内并行枚举。
- **Phase 2：候选验证与可打性**
  - 对筛出的种子用 `sts2-cli`（`src/Sts2Headless/RunSimulator.cs:232-285` 的 `StartRun(character, ascension, seed)`，可截断到 `GenerateRooms`/`EnterAct`）做真实引擎对拍；
  - 可打性/战损接 CombatSolver v6 规划模拟（九条战斗 RNG 可注入采样，见 `docs/decision-network-blueprint.md:95-99`）。

### 5.2 必须作为输入参数的账号状态
| 输入 | 影响点 |
|---|---|
| NeowEpoch 是否 Revealed | 是否遇到捏奥；无则 Act1 起点变 Monster |
| 角色 Epoch 解锁（卡池） | 卡池内容、Neow ScrollBoxes 过滤 |
| 是否全角色解锁 | Neow Kaleidoscope 过滤（改变洗牌池大小 → 后续全部位移） |
| `NumberOfRuns` / EpochUnlockCount | 教学局固定奖励分支 |
| 飞升等级 | A1 精英数、A3 金币、A7 赔率、A10 双 Boss |
| 单人/多人 + slot | 地图房间数、RNG 槽位偏移、Neow 人数过滤遗物 |
| Modifiers（自定义模式） | Neow 替换分支（不消耗 RNG）；BigGameHunter 等重建地图 |
| 是否有 GoldenCompass/SpoilsMap/FurCoat | 地图替换/后修（标准搜种器检测到即跳过该种子） |

### 5.3 验证策略
- 复用 `smoke/run-planning-smoke.ps1` 的隔离环境（硬链接游戏目录+独立 APPDATA）与 `src/Validation/`（LiveFingerprint、PredictionPurityGuard）。
- 新增对拍用例：纯种子重放 vs `sts2-cli` 实跑，逐位比对 ①捏奥 3 选项、②三幕地图逐格类型与坐标、③遭遇队列/双 Boss、④沿固定路线的前 K 场奖励。已知坑位优先覆盖：LargeCapsule RNG 分叉、pity 初始 −0.05、StableShuffle 排序语义、ulong 回绕、A1 精英 ×1.6。
- 现成参照：`docs/capability-audit.md:76-90`（已确认游戏行为清单）、`docs/validation/prediction-accuracy-audit-2026-10-07.md`（链路逐环节验证方法）。

### 5.4 已知边界（预测器要显式处理）
- `Rng.Chaotic`（DateTime 播种）仅装饰但破坏确定性比对（`docs/rng-audit-event-execution.md:31-32`）；
- `GoldProportion` 依赖战斗结果（逃逸敌人比例）——按全杀假设或标注为"战斗相关"；
- 多人共享事件（IsShared）预测语义未做；
- 卡牌数值不能手写转写（STS2 与 STS1 大量差异）——战斗内评估必须走引擎导出（`docs/handoff-decision-network.md:129`）；
- BossEncounter 掷骰 offset=0 但仍重置 pity；商店升级掷骰必败但消耗流——这些"消耗但不生效"的调用漏一个就全程错位。

---

## 外部参考
- [Slay the Spire 2 Wiki – Seeds](https://slaythespire-2.com)（确认种子决定地图/掉落/遭遇，与逆向结论一致）
- [Slay the Spire Wiki (wiki.gg) – Neow](https://slaythespire.wiki.gg/wiki)（Neow 3 选项的外部交叉验证）
- STS1 时代同类工具（架构参照）：[ForgottenArbiter/SeedSearch](https://github.com/ForgottenArbiter/SeedSearch)、[Unwinnable seed 分析](https://oohbleh.github.io)

---

## 附：捏奥选项执行效果的流消耗（2026-10-08 引擎对拍验证）

选项**生成**用独立事件 Rng（`seed+slot+XxHash64("NEOW")`）；选项**选中后**的执行效果消耗下列流，
并相应位移后续所有依赖这些流的序列（Rewards=卡奖/药水/金币/遗物稀有度；Niche=怪物HP/变形/诅咒；
Transformations=后续变形；CombatPotionGeneration=战斗药水生成）：

| 选项 | 消耗 | 说明 |
|---|---|---|
| NewLeaf（变化一张牌） | Niche ×1 | 玩家选 1 张牌 → `CardCmd.TransformToRandom(·, Rng.Niche)`；变形池=角色池∩{C,U,R}，每张 1 次 NextItem |
| LeafyPoultice（变化两张牌） | Transformations ×2 | 自动变形起始打击/防御，`CardCmd.Transform(·, PlayerRng.Transformations)` |
| NeowsBones（随机2遗物+诅咒） | Rewards ×27 + Niche ×1 | 28 项候选池（30 − MassiveScroll单人剔除 − 自身）Fisher-Yates 洗牌 27 抽 → 前 2 供选；诅咒从按 Id 排序的诅咒池 Niche 抽 1；**二选一选中后按该遗物表级联消耗**（实测选 LeadPaperweight +6，总 33，counter=33 写入存档） |
| HeftyTablet（3稀有卡+诅咒牌） | Rewards ×3 | Uniform+Rare 谓词+NoUpgradeRoll=每张 1 抽；Injury 无 RNG |
| ArcaneScroll（随机稀有卡） | Rewards ×1 | 同上（1 张） |
| Kaleidoscope（2跨角色卡奖） | Niche ×4 + Rewards ×6 | 其余 5 角色池 StableShuffle + 每张 [RollWithBaseOdds+选卡+升级掷骰]（pity 不动） |
| ScrollBoxes（2捆绑） | Rewards ×6 | 每捆绑 2 普通+1 罕见（Uniform，Defect 1% 三爪） |
| LeadPaperweight（2无色选1） | Rewards ×6 | 无色池 RegularEncounter × 2 |
| MassiveScroll（3多人卡选1） | Rewards ×3 | |
| LostCoffer（卡奖+药水奖） | Rewards ×11 | 卡奖 3×3 + 药水奖 2（PotionReward 默认绑 Rewards） |
| PhialHolster（+1槽+2药水） | CombatPotionGeneration ×4 | 注意走的是 Run 级药水生成流，不是 Rewards |
| SmallCapsule（1随机遗物） | Rewards ×1 + 袋首取件 | |
| LargeCapsule（2随机遗物+打击防御） | Rewards ×2 + 袋首取件 ×2 | |
| 其余（金币/HP/升级/被动/移除/加固定牌/诅咒牌） | 零消耗 | GoldenPearl/SilkenTress/CursedPearl/DowsingRod/NeowsTorment/PreciseScissors/PrecariousShears/NeowsSacrifice/NeowsTalisman/Pomander/WingedBoots/BoomingConch/FishingRod/LavaRock/StoneHumidifier/NutritiousOyster/SilverCrucible |

**引擎对拍（sts2-cli 选选项→读存档玩家流 Xoshiro 状态）**：GoldenPearl 0 抽 ✓、HeftyTablet 3 抽 ✓、
LostCoffer 11 抽 ✓、NeowsBones 27 抽 ✓（+级联 LeadPaperweight 6 抽=33，存档 counter 证实）✓、
LeafyPoultice Transformations 2 抽 ✓。惩罚项（ curses/Injury/Guilty 等加牌）与玩家选择类
（移除/升级/变形选牌）不消耗随机流本身。

对搜种器：`neowChoice` 配置（0/1/2）会在路线重放前应用对应选项的流消耗；
不设置则不模拟（保守）。玩家在选择后还需再做的决策（变形选哪张、拿哪个遗物）会带来分支，
搜种器按"级联遗物表自动消耗、玩家决策不消耗"建模。
