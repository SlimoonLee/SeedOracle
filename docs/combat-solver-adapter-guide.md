# CombatSolver 第三方适配指南(给其他 mod 做适配怎么做)

研究基线:CombatSolver upstream `main` @ `v0.47.1`(commit `7d9b4be`)、AutoWatcher `1.0.7`、观者 `0.9.29`。
本地材料:`.reference/CombatSolver-latest/`(upstream 最新)、`.reference/CombatSolver/`(作者原库旧检出)、
`.reference/AutoWatcher/`(适配 mod 完整范例)、`.reference/Watcher-decompiled/`(观者 0.9.29 反编译,304 个源文件)。

本文回答一个问题:**怎么写一个像 AutoWatcher 那样的适配 mod,让 CombatSolver 能模拟"另一个内容 mod"的牌、Power、遗物、药水**。规范原文见求解器仓库 `docs/THIRD_PARTY_ADAPTERS.md`(第三方适配手册),本文按 0.47.1 现状补充实现细节与 AutoWatcher 的落地模式。

## 0. 两种"适配"先分清

| 形态 | 谁 | 做什么 |
|---|---|---|
| **内容适配 mod**(本文主线,AutoWatcher 型) | 想让求解器看懂某个内容 mod 的作者 | 在自己的 mod 里把目标 mod 的卡牌/Power/遗物/药水**登记进求解器的镜像注册表**,不改变任何游戏行为 |
| **API 调用方**(SeedOracle 型) | 想在路线预测/规划里调用求解器的作者 | 走 `PreCombatForecastApi`(public,`ApiVersion = 6`,位于 `src/Api/PreCombatForecastApi.cs`)起隔离 headless worker,按 v5/v6 协议求解 |

内容适配 mod 的清单关键是 **`"affects_gameplay": false`**:求解器因此不把它当 gameplay mod 处理(门禁放行、镜像查询判 `Ignored`、Harmony 补丁审计不算外来补丁),同时它自己也不改游戏行为。调用方一侧 SeedOracle 已经趟通,本文不展开。

## 1. 求解器怎么对待第三方内容(为什么要适配)

求解器不认识任何第三方内容。它在 headless 模拟里用**镜像**按精确运行时类型重现战斗行为,查不到就退化:

- **门禁**(`src/Prediction/PredictionModHookSubscriberCapture.cs`、`PredictionModHookSubscriberInertness.cs`):扫描所有 ModHelper 战斗 hook 订阅者,按顺序检查 ①清单 `affects_gameplay: false` ②`IsCombatInert`(只重写了战斗外/战斗开始结束 hook)③`KnownPreRootSubscriberTypeNames` 白名单 ④BaseLib CardModifier。全不满足 → 抛 `IncompatibleGameplayModException`,**整个求解器停摆**。另有按 Mod ID 的显式黑名单(`PredictionModPatchAudit.IncompatibleModIds`:WheelchairSpire、PengoTarot、BetterCharacterRelics),优先于一切放行。
- **镜像分派**(`src/Engine/Common/Mirrors/MethodMirrorRegistry.cs`,`MirrorDispatchKind`):每个被镜像的虚方法按精确类型查表,五种结果——`NotOverridden`(走基类,正确)、`Handled`(有登记,目标状态)、`Inferred`(结构推断,记风险)、`Ignored`(人工确认无影响)、`Unsupported`(记风险)。**风险红字只是显示**,不改搜索;凡是会改变"接下来还能做什么"的效果(强制结束回合、额外回合、让牌打不出)必须真建模。
- **只读 hook 自动回落**:`Modify*`/`Should*` 这类 hook 求解器会直接调用 mod 自己的原实现(姿态伤害倍率就是这样)。**这是适配能便宜的根本原因**:凡是只读 hook 不用登记;要登记的是 `After*`/`Before*`/`OnPlay` 这些有副作用的。

