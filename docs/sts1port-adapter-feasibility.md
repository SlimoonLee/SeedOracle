# STS1Port「一代老兵」的 CombatSolver 适配可行性评估

评估对象:工坊 `3751303939`,STS1Port `4.3`(铁甲战士/静默猎手/故障机器人三角色,225 卡,`affects_gameplay: true`,无 RitsuLib 依赖,内嵌 BaseLib.Abstracts 迷你框架)。
参照模式:`docs/combat-solver-adapter-guide.md`(AutoWatcher 范式)、`.reference/STS1Port-decompiled/`(反编译,354 个源文件)。

## 结论

**能做,模式已被 AutoWatcher 完整验证,无原理性障碍;但量级约为 AutoWatcher 的 3 倍(≈330–355 条登记 vs 观者 ≈120 条),且有两个"不解决就整局硬失败"的一票否决点、一个"算对与否无法报警"的补丁区。** 建议按分阶段路径做,第一阶段即可发布可用子集。

## 内容面盘点(反编译实测)

| 维度 | 数量 | 对适配的含义 |
|---|---|---|
| 卡牌文件 | 246(Fault/Stalker/Steel 各 82) | 244 张重写 OnPlay → 每张一条登记 |
| 其中简单牌(单 await 无分支) | 61(36 纯攻击、16 纯格挡、少量单 Power) | 动词组合便宜覆盖 |
| 其中玩家选择牌 | 17(`CardSelectCmd`:弃牌 5、手选 7、跨牌堆 FromSimpleGrid 4、升级 1)+ Power 侧 2 | `CardChoiceMirrors` 19 条,否则静默空操作 |
| 其中 CalculatedVar 动态变量牌 | **11**(`grep` 实测,见硬点 1) | 门禁级 |
| X 费牌 | 8(统一 `HasEnergyCostX`/`ResolveEnergyXValue`) | 一套动词覆盖 |
| 牌堆操作特殊牌 | ~74 | 逐张转写,工作量主体 |
| 生成牌 | 18,token 全是**原版** Shiv/Wound/Burn/Dazed 等 | 走求解器现成生成选项通道 |
| 自定义状态/诅咒牌 | 0 | 零成本 |
| Power | 24,钩子覆写 30 处(Modify*/Should* 6 处只读自动回落) | 登记 ~24–30 条 |
| Power 隐藏状态 | 9 张卡的私有计数 + 4 个 Power 的 `_internalData`(Choke/Heatsinks/ThousandCuts 的按牌字典、OrangePill 计数) | `PowerHiddenStateMirrors` 13 条 + 根捕获 |
| 遗物 | 4(2 个只读回落零登记) | 2 条 |
| 自定义药水 | **0**(三角色全复用原版药水池) | 零成本 |
| 角色池 | 三角色复用原版遗物/药水池,仅牌池独立 | 外围成本≈0 |
| Harmony 补丁 | 51 个文件,其中影响战斗的 **6 个**(见硬点 3/4) | 32 个纯表现可无视 |
| ModHelper 战斗订阅者 | **0 个**(只有 5 处 `AddModelToPool`) | 订阅者门禁天然通过 |
| 额外回合/召唤物/多阶段 | 无 | 求解器写死的额外回合表不构成冲突 |

## 有利因素(比观者容易的部分)

1. **门禁第一关天然通过**:没有独立战斗 hook 订阅者,不需要碰 `KnownPreRootSubscriberTypeNames`。
2. **框架零行为**:BaseLib.Abstracts 副本只加图标路径与 `GainsBlock` 扫描,所有战斗行为都在原版 `CardModel` 虚方法内的原版 Cmd 上——要镜像的只有各卡自己的重写。
3. **大量牌 Apply 的是原版 Power**(Weak×11、Vulnerable×8、Strength×7、Poison×6、Focus×5…),求解器已有原版镜像,登记即用;形态牌(Demon/Echo/Wraith Form)同理。
4. **药水/遗物外围几乎为零**,token 全原版。
5. **无姿态、无额外回合、无洗牌触发重写**——观者最难的三大块这里都没有。

## 一票否决点(必须先解,否则装上就停)

### 1. CalculatedVar ×11 张 → `CalculatedVarSpecRegistry` 封闭

`CombatPredictionDynamicVarExtensions` 对第三方卡的 `CalculatedVar` 求值失败时直接抛 `IncompatibleGameplayModException`(11 张:PerfectedStrike/Stack/CompileDriver/Finisher/Flechettes/Barrage/BodySlam/DemonicShield/HeavyBlade/Blizzard/ThunderStrike——原版同名逻辑的卡在表内,但 STS1Port 是**不同运行时类型**,查不到)。核实过 0.47.1 的 `src/Prediction/CalculatedVarSpecRegistry.cs`:写死的 `SupportedTypes` 清单 + 私有 `switch`,**不像门禁白名单那样可以注入**。两条解法:

