# 决策网络蓝图 —— CombatSolver 机制解剖与我们的采纳方案

目的:把 CombatSolver(v0.47.1)对卡牌/遗物/药水/怪物的**内容认知方式**与**搜索决策机制**拆解为可移植的设计,用于建立我们自己的决策网络与收益模型。本蓝图由三路源码解剖合成(状态与模拟层 / 内容认知层 / 搜索决策与估值层),全部结论带 CombatSolver 仓库路径。

## 一、CombatSolver 的真实决策结构(最重要的发现)

它的"最优"不是单一打分函数,而是**三层结构**:

1. **节点基础分 Score**(血量主导标量):`投影血 × 30_000` 为价值锚;死亡 −1e12;胜利 +1e10;敌方剩余有效 HP 每点 −10_000;卖血每点额外 −20_000;能力牌成长按剩余回合折现(Beam 每点 5 万);局外成长封顶 = 1 点 HP。
2. **Beam 中途排序分**:基础分 + 资源附加项(当前能量 ×60_000、可达手牌 0/1 背包、潜在铺垫表、可复用攻击、延迟伤害、敌压制/虚弱 × 剩余视野 ×30_000)。
3. **终局字典序比较**:完整胜利 → 存活 → 死亡保命消耗 → 战损赤字 → 结束回合 → … → Score 只排第 14 位(平局裁决)。

**自建收益模型必须复刻这三层,只抄一个打分函数会系统性选错路线。**

`ProjectHpAfterThreat`:对每个存活敌人的意图逐 hit:修正 → 扣格挡 → 未挡部分走 ModifyHpLost 链 → 一次性死亡保命(记账 10 倍代价)→ 终态 HP。**格挡的价值只通过投影血体现;回合末剩余格挡本身 0 分**(除非 BodySlam 类存在)。

## 二、估值项全表(27 项,收益模型的检查清单)

| # | 估值项 | CombatSolver 定价 |
|---|---|---|
| 1 | 投影血(挡后余血) | ×30 000;≤0 → −1e12 |
| 2 | 路线累计未挡伤害 | 每点 −30 000 |
| 3 | MaxHp 变化 | ±30 000/点 |
| 4 | 卖血(超同层最少战损) | 每点 −20 000 |
| 5 | 格挡 | 仅经投影血计价;剩余格挡 0 分 |
| 6 | 当前能量 | beam:min(6,E)×60 000;可达手牌 0/1 背包 |
| 7 | 胜利/死亡 | +1e10 / −1e12;敌余血每点 −10 000 |
| 8 | 易伤/虚弱/敌力压 | 回合数×min(24,攻击价值)×5 000/1 000;压制 Δ×horizon×30 000 |
| 9 | 能力牌成长 | 五维向量按剩余回合(≤16)折现,beam 5 万/点 |
| 10 | 牌库潜在铺垫 | 静态表(EchoForm 12…其他 Power 2),仅未在场 trait |
| 11 | 可复用攻击/重放 | Δ×20 000(cap 16)/ min(64)×10 000 |
| 12 | 延迟伤害(毒/末日) | 点数 ×10 000 |
| 13 | 牌库杂质 | 状态/诅咒牌 ×(−8 000) |
| 14 | 未来资源(能量/抽牌/保留) | ×16/×8/×4 + 免费牌窗口 |
| 15 | 洗牌投影质量 | Σ CardValue×(count−index)(真实 RNG 投影) |
| 16 | 局外成长 | min(值×25 000, 25 000)(封顶 1 点血) |
| 17 | 成长额度(GrowthValues) | Σ额度×次数 ×30 000;有界目标允许早停 |
| 18 | 遗物计数器/战后代偿 | HpCredit×30 000;清幕回血时恢复值 1/5 |
| 19 | 死亡保命 | 回血 ×10 倍代价 |
| 20 | 被偷资源(保资源模式) | −1e6,终局独立字典序维 |
| 21 | Anger 复制 | 每张 −15 000 |
| 22 | 药水战略成本 | 折算 HP 进终局标签 |
| 23 | 预测风险 | 未补偿 gap −2 000;胜利存疑降边界 |
| 24 | 动作数/用药数 | −1/动作;终局用药少者优先 |
| 25 | 卡牌基础价值 | Damage + 0.8×Block + 3×Draw + 8×Power |
| 26 | Boss 血量情境 | 清幕回血:HP 权重 1/5;终 Boss:只求活 |
| 27 | 洗牌后状态不确定 | 边界降级(投影截断) |