适配 mod 的白名单写入注意:`KnownPreRootSubscriberTypeNames` 是 **private static** 字段(0.47.1 仍是,官方标注"待做"未开公开入口),AutoWatcher 用 Krafs.Publicizer 后直接 `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames.Add(typeof(自己的HookProxy).FullName!)` 写入——publicizer 连 private 成员都能公开化访问(实证:`AutoWatcher/src/Entry.cs:69`)。

## 2. 决策:适配到什么深度

| 你的 mod 内容 | 要做什么 |
|---|---|
| 纯美术/UI/音效(`affects_gameplay: false`) | 什么都不做,自动放行 |
| 只改地图/事件/商店等战斗外内容 | 什么都不做,判定战斗惰性 |
| 加了牌/Power/遗物/药水/敌人 | 往下做:门禁放行 + 镜像登记 |
| 有"收益取决于出手顺序"的 Power | + `StrategicEffectMirrors`(估值) |
| 有带玩家选择的牌/药水 | + `CardChoiceMirrors` / `PotionChoiceMirrors`(否则**静默变空操作**,不是红字) |
| Power 有隐藏计数(私有字段/`_internalData`) | + `PowerHiddenStateMirrors`(否则**分支被错误去重**) |
| 有局外成长(金币/永久升级/斩杀升级) | + `GrowthSourceMirrors`(否则**搜索必然避开这张牌**) |
| 自己用 Harmony 补丁改写了某张牌的 OnPlay | + `AdaptedCardOnPlayMirrors`(按实际补丁组合登记) |

## 3. 工程骨架(照抄 AutoWatcher)

```
YourAdapter/
├── YourAdapter.csproj      # Godot.NET.Sdk,net9.0,Krafs.Publicizer
├── YourAdapter.json        # affects_gameplay: false + 三条 dependencies
├── local.props.example     # 只含 SteamRoot,git 提交;local.props 本地覆盖
├── src/
│   ├── Entry.cs            # [ModInitializer],自检→注册,全有或全无
│   ├── AdapterSelfCheck.cs # 加载期反射自检(硬门槛)
│   ├── SolverCompat.cs     # 可选入口四态软绑定
│   ├── PinnedTargets.cs    # 版本下限 + 哈希钉死
│   ├── YourSim.cs / *Verbs.cs   # 统一模拟上下文 + 效果动词
│   └── *Mirrors.cs / *Patch.cs  # 各域登记 + Harmony 兜底
├── tools/run-matrix.ps1    # 无头夹具矩阵
└── docs/VERIFICATION.md    # 逐条核对台账 + 反向对照记录
```

csproj 要点(`AutoWatcher/AutoWatcher.csproj`):

```xml
<PackageReference Include="Krafs.Publicizer" Version="2.3.0" PrivateAssets="all" />
<!-- 连 private 成员都可访问(Entry.cs 直写 KnownPreRootSubscriberTypeNames 私有字段的实证) -->
<Publicize Include="sts2|CombatSolver|TargetMod" IncludeVirtualMembers="false" IncludeCompilerGeneratedMembers="false" />
<!-- 所有外部引用 Private="false",不把别人二进制带进产物 -->
```

清单(`YourAdapter.json`)按依赖逐条钉最低版本,这是唯一可靠的版本门(被适配 mod 的 `AssemblyVersion` 可能是 `0.0.0.0`,观者就是这样——它的真版本只在清单里):

```json
"affects_gameplay": false,
"dependencies": [
  { "id": "STS2-RitsuLib", "min_version": "0.5.13" },
  { "id": "CombatSolver",  "min_version": "0.38.2" },
  { "id": "TargetMod",     "min_version": "0.9.28" }
]
```

## 4. 加载时序与三条纪律(`AutoWatcher/src/Entry.cs`)

```
[ModInitializer] Initialize()
  1. AdapterSelfCheck.Run()          // 失败 → log Error 后直接 return,一个镜像都不注册
  2. 放行自己的订阅者(白名单 Add)
  3. RegisterAll():可打出性 → 起手牌 OnPlay → Harmony 补丁 → 隐藏状态 →
     Hook/遗物/药水镜像 → 战略估值 → 卡牌/药水选择 → 成长来源 → 移除偏置
     // 任一步抛异常 → catch、log、return,整个注册作废
```