- **推荐:补求解器内部方法**(AutoWatcher 兜底模式):Prefix `TryCalculate`,STS1Port 卡按分支数据返回乘数,其余照原路走。目标方法单一、签名稳定,失败即整体拒绝加载。
- 或者给上游提 RFC 开 `Register` API(求解器对第三方入口的接受度高,`CardChoiceMirrors`/`GrowthSourceMirrors` 都是这么来的),但周期不可控。

### 2. `ClawInteropPatch` 补在**原版** `Claw.OnPlay` 上(已核实:`AccessTools.Method(typeof(Claw), "OnPlay")`)

`PredictionModPatchAudit` 会审计战斗与整副牌组每张卡的 OnPlay 补丁,发现第三方 owner 即拒——牌组里只要有一张原版爪击(事件/无色牌可带入),整局求解被拒。解法明确:`AdaptedCardOnPlayMirrors.Register<Claw>(schema, target, patches, handler)` 组合登记,逐字复刻补丁签名(owner/priority/before/after),加载期一次做完(首战捕获后封闭)。

## 高风险区("算不对但不报错",必须显式降级)

**球类三补丁**:`ElectroPatch`(闪电球被动/放球 AOE 化——`LightningOrbMirrors` 是原版硬编码,无覆写点)、`LightningChannelPatch`(每充一个雷球 FaultThunderStrike +1、冰球 FaultBlizzard +1)、`OrbDamageMarker`(用 `StackTrace` 给球伤害补 Attack 位,LockOn/幻杀/火息的数值正确性依赖它)。**这是整个适配唯一无法用现有注册表干净表达的语义**,也是机器人角色的核心机制。建议:能近似登记的走 `AfterOrbChanneledMirrors`;表达不了的按 AutoWatcher 的 `Unmirrored` 红字风险显式降级,绝不静默近似。

其余封闭开关撞点(均有 AutoWatcher 先例可抄):常规 `AfterSideTurnEnd`(6 个 Power + 2 张牌的到期结算,挪 `AfterSideTurnEndLateMirrors` 近似)、`AfterApplied`/`AfterPowerAmountChanged`(Flex/Combust)、`ModifyMaxEnergy`(Berserk)、冲洗保留(`StalkerWellLaidPlansFlushPatch` 补在 `AbstractModel.BeforeFlushLate`——按 `WatcherRetainPatch` 模式补 `FlushPlayerHandAtTurnEnd`)、Reflex 的弃后抽(静态队列补丁改写为镜像内"弃 X 抽 Y"语义)。

## 工作量估算

| 项目 | 条数 |
|---|---|
| 卡牌 OnPlay 镜像(244 张;61 张是 1–5 行动词组合) | 244 |
| 卡牌非 OnPlay 钩子(Discarded/Exhausted/Drawn/Played…) | ~16 |
| Power 钩子镜像 | ~24–30 |
| `CardChoiceMirrors` 选择 | 19 |
| 隐藏状态指纹(+根捕获) | 13 |
| CalculatedVar 兜底补丁 | 11(一个补丁点) |
| `AdaptedCardOnPlayMirrors`(原版 Claw) | 1 |
| `GrowthSourceMirrors`(Rampage/Claw/GeneticAlgorithm 类跨战成长) | ~3 |
| 球类/冲洗/Reflex 等特殊补丁 | ~5 |
| **合计** | **≈330–355**(AutoWatcher ≈120) |

难点 Top5:①CalculatedVar 封闭(见上)②Claw 补丁审计(见上)③缺注册表的回合末/施加时点钩子(8+ Power,近似引入系统性偏差)④球三补丁语义 ⑤19 张选择 + 74 张牌堆操作的逐行转写。

## 建议实施路径

1. **Phase 0(骨架)**:工程骨架 + `AdapterSelfCheck`(钉 STS1Port ≥4.3、CombatSolver ≥0.38.2)+ `SolverCompat` 软绑定;无夹具不登记。
2. **Phase 1(可发布子集)**:61 张简单牌 + 原版 Power 牌 + 选择 19 + Claw 组合登记 + CalculatedVar 兜底补丁 → 三个角色已"能算",覆盖大多数对局;未覆盖部分显式红字。
3. **Phase 2(完整性)**:Power 钩子 24–30 条 + 隐藏状态 13 + 回合末近似 + 冲洗/Reflex 补丁。
4. **Phase 3(球机制)**:球三补丁建模或显式降级;这条决定机器人角色的可信度,单独出夹具矩阵。
5. 验证沿用指南第 9 节:算术判据夹具 + 反向对照,`-ExpectedInitialUnmirroredCount 0` 逐步收紧;mods/ 只留 CombatSolver + STS1Port + 适配层三个。

## 材料索引

- 反编译源码:`.reference/STS1Port-decompiled/`(卡 246、Power 24、补丁 51、角色 9、遗物 4)
- 范式:`.reference/AutoWatcher/`、`docs/combat-solver-adapter-guide.md`
- 求解器侧:`.reference/CombatSolver-latest/src/Prediction/CalculatedVarSpecRegistry.cs`、`src/Prediction/PredictionModPatchAudit.cs`、`docs/THIRD_PARTY_ADAPTERS.md`
