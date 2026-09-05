# 事件选项执行 RNG 风险审计（M3 前置）

审计基线：2026-09-06，游戏 v0.111.0（`.reference/sts2-v0.111.0`），Random Foreseer 0.13.10。
结论：**存在干净的模型级执行路径，可以安全执行影子跑局上的事件选项**，但必须落实 4 项围栏
（NetId 改写、NonInteractiveMode 包裹、选择器注入、黑名单），详见下文。

## 1. 执行路径（反编译确认）

正式流程：`NEventOptionButton` → `NEventRoom.OptionButtonClicked` →（共享事件走
`EventSynchronizer` 投票/网络消息）→ `EventOption.Chosen()`（`Events/EventOption.cs:174`）→
各事件 `GenerateInitialOptions` 里构建的 `OnChosen` 闭包 → `*Cmd` 静态帮助类。

**绕开 UI/命令管线的模型级路径存在且就是同步器做的事去掉消息**：
1. `ModelDb.Event<T>()`（或 `SaveUtil.EventOrDeprecated(id)`）→ `ToMutable()`；
2. `await ev.BeginEvent(shadowPlayer, combatSynchronizer: null, isPreFinished: false)`
   ——确定性播种 `ev.Rng`（`EventModel.cs:234`），跑 `CalculateVars`、生成 `CurrentOptions`；
3. `await ev.CurrentOptions[i].Chosen()`；多页事件每步后重读 `CurrentOptions`（次级选项机制
   = `SetEventState` 替换选项列表），至 `IsFinished`。
不经过任何 `*Cmd` 命令/网络消息——事件没有专用 Command 类，管线只有 3 个网络消息且都在
UI/同步器层。

## 2. 事件 RNG 方案（已确认）

`EventModel.BeginEvent`（EventModel.cs:234）：
`Rng = new Rng(RunState.Rng.Seed + (IsShared ? 0 : playerSlotIndex) + XxHash64(event.Id.Entry))`。
选项执行期效果只消费：**事件本地 `base.Rng`** 与 **`player.PlayerRng.Rewards`**（药水/奖励/
遗物稀有度工厂）。
- `Rng.Niche / TreasureRoomRelics / CombatPotionGeneration`：事件选项代码零接触（已 grep 验证）。
- `Rng.Shuffle`：仅 `CardPilePosition.Random` 入组时用（事件不用）。
- `Rng.Chaotic`（DateTime 播种）：仅装饰（伤害数字 VFX、Trial 入场编号），不影响玩法但破坏
  双影确定性比对 → 比对时忽略装饰字段。
全部流在影子跑局上都是克隆/确定性重放 → **执行不触碰 live 随机流的任何一位**。

## 3. 效果类风险清单（`*Cmd` 帮助类）

| 效果 | live 写入点 | 围栏后判定 |
|---|---|---|
| 金币增减 / HP / 治疗或上限 / kill | 纯模型+历史 | 安全 |
| 入组（Add/诅咒/创建） | 卡组、历史 | 安全（事件不用 Random 位置） |
| 升级/降级/附魔/变身 | 卡牌、历史 | 安全（变身吃参数传入的 base.Rng） |
| 移除卡 | 卡组 | 安全 |
| 获得遗物/药水（含 GrabBag 拉取） | 双遗物袋、槽位 | 安全（消耗影子 Rewards 流） |
| `SfxCmd.Play` 音效 | **无条件解引用** NAudioManager | `NonInteractiveMode.AutoSlayerCheck=()=>true` 静音（测试注入点） |
| VFX 分支（伤害数字/升级/获得卡） | `NRun.Instance.GlobalUi` 等 | 全部 `LocalContext.IsMe/IsMine` 门控 → **NetId 围栏**后不触发 |
| `SaveManager.Mark{Card,Potion,Relic}AsSeen` | **持久档案** Discovered* | 同上 IsMe 门控 → NetId 围栏覆盖 |
| `CardSelectCmd` 选牌界面 | 阻塞模态 + 远程选择等待 | **选择器注入**：游戏自带 `CardSelectCmd.Selector/LocalSelector` 注入栈，压入返回用户计划选择的 `ICardSelector` |
| `RewardsCmd.Offer` 奖励界面 | 开奖励 UI | **v1 黑名单**；待查是否存在奖励注入点后升级 |
| `EnterCombatWithoutExitingEvent` | **要求 IsShared 否则抛**、需要 combatSynchronizer、换场景、push live 房间栈 | **黑名单** |
| CrystalSphere 小游戏 | 开屏幕 + 发网络消息 | **黑名单** |