出处:`src/Search/CombatBeamSolver.StateEvaluation.cs:43-599`、`SolverWeights.cs`、`Ranking.cs:41-127`、`CombatBeamSolver.Terminal.cs`、`ActEndingBossPolicy.cs`、`GrowthPolicy.cs`、`CardChoiceSupport.cs:1093`。

## 三、内容认知的建模要素表(每类内容建模前必答)

### 卡牌(11 问)
①伤害/格挡/命中数是静态还是 Calculated?数据源?(`src/Prediction/CalculatedVarSpecRegistry.cs`,41 种已支持类型,公式 = base + extra × 分支内乘数)
②目标形状与来源形状?(GeneralCardMirrors)
③效果原子序列?哪个原子可被玩家选择中断?(CardEffectRecipe:原子可中断、断点续帧)
④条件判定门?(BespokeCardMirrors:PACTS_END 消耗堆阈值、DISMANTLE 易伤双段、SPITE 掉血双段)
⑤可打出覆写?(CardIsPlayableMirrors + CanPlay 链)
⑥生成牌?直接入手还是 N 选一?候选池与 RNG 流?(CardGenerationCardMirrors:候选集记入历史,选择未决标记)
⑦玩家选择(弃/烧/移/复制/变形)?(CardChoiceSupport.GetSpec:候选与上下界、组合去重)
⑧关键词改变什么行为?(Exhaust/Ethereal/Retain/Unplayable 各有权威出处)
⑨读哪些历史计数?(CombatHistoryCounters 6 项:完成出牌/虚无出牌/闪电球/未格挡受击/抽卡/生成卡;读者→依赖掩码,如 GOLD_AXE 读完成出牌、MURDER 读抽卡、TEAR_ASUNDER 读未格挡受击、ASHEN_STRIKE 读消耗堆)
⑩附魔/灾厄/手牌回合末效果/落点改写?(EnchantmentOnPlayMirrors、CardOnTurnEndInHandMirrors、CardResultLocationMirrors)
⑪OnPlay 是否被 IL 推断覆盖(纯攻击/格挡/抽牌直线体自动生成镜像)?

### 遗物(5 问)
①计数周期与状态来源,能否作战斗末目标?(RelicCounterCatalog:11 项可作目标,25 项明确不可)
②私有可变状态与根初值捕获?(SimulatedCombatState.Relics.cs:19 项反射捕获)
③触发时机(抽牌前/回合开始/能量重置后/出牌后/受伤后/死亡后/用药后/额外回合)?
④效果施加(含挂起选择续帧,如 TOOLBOX 三选一)?
⑤目标范围与愿付战损(HP 额度/优先级 1-3)?(RelicCounterPolicy)

### 药水(6 问)
①直接效果还是复杂效果(OnUse 镜像)?(PotionOnUseSupport.Use switch 覆盖 57 种)
②是否生成牌?入手还是 N 选一?候选来源?(CardGenerationPotionMirrors:按角色池 3 张/无色池)
③选择规格:Effect/SourcePile/min/max?(CardChoiceSupport.CardChoiceSpec;攻击药水 = [0,1] 可不选)
④目标类型决定枚举几个分支?
⑤战略成本(HP 折算)与豁免?
⑥部署回放 token(CardId+Upgrade+occurrence)与选择前缀续跑(先 fork"用药后选牌前"的稳定前缀,每个候选从那里续跑——不重复模拟)?

### 怪物(8 问)
①状态机结构:MoveState/RandomBranch/Conditional?各攻击意图基伤与次数?(BranchMonsterStaticSnapshot.Capture)
②随机分支权重与限流(Once/CannotRepeat/XTimes/Cooldown)?RNG 只在 NextFloat?(MonsterRandomBranchResolver:连 total=0 返回第一支的原版行为都复刻)
③条件分支判据(血量/队友数/私有计数)?未知时回退根选择并标 exact=false?
④非攻击效果白名单(上 Power/召唤/自爆/偷牌)?(MonsterMoveEffects:约 140 组怪×move)
⑤动态伤害修正?(AdjustMonsterMoveDamage)
⑥反应型 Power(受击/死亡)触发条件与两段式状态?(AfterDamageReceivedMirrors:CURL-UP 两段式、唤醒、反伤、阈值;AfterDeathMirrors:补货重生/队友死联动)
⑦召唤:入场 Power、槽位、首动掷点?(MonsterSpawnSupport)
⑧多阶段/复活/死亡联动?(RESPAWN、蒸汽爆发、Queen-Amalgam)