纪律(踩过坑的,不是风格建议):

1. **加载时一次性登记完**。`MethodMirrorRegistry` 查询结果进 `_lookupCache`/`_resolvedSnapshot`,类型一旦被查过(拿到 `Inferred`/`Unsupported`),之后再 `Register` **静默失效**;`ModelPredictionStateMirrors`、`AfterSideTurnEndLateMirrors`、`AdaptedCardOnPlayMirrors` 则在首次根捕获后 `Seal()` 冻结,迟到登记明确抛异常。mod 初始化是唯一同时满足"任何战斗前"的时机。
2. **全有或全无**。自检不过或注册中途失败就一个都不登记——半套镜像比不装更糟(求解器拿一部分正确的镜像给出看起来可信的路线,缺的部分静默空操作)。
3. **登记优先、补丁兜底**。求解器有登记入口的一律登记;同一位置从登记转正后要把补丁撤掉(观者 `DevaPower` 根捕获:`NeedsRootCapturePatch` 为 false 时不装补丁)。

## 5. 版本钉死:三层钉 + 四态绑定(AutoWatcher 模式)

1. **版本下限(硬拒绝)**:对 CombatSolver 按程序集版本判(`AdapterSelfCheck` 反射比对 `PinnedTargets.CombatSolverMinimumVersion`);对被适配 mod 按清单 `dependencies` 交给加载器。教训:0.31.3 源码里有扩展点但发布二进制不全,所以下限要钉**发布版**。
2. **结构自检(硬拒绝)**:`AdapterSelfCheck` 只用反射(自检若直接引用缺失类型会先 JIT 崩)逐项查 `("CombatSolver.CardChoiceMirrors", "Register")` 这类**必需成员**与被适配 mod 的**私有成员**(publicizer 放开的、更新中最容易被动到的),缺一个就用人话拒绝加载。
3. **SHA256(软警告)**:被适配 mod 的 DLL 哈希与核对过的版本不一致 → 照常注册但 Warn"语义漂移抓不到,发现路线不对请报 Bug"。结构自检抓"改了结构",哈希抓"没改结构只改数值"。
4. **可选入口四态绑定**(`SolverCompat`):对"旧求解器没有、新求解器才有"的入口,加载时用**表达式树编译成委托**绑定一次,四态区分 `Bound / Absent(上游还没做,跳过是预期) / Mismatched(上游改了签名,是我们的 bug) / Faulted`,绑不上留 null、调用点 `?.Invoke`。签名核对只查前 N 个参数、容忍末尾新增可选参数。教训:两态方案里"上游没做"和"我们过期"长得一模一样,0.38.3 给 `GrowthSourceMirrors.Register` 加可选参数时静默失效了两天。

## 6. 登记入口总表(0.47.1 现状)

全部入口是 **internal**,第三方必须自配 publicizer。`docs/THIRD_PARTY_ADAPTERS.md` §2.7/§2.8 的"尚未发布"是陈旧标注,代码已在 **0.33.6** 发布。