### 关键新发现：影子 NetId 必须改写
`RunState.FromSerializable` 保留玩家 NetId → 影子玩家 `LocalContext.IsMe == true` →
上述 IsMe/IsMine 门（档案写入、VFX、伤害数字）**全部放行**，会污染本地成就档案并触碰
`NRun.Instance.GlobalUi`。**围栏**：影子化后立刻把影子玩家 NetId 改写为哨兵值（影子内部
查找改用槽位/引用，不用 NetId）；执行结束影子整体丢弃。

## 4. 31 类事件分级

- **黑名单（真实执行不可行）**：战斗类 `BattlewornDummy`、`DenseVegetation(REST→FIGHT)`、
  `PunchOff`、（`RoundTeaParty` 无战斗）；`CrystalSphere`（不在 31 内，独立黑名单）。
  这些在计划面板显示为「进入特殊战斗/面板，无法预演」，战斗数值留给战损模拟（后续解冻）。
- **需奖励界面注入（v1 降级）**：`BrainLeech(RIP)`、`ColorfulPhilosophers`、
  `PotionCourier`、`TheFutureOfPotions`、`WhisperingHollow(GOLD)`、`WarHistorianRepy`、
  `BattlewornDummy` 的战后奖励。等找到 `RewardsCmd.Offer` 的注入/桩点后升级为可执行。
- **可执行（白名单）**：其余全部，含多页/循环次级选项——`DollRoom`、`SlipperyBridge(HOLD_ON
  循环)`、`EndlessConveyor(GRAB 循环)`、`TabletOfTruth(DECIPHER 递进)`、`Trial(两段)`、
  `TinkerTime(两段)`、`RoundTeaParty`、选牌类 `AromaOfChaos`、`DoorsOfLightAndDark`、
  `LuminousChoir`、`MorphicGrove`、`Symbiote`、`Wellspring(BATHE)`、`WhisperingHollow(HUG)`、
  `RoomFullOfCheese`、`BrainLeech(SHARE_KNOWLEDGE)`、`WelcomeToWongos`、`RanwidTheElder`、
  `Reflections`、`ThisOrThat`、`UnrestSite`、`TrashHeap`、`InfestedAutomaton`、
  `TheLegendsWereTrue`、`SlipperyBridge(OVERCOME)` 等。
  选牌选项的「5 选 1 不可跳 / 3 选 1 可跳」语义来自选项构造参数（`Cancelable=false` 等），
  计划子面板直接按执行结果渲染。

## 5. 运行时围栏（实现时逐条落实）

1. 影子化后改写影子玩家 NetId（哨兵值）；影子内查找用槽位。
2. 执行全程 `NonInteractiveMode.AutoSlayerCheck = () => true`（finally 还原）。
3. `CardSelectCmd.Selector` 压入确定性 `ICardSelector`（返回计划面板里用户点选的牌）。
4. 全程包 `PredictionPurityGuard` + live 指纹（`run_rng`/档案外所有段）——任何 live 漂移抛异常。
5. 双影比对：同一选项在两份独立影子跑局各执行一遍，结果状态指纹一致（忽略 `Rng.Chaotic`
   装饰字段）；不一致 → 该选项自动降级。
6. 黑名单/白名单做成显式表，未列事件默认黑名单（保守降级）。

## 6. 验证计划

- Debug 冒烟测试：对白名单事件逐选项执行断言 live 指纹不变 + 双影一致 + 结果与 RF 预测
  hover 集吻合。
- 人工协议：计划选择 → 实际进事件选同一项 → 对比投影状态与实际状态（卡组/金/血/遗物）。

## 7. 遗留开放项

- `RewardsCmd.Offer` 的注入点调查（解锁奖励类选项）。
- `SaveEventOptionToHistory` 是否需要为影子补写（仅影响历史统计，不影响 RNG）。
- 多人共享事件（IsShared）的本地预测语义：影子执行按单人槽位即可，标注条件。