### 贯穿原则(两条元问题)
1. 每个语义有且只有一个**权威结算点**(mirror/spec/support/state);
2. 分支内读取的每个值必须声明来源(根冻结快照/分支旁表/双历史计数),禁止触碰实机对象。

## 四、随机性的注入边界(对我们最重要的发现)

CombatSolver 的九条 RNG 流(洗牌/生成卡/生成药/选牌/能耗/目标/生成球/怪物 AI/niche)的**全部消费点收敛到单一接口**;生成型效果已被建模为"候选集 + 一次抽样"(`CardGenerationResultKind {Random, Contextual, Fixed}`,候选集记入历史);逐击伤害已区分"逐击重算的 CalculatedVar"与"传入的定值"。

**含义:把"取下一个随机数"替换为"外部注入采样值/分布",执行层完全不动。** 这正是我们"无随机数知识、按分布推理"立场所需要的接缝——CombatSolver 的执行层天然支持确定化采样(determinized rollouts),我们的 MPC 架构可以直接对接。

## 五、我们的决策网络设计(采纳映射)

```
┌─ L1 内容认知层(采纳 CombatSolver 分类学)
│    卡牌要素表 11 问 → 效果原子 + 条件门 + 计数器读者声明
│    怪物要素表 8 问  → AI 状态机 + 反应型注册表
│    遗物 5 问 / 药水 6 问
│
├─ L2 状态层(信念词汇表,引擎权威 + 事件增量)
│    生物 {hp,maxHp,block,powers} × 玩家 {energy,牌堆账本,药水}
│    回合内计数器 + 战斗级计数器(消耗堆/生成/掉血事件,漂移检测)
│    隐藏状态旁表思想:不可观察的修正(如 Rage)由驱动器携带并声明
│
├─ L3 回合内精确搜索(已实现,毫秒级)
│    动作枚举:出牌(逐目标)/用药/结束回合
│    估值 = 下述 L4 收益模型的回合内投影
│    剪枝:族代表(七族)+ 转置表
│
├─ L4 收益模型(研究目标,双 oracle 监督)
│    回合内项:投影血/伤害/击杀/卖血(现 v0)
│    待采纳:上表 27 项逐项接入,顺序按对局敏感度
│    监督:CombatSolver 强标签(战斗入口) + 引擎 rollout(回合级真值)
│
└─ L5 在线循环(MPC,已实现)
     执行一步 → 重观察 → 重解;偏差被吸收,不跨步累积
```

## 六、采纳优先级(按对收益模型评价的贡献排序)

1. **投影血替换现有掉血计价**(把"意图总量 − 格挡"升级为逐 hit 投影 + 卖血惩罚)——27 项中的第 1/2/5 项,直接决定留挡/斩杀决策质量;
2. **敌余血负债 + 击杀奖励校准**(第 7 项),修正"慢性死亡"局面的攻防配比;
3. **反应型怪物注册表**(怪物第 6 问):消除当前唯一残留差异类(+1 力量受击反应),即 CombatSolver AfterDamageReceived 同族;
4. **能力牌成长的剩余回合折现**(第 9 项):修正成长怪对局的攻防配比;
5. **历史计数器读者声明**(卡牌第 9 问):CalculatedVar 类卡的分支内数据源显式化;
6. **族代表剪枝 + 转置表**(搜索侧,可选):当前规模不必要,手牌扩到全池后再加。

## 七、与现有工作的关系

- 回合分解、MPC、分级对拍、漂移检测:全部保留,是本蓝图的 L3/L5 与验收设施;
- 87 张效果骨架:保留,作为 L1 的近似实现;新卡语义按"先查 CombatSolver 镜像、再写骨架、对拍验收"的流程补齐;
- sts2-cli:继续作为真值执行器与 rollout 标签来源;
- CombatSolver:作为 L4 的强标签 oracle 与语义参考实现,**不做运行时依赖**。