| 入口 | 最低求解器版本 | 用途 / 不登记的后果 |
|---|---|---|
| `XxxMirrors.Registry.Register<T>(handler)`(Hooks/ 39 张 + Cards 4 + Potions 1 + Enchantments/Afflictions 各 1) | 早期 | `After*`/`Before*` 副作用钩子。重复登记抛异常(`Dictionary.Add`);漏登记记红字风险,但改变可行动作集合的效果必须建模 |
| `AfterEnergyResetMirrors` | **0.38.2** | 回合开始重置能量后的能力结算(之前是写死五类型的 switch,第三方漏了也不报) |
| `CardIsPlayableMirrors.Registry.Register` | 早期 | 可打出性;不登记按基类恒真,牌被排进路线到部署才报 `card_unplayable` |
| `CardOnPlayMirrors.Registry.Register`(经共享注册表,处理器文件自己不持表) | 早期 | OnPlay 效果,按反编译实现逐行写 |
| `PotionOnUseMirrors.Registry.Register` | 早期 | 药水使用效果 |
| `CardChoiceMirrors.Register<TCard>(spec, apply)`(PR #56) | **0.32.0** | 卡牌玩家选择展开成搜索分支;不登记**静默变空操作**。升级等级必须对(部署按 CardId+升级定位);选项牌不在模拟牌堆里,拿的是 `PlanCardToken` |
| `PotionChoiceMirrors.Register<TPotion>(spec, apply)` | 早期 | 药水玩家选择;不登记求解器当它没收益随手插 |
| `StrategicEffectMirrors.Register<TPower>(requirements, evaluate, host)` | 早期 | 会改变出牌顺序的 Power 估值;`StrategicEffectHost.Enemy` 用于挂在敌人身上的收益(反弹格挡型),否则动作分类认不出它是防御 |
| `PowerHiddenStateMirrors.Register<TPower>(name, read)` / `RegisterRootCapture<TPower>(capture)`(PR #58) | **0.33.0** | Power 隐藏计数进状态指纹;不登记**分支被错误去重**。`_internalData` 类状态必须同时登记根捕获(克隆会重置);读取函数纯读、无分配、只读自己;状态存 `simulator.StateStore`(实现 `IPredictionStateForkable`) |
| `GrowthSourceMirrors.Register(id, card, hasTarget, title?, opportunityTarget?)` | **0.33.6** | 局外成长来源:侧栏额度行 + 指纹 + 早停豁免;不登记**搜索必然避开这张牌**。id 要稳定(持久化键);`RecordGrowthReward` 只在收益真到手时调 |
| `CardRemovalValueMirrors.Register<TCard>(offset)` | **0.33.6** | 移除估值偏置(\|offset\|≤100);起手打击类给负偏置,否则净化永远不会先烧它(静默) |
| `ModelPredictionStateMirrors.RegisterRelic/RegisterModifier<TModel,TState>(schema, capture, writeLive, writePredicted)` | **0.35.2** | 遗物/Modifier 独立分支状态(进指纹与续用戳);首根/续用捕获后整表冻结 |
| `AdaptedCardOnPlayMirrors.Register<TCard>(schema, target, patches, handler)` | **0.38.2** | 被(自己或他人的)Harmony 补丁组合改写过 OnPlay 的卡:登记精确目标+完整补丁组合+唯一完整预测实现,命中后不再跑原版 OnPlay |
| `AfterSideTurnEndLateMirrors.Register<TModel>(handler)` | **0.38.2** | 回合结束晚期结算;未登记的有效覆写**抛 `NotSupportedException`**(不只记风险) |
| `BeforeSideTurnStartMirrors.Register` / `AfterPlayerTurnStartMirrors.RegisterEarly/Register/RegisterLate`(PR #126/#128) | **0.44.1** | 回合开始前 / 玩家抽牌后三轮(Early/普通/Late) |
| `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames.Add(...)` | (私有字段) | 门禁白名单放行自己的 ModHelper 订阅者;用 `typeof(X).FullName!` 不写死字符串 |

登记时的语义纪律:按**反编译实现**写,不照卡面文字猜(典型坑:`PowerVar<T>` 单参构造的键是 `typeof(T).Name` 不是卡面词);抽牌边界、施加时点、"上一张牌"口径这些**时机比数值更容易错**;第三方 Power 来源用 `ICombatPredictionEffectSink.ApplyPowerFromSource` 显式给 `cardSource`,原版传 null 保持 null;镜像里读的是**模拟分支状态**(预测意图、模拟手牌),绝不能读 live 状态——后台线程读到的是玩家屏幕上的值。

## 7. 求解器没有入口的地方:Harmony 兜底

0.47.1 仍封闭(只能补丁,见 `THIRD_PARTY_ADAPTERS.md` §6):额外回合来源(只认龙涎香/帕尔之眼)、`CombatPredictionSimulator.OnPlayWrapper` 出牌后补抽、回合末弃手牌/BeforeHandDraw/常规回合末、卡牌私有隐藏字段进指纹(§2.6 只覆盖 Power 侧)等。

补丁模式(全部打在**求解器模拟器内部方法**上,天然只影响模拟、不碰实机):

- 目标方法用 `AccessTools.Method(...)` 在 `ResolveTarget()` 里现场解析,**解析不到抛异常 → 整个适配层不注册**。
- 需要兼容多版本求解器时:`ResolveTarget()` 返回 `(MethodInfo Target, string PostfixName)` 二元组,两版参数名不同就各写一个只差签名的 Postfix 薄壳,逻辑集中一处(AutoWatcher `WatcherTurnEndPatch` 兼容 0.32/0.33 两个落点的样板)。
- 时序敏感的用 **Prefix 记 `__state`,Postfix 消费**(保留名单要在冲洗前记,不能用"冲洗后还留着"反推);调用方把返回 bool 当搜索边界的,挂起选择时写 `ref bool __result = true`。
- 挂起玩家选择的走回合开始选牌通道 `TurnStartChoiceSupport.ResolvePileDiscard`,不要走出牌那条。

## 8. 模拟侧编程模型(AutoWatcher 的写法)

- **统一上下文 struct**(如 `WatcherSim`):求解器每类钩子各有上下文类型、无公共基类;把 `SimulatedCombatState / CombatPredictionSimulator / CombatPredictionState / CombatPredictionHistory / Player` 收进一个 readonly struct,各上下文写一个 `From(...)`,效果动词全挂在它上面。
- **两层动词**:卡牌上下文动词(`Attack/Block/Draw/Power/ForceEndTurn/TakeExtraTurn...`)+ Sim 级动词(`GainMantra/Scry/TemporaryDexterity/ApplyEndTurnDeath...`)。一张"施加一个 Power"的牌用 `Effects.ApplyPower(Type, amount)` 泛型覆盖(内部 `MakeGenericMethod`,对任意 `PowerModel` 子类成立)——这是批量便宜覆盖的关键。
- **姿态/形态等原版可见状态直接建模成 `PowerModel`**:施加/清零之后,求解器的指纹、剪枝、去重自动认得;其只读 hook(`ModifyDamageMultiplicative` 等)求解器自动回落到 mod 原实现,数值自动对。
- **隐藏状态存 `simulator.StateStore`**(实现 `IPredictionStateForkable`,`Fork()` 用 `MemberwiseClone()`),不要存在 Power 对象上(`DeepCloneFields` 会重置 `_internalData`,搜索每次分叉都克隆)。只多记必要的一个整数(如实例个数),不必塞整张表。
- **临时增益的坑**:施加侧与回收侧不对称,普通 `ApplyPower` 只加记账层、回合结束照扣真敏捷 → 下一回合负敏捷,"不是少算,是算反",必须走 `ApplyTemporaryDexterity` 这类配套动词。
- **诚实降级**:建模不了的效果 `History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete)` 让求解器打红字,而不是静默空操作;但红字不适用于会改变可行动作集合的效果,那必须真建模(强制结束回合走 `RequestPlayerTurnEnd()`,与原版同一条路)。

## 9. 验证

验收标准(`THIRD_PARTY_ADAPTERS.md` §4,不适手感/胜率先行):

1. **严格 diff 零差异**:模拟终局与真实终局逐字段相等;
2. **`PredictionGaps` 非补偿项为空**:求解器不报任何未镜像效果。

胜率只在两条都干净之后才有意义(抓 diff 抓不到的估值定价错)。每条夹具必须做**反向对照**:注释掉目标登记重新构建,夹具必须挂;恢复后必须过。

基础设施:求解器仓库 `tools/run-unattended-test.ps1`(PowerShell 7.4+,本机 Steam 游戏副本 + 工坊 RitsuLib,逐实例隔离)跑单场景;AutoWatcher `tools/run-watcher-matrix.ps1` 是适配 mod 侧的矩阵样板——34 条夹具、全部用**算术判据**(正好斩杀回合、格挡峰值、能量收支、`-ExpectedInitialUnmirroredCount 0`),开头校验 harness 产物与编译引用 DLL 哈希一致,并行度钉 1(否则最优性断言随机挂)。环境:mods/ 只留 CombatSolver、被适配 mod、适配 mod 三个;改 mod 后必须杀游戏进程再跑。反向对照记录进 `docs/VERIFICATION.md` 台账。

## 10. 给其他 mod 做适配:执行清单

1. 读被适配 mod 的反编译源码(ilspycmd),列出**每个类型对基类虚方法的重写**,与第 6 节的注册表逐一对照——漏一个的表现是"效果看起来正常但其实没发生"。只读 hook 不用登记。
2. 建工程骨架(第 3 节),清单 `affects_gameplay: false` + 三条依赖,publicize `sts2|CombatSolver|目标mod`。
3. 写 `PinnedTargets` + `AdapterSelfCheck` + `SolverCompat`(第 5 节),**先于一切注册**。
4. `Entry`:白名单放行自己的订阅者 → 一次性 `RegisterAll()`,全有或全无。
5. 逐类型写镜像(第 8 节动词库);撞封闭开关的用第 7 节补丁模式;能转正的登记随时替换补丁。
6. 按第 9 节搭夹具矩阵,每条带反向对照;跑通后再谈胜率。
7. 发布分两条线:对标求解器**发布版**的工坊版 + 钉某个开发版的 GitHub 版;求解器每升一版,核对 `docs/releases/` 与 `THIRD_PARTY_ADAPTERS.md` 的开关表变化。

## 11. 版本时间线(适配相关,0.32 → 0.47.1)

- **0.32.0** `CardChoiceMirrors`(PR #56)· **0.33.0** `PowerHiddenStateMirrors`(PR #58)+ 黑名单机制 · **0.33.6** `GrowthSourceMirrors`+`CardRemovalValueMirrors`、战前 API v6 · **0.35.2** `ModelPredictionStateMirrors` · **0.38.2** 适配接口第一批集中发布(`AfterSideTurnEndLateMirrors`、`AfterEnergyResetMirrors`、`PredictionCardReferences`、`AdaptedCardOnPlayMirrors`;AutoWatcher 的最低门槛即此)· **0.39.0** RitsuLib 0.6.0 拆分程序集(所有适配 mod 构建环境要跟)· **0.43.2** 不支持的第三方牌从静默变显式提示 · **0.44.1** `BeforeSideTurnStartMirrors`+`AfterPlayerTurnStartMirrors` 三轮 · **0.45.0–0.47.1** 无新登记入口,搜索优化为主;`KnownPreRootSubscriberTypeNames` 公开入口仍"待做"。

## 12. 相关材料

- 求解器:`.reference/CombatSolver-latest/docs/THIRD_PARTY_ADAPTERS.md`(手册)、`third-party-strategic-effects.md`、`third-party-model-state.md`、`third-party-onplay-patches.md`、`third-party-turn-phase-mirrors.md`、`HEADLESS_TESTING.md`、`ARCHITECTURE.md`
- 范例:`.reference/AutoWatcher/`(`README.md` 的"已知缺口"是诚实降级的实例;`docs/PLAN.md`/`docs/VERIFICATION.md` 是坑与核对台账)
- 被适配对象:`.reference/Watcher-decompiled/`(观者 0.9.29 反编译;抽查确认 `DevaPower.Instances` 走 `_internalData`、`EndTurnDeathPower._appliedOnTurn` 私有字段、`Wrath.ModifyDamageMultiplicative` 只读钩子——与 AutoWatcher 钉死的成员一一对应)
- 调用方集成(另一种适配):本仓库 SeedOracle(`docs/combat-solver-precombat-assessment.md`、`docs/capability-audit.md`)
