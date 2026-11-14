# 交接文档:杀戮尖塔 2 未知随机数决策网络

> 写给下一个接手的模型/开发者。目标、现状、已验证路径、边界和下一步。

## 一、结论先行

**STS2 已经支持无 UI 的快速推进，而且仓库里有不止一条路径。** 先不要因为“无随机数决策”而把整套 CombatSolver 语义翻译到 Python。

| 路径 | 是否启动完整 Godot | 适用范围 | 关键事实 |
|---|---:|---|---|
| sts2-cli | 否。使用真实 sts2.dll + GodotStubs | 整局/战斗的真实引擎推进、JSON 控制、对拍 | 已实跑真实 Neow 和完整战斗；不是 CombatSolver |
| CombatSolver OfflineSearchHarness | 否。普通 .NET 进程，绕过 Godot 场景树 | 高吞吐战斗搜索、宽度/预算对比 | 直接加载 sts2.dll，构造战斗并调用 CombatRootSnapshot / CombatSearchCoordinator |
| CombatSolver PreCombatForecast / unattended worker | 是，进程无可见 UI | 独立 worker、战斗前 forecast、显式 sample seed | 使用 SlayTheSpire2.exe --headless --disable-vsync --max-fps 0 --force-steam=off；可设 FastMode=Instant |
| STS2 AutoSlay | 通常是测试/非 Release 路径 | 官方自动跑局 smoke test | 证明游戏本体有 unattended runner，但不是当前决策网络的 API |

**关键拆分：**

1. headless、Fast、Instant 解决的是 UI、动画、Task/命令等待和进程吞吐；
2. “不知道随机数确切结果”是决策层的信息边界；
3. 当前 CombatSolver 已经是可复用的语义/搜索后端，但它的默认搜索仍然会捕获、复制和推进实际 RNG 流；
4. 因此可行路线是 **复用 CombatSolver 语义，将未知未来独立采样后评估当前动作**。CombatSolver 已补上内部 blind/sample 入口并接入 OfflineSearchHarness：独立九条 RNG、重排未知抽牌顺序、重建未来意图。当前首动作复评已经改为逐步观察的可执行轨迹，并接入原生药水观察选牌；Mod 在线 UI、任意战斗中途恢复和复杂原生嵌套选择仍待后续阶段。

项目目标仍是：在不知道随机数确切结果的前提下，根据可见状态选择动作，并用引擎真值评价模型估值与真实结果的偏差分布。

### 2026-09-28 当前实施状态

工作分支为 `.reference/CombatSolver-latest` 的 `codex/blind-rng`，已同步 `origin/main@7236330`（0.47.2 及后续修复）。当前实现与可读证据见[随机池与逐步观察策略](../.reference/CombatSolver-latest/docs/strategy/blind-pool-policy-20260928.md)。

接手扩展随机牌、药水和机制时，使用[未知 RNG 扩展交接指南](../.reference/CombatSolver-latest/docs/strategy/blind-rng-expansion-handoff-20260928.md)：行为基线为 `a4b58d0`，包含代码职责、机制分类、逐批流程、验收模板、已踩坑和可直接交付的任务说明。

- 随机池动作即使没有确定化样本提名，也补入完整首命令矩阵；攻击/技能药水实际三张与跳过均有独立内层续搜评分。酸和添柴先完整结算生成，再观察手牌决定下一步。
- 外层样本不再直接采用知道自身未来的完整路线。每个后续决策都从新观察独立采样；相同观察、不同外层隐藏未来用两个新建决策器核对，不靠缓存假通过。内层仍用确定化提案，不能称为完全消除一切估值偏差。
- 七个最小无头场景合计 94 项检查通过，包含原生攻击/技能药水和弃牌重抽的完整状态/RNG 对账，以及跨回合蓄能和立即击杀正反例。酸有一条时间截断，只计前缀收益。
- 之前玩家存档指导的 9 场结果属于旧算法证据，本轮没有重复完整通关或统计胜率测试。原档只读，宿主保存后端为 `MemorySaveStore`。
- 全局可见字段白名单仍未完成，尤其是怪物私有 AI 和第三方隐藏字段。不要把真实 RNG/隐藏牌序独立性写成严格的全部可见信息保证。

## 二、基础设施

| 组件 | 位置 | 说明 |
|---|---|---|
| sts2-cli | D:\FORSTS2\sts2-cli | 无头真实引擎，JSON stdin/stdout；跑真 sts2.dll。已加导出消耗堆和药水 id 字段 |
| turn-solver | D:\FORSTS2\turn-solver | 当前代码：引擎驱动、回合分解求解器、MPC、分级对拍、全跑图/回放 |
| CombatSolver 源码 | D:\FORSTS2\.reference\CombatSolver-latest | 已同步上游 0.47.2。OfflineSearchHarness 的 blind 模式运行逐步观察轨迹；游戏内默认 UI 搜索仍使用确定化 RNG |
| 游戏反编译源码 | D:\FORSTS2\.reference\sts2-v0.111.0 | 全量卡牌/怪物/遗物/药水 C# 源码 |
| CombatSolver OfflineSearchHarness | D:\FORSTS2\.reference\CombatSolver-latest\tools\OfflineSearchHarness | 不启动 Godot 的 .NET 9 宿主；已支持原生战前存档、历史指导、战斗推进与观察选牌 |
| CombatSolver 文档 | D:\FORSTS2\.reference\CombatSolver-latest\docs\OFFLINE_SEARCH_HARNESS.md、HEADLESS_TESTING.md | 分别描述离线搜索宿主和完整游戏 headless 测试 |
| 官方翻译 | D:\FORSTS2\sts2-cli\localization_zhs | 卡牌/怪物/遗物/药水官方中文名 |
| 模型克隆 | D:\FORSTS2\.reference\mods-decompiled | 尖塔军师/AutoSTS2 反编译 |
| Python | C:\Users\slimoon\AppData\Local\Python\bin\python.exe | 非 Store 版 |

引擎启动环境变量：

    STS2_GAME_DIR=D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64

已构建：

    sts2-cli\src\Sts2Headless\bin\Debug\net9.0\Sts2Headless.exe

## 三、目标与修正后的架构

    真实 STS2 / sts2-cli
            │ 主线程稳定捕获根；当前直接使用 CombatSolver 内部根
            ▼
    blind RNG 适配层
            │ 保留可见手牌/状态/当前意图；重采样未来 RNG 和未知抽牌顺序
            ├─ K 个独立 sample seed
            │      ▼
            │  现有 CombatSolver 语义 + CombatSearchCoordinator
            │      ▼
            ├─ 收集首命令并补齐当前准入的全部合法动作
            └─ 每候选 × 每样本执行逐步观察轨迹，按均分、票数或尾部风险聚合
                           │
                           ▼
                  在真实引擎发起一个命令（无头已接入，Mod UI 待做）
                           │
                           ▼
                     选择页/稳定决策点重观察、重锚定、重解

因此五层应写成：

- **L1 内容认知/语义后端**：优先复用 CombatSolver；Python CARD_SPECS 作为降级、诊断和未接入后端时的 fallback，不再把“系统性移植全部 vanilla 语义”当成主路线。
- **L2 状态层**：引擎权威 + 可见状态 + 事件增量计数器。
- **L3 回合内搜索**：CombatSolver 对独立内层样本搜索，提名当前命令；执行外层轨迹时，每一步都重新观察和提名。Python 精确搜索保留为诊断/回退。
- **L4 收益模型**：对外层可执行轨迹做期望、尾部风险和跨回合评价；内层确定化提案分数与完整/截断轨迹分开报告。
- **L5 在线 MPC**：执行一步 → 重观察 → 重解，任何未知 RNG 都不跨步假定。

关键原则：

- **sample 内复用既有语义，跨 sample 评估**：单个 sample 可逐字段验证；未知随机性用独立假设的结果分布表示，保留搜索边界。
- **决策前替换隐藏未来**：完整根中的九条 RNG、未知牌序、未来意图及后续创建 RNG 在采样入口替换；全局可见字段白名单仍待审计，不能把当前 RNG 独立性证明扩成所有字段的保证。
- **MPC 重锚定**：执行一步后从真实引擎重新观察，避免 sample 轨迹误差跨步累积。
- **明确未支持边界**：CombatSolver 不支持的语义或选择来源显式失败/形成边界；Python fallback 的 invariant/resync 只属于其自身差分口径。
- **快速不等于盲随机**：FastMode=Instant 只是跳过动画/等待；它不会隐藏、重采样或分布化 RNG。

## 四、当前状态与实证

### 早期路径证据（保留历史口径）

以下 Python/CLI 与最初 blind 的数值来自各自当时的实现；最新行为基线和 94 项专项检查见第一节及扩展交接指南，不能把早期耗时、评估次数或胜利数当作当前策略的成绩。

- 回合内精确搜索：≤10 手牌，单步约 0.1~0.5ms，≤1k 节点。
- 全池 87 卡（铁甲）全跑图：10/10 胜，平均战损 9.0。
- 分级对拍：strict（逐字段）、invariant（总量守恒）、resync（仅能量+手牌数）。
- 漂移检测：模拟器计数器 vs 引擎真值逐步比对。
- 铁甲胜利局回放：路线、选牌、锻造、药水按记录复刻。
- sts2-cli 真实 headless 启动：返回 ready，加载 ModelDb 1660 项，启动 Ironclad run 并返回真实 Neow event_choice，随后正常 quit。
- sts2-cli 驱动完整战斗：turn-solver/run_fight.py validate --seeds 1 --encounter CULTISTS_NORMAL --deck starter 返回 won=true、rounds=8、steps=22、diff_count=0、truncated_steps=0。
- CombatSolver 离线宿主：文档和工程均存在，普通 net9 console 不启动 Godot，直接构造 game-owned combat 并调用 CombatRootSnapshot.Capture / CombatSearchCoordinator.Solve。
- CombatSolver PreCombat API：已有 SimulateAsync / SimulatePlanningAsync；调用方可提供 sample seed，sample 只在隔离 worker 内替换 encounter/combat RNG，不修改 live run。
- CombatSolver blind 合同：121 项通过，覆盖九条 RNG 原生序列、Fork 别名隔离、同 seed 对原隐藏 RNG/抽牌顺序的独立性、显式已知堆顶和聚合反例。
- CombatSolver blind 实跑：IRONCLAD / `CULTISTS_NORMAL` / 3 samples / 300 nodes / 3000 ms；2 候选（含 EndTurn）、6 次评估，首命令 BASH。DOP1/Majority 和 DOP2/MeanScore 的评估矩阵在去除耗时后完全一致；9 次请求共 2700 展开、8856 转移。
- blind 边界夹具：零能量时 1 候选、2 次评估、返回 EndTurn；含 FIRE_POTION 的 DOP2/RiskAverse 场景实际评估药水和 EndTurn 各 3 次。所有采样夹具的搜索后 live/shadow 重捕获均与原根一致，route 只有一个命令，原根戳及续用不导出。
- 原确定性模式烟测通过，仍输出 14 个路线动作和根戳；Release/宿主编译均为 0 警告/0 错误，Windows 结构门禁通过。未启动可见游戏，未提升版本或发布。

### 已知缺陷

| # | 缺陷 | 根因 | 修复方向 |
|---|---|---|---|
| 1 | Python fallback 的敌人受击反应未完整覆盖 | 需要逐怪物反应注册表 | 若继续用 Python，移植 AfterDamageReceivedMirrors 同族语义；接 CombatSolver 后由后端覆盖 |
| 2 | 成长折现不可见 | 效用缺少未来收益项 | 在 L4 加剩余回合、命中数和成长威胁折现；可参考 StrategicEffectModel |
| 3 | 击杀时限意识缺失 | 缺少剩余回合和敌余血负债 | 在收益模型加入 RemainingTurns 与每轮净亏血 |
| 4 | Python 通用推断只覆盖直线效果 | 球、召唤、变形、条件组合等语义覆盖不足 | 优先通过 CombatSolver 后端取得语义；Python 只补接入不了的效果并强制 resync |
| 5 | 非铁甲职业零建模 | Python 通用体覆盖有限 | 由 CombatSolver 后端先覆盖；再按评价范围补职业适配 |
| 6 | CombatSolver 默认搜索会看到确定化 RNG | CombatPredictionRngSet 捕获九条流并在 Fork() 中复制；CardGenerationResultKind.Random 只是来源分类 | 已增加可选 blind 入口和外层观察策略轨迹；原生无头执行已接入，默认确定性模式保留，Mod 在线 UI 未接入 |

## 五、已确认的实现边界

1. **奖励掉落与战斗无关**：Player.PlayerRng.Rewards 是玩家级流；CLI 忠实调用本体，同种子不同打法的战后奖励逐张一致。
2. **卡牌数值不可盲信手写**：STS2 与 STS1 语义/数值有大量差异，必须从引擎或源码核实。
3. **引擎手牌导出的伤害是已结算值**：含易伤/力量，模拟器不能重复乘。
4. **某些卡的 hits 在 OnPlay 循环里**：例如 TWIN_STRIKE，预览 repeat 不足以覆盖。
5. **怪物意图部分是确定模式**：未知怪仍需以当前意图和 sample/实测为准。
6. **默认 CombatSolver 的 RNG 是真实状态的副本**：普通搜索从 RunState.Rng 捕获并分叉；可选 blind 入口用 sample seed 替换未来流，同时重排未知牌序及重建未来 forecast。只换 RNG 而保留真实未来牌序或意图仍会泄露隐藏信息。
7. **HeadlessFastModeForTest=Instant 只改时序**：UnattendedTestRunner/CombatInstantModePatch 将偏好设置为 Instant，目的是跳过动画和等待。
8. **离线宿主已有原生推进**：支持生成战斗、当前 schema 的原生战前 `.save`/可解码 `.mcr`、历史指导及逐命令战斗推进，选择页默认 Blind；仍不替代完整无人差分，也不支持任意保存的 mid-combat checkpoint。
9. **PreCombat API 的公开边界在战斗前**：现有公开接口主要接收完整序列化 run / planning run；在线战斗内需要新增窄接口或 worker protocol 才能把可见状态交给 CombatSolver。
10. **sts2-cli 与 CombatSolver 是两层**：sts2-cli 是真实引擎 rollout/oracle，不会因为存在而自动提供 CombatSolver 的策略搜索。

## 六、路线修正：复用 CombatSolver，只补 blind RNG 边界

### 为什么不再优先做全量语义移植

CombatSolver 已经同时具备：

- vanilla 内容镜像和效果语义；
- 可分支的 combat snapshot/search；
- 不启动 Godot 的高吞吐 OfflineSearchHarness；
- 完整游戏 headless worker；
- PreCombat 的显式 sample seed 能力。

把这些语义再翻译到 Python，会重复维护卡牌、Power、怪物反应和 RNG 细节，且仍要重新解决漂移。真正缺的不是“第二套 vanilla 语义”，而是把 CombatSolver 的确定性样本包在一个不泄露 live RNG 的决策边界后面。

### 推荐的最小可行方案

对每个决策点：

1. 在稳定单人 Play 阶段捕获引擎根；当前接口使用内部完整根，尚无独立的可见状态序列化协议；
2. 生成 K 个独立 sample seed，构造隔离分支，替换九条 RNG、未知抽牌顺序和未来意图；已知堆顶长度须由观察者明确提供；
3. 对每个样本运行现有 Coordinator 提名首命令，再补齐当前枚举入口准入的全部合法首命令，包含 EndTurn；
4. 每个候选在全部外层样本上实际执行首命令，动作后冻结观察；使用固定 DecisionSeed 派生独立内层样本，逐步提名并执行下一命令，到终局或预算边界；
5. 按外层轨迹票数、完整矩阵均分或尾部风险选命令；完整轨迹、截断前缀与内层提案分数各有明确口径；
6. 原生无头层已发起一个命令，在实际选择页观察候选并决策；动作完成且状态稳定后重新捕获和采样。Mod 在线 UI 及复杂原生嵌套选择仍待接入。

伪代码：

    frozen = capture_stable_engine_root()
    samples = [frozen.fork_for_sample(seed) for seed in sample_seeds]
    discovery = [solve(sample) for sample in samples]
    candidates = first_commands(discovery) | admitted_first_commands(frozen)
    policy = observed_policy(independent_inner_seeds=DecisionSeed)
    matrix = {
        (command, seed): execute_observed_trajectory(sample, command, policy)
        for command in candidates
        for seed, sample in zip(sample_seeds, samples)
    }
    # trajectory 每步结算后重观察，下一命令不沿用该外层样本的全知路线
    command = aggregate(matrix, objective="mean_or_tail_risk_or_trajectory_votes")
    # 无头已接入；Mod UI 待做。发起后才处理实际可见的选择
    engine.initiate(command_without_sample_choices(command))
    resolve_native_observed_choices()
    frozen = observe_next_stable_decision()

这里的 seed 是模型用来表示未知未来的假设，不是 live run 的 RNG 结果；sample 结果不能写回 live run，也不能进入跨步的“已知事实”。

### 接入顺序

| 阶段 | 内容 | 验收 |
|---|---|---|
| A | CombatSolver 内部增加 stable root + sample seeds 入口，替换隐藏 RNG/牌序/未来意图，复用现有 search | **已完成**：编译及 121 项采样/Fork/聚合合同通过；尚无通用可见状态 DTO |
| B | OfflineSearchHarness 补齐准入首命令并执行全候选 × 全外层样本的观察策略轨迹，只输出当前命令 | **已完成当前范围**：七个最小场景合计 94 项检查；搜索根不变、独立策略扰动检查、完整/截断口径已登记 |
| C | 真实引擎逐命令 MPC、已揭晓选择及执行后重观察 | **无头已接入**：原生攻击/技能/弃牌重抽药水完整 continuation/RNG 验收；Mod UI、任意中途恢复、复杂嵌套选择待做 |
| D | 多 seed 原生 rollout 的质量校准与偏差分布 | 待扩展：既有九场玩家历史指导属于旧算法证据；当前未做统计胜率或完整通关 |
| E | 按机制族扩展随机池覆盖与逐来源验收 | 执行扩展交接指南；通用选择评分、内层乐观性与可见字段白名单单独安排 |

### 当前不能直接承诺的部分

- 现有 CombatSolver public API 没有通用的“在线 mid-combat blind policy”入口；CombatRootSnapshot 和 coordinator 的关键部分仍是内部实现。
- OfflineSearchHarness 当前不是任意存档/任意回合的恢复器；要做在线接入需补 root 构造或协议。
- 多 sample 聚合是未知 RNG 的可行近似/策略评估方式；它不是把当前确定性 solver 变成严格的全分布解析器。
- 当前首动作已补齐准入枚举并由可执行外层轨迹评分；内层仍用确定化搜索提名和评价药水选项，仍可能乐观。全局可见字段白名单也未完成，不能称为精确的部分可观测最优规划器。
- 搜索边界上的存活和预算内完整胜利比例不是经真实 rollout 校准的整场胜率；截断项的 HP 只代表已执行前缀。
- `--budget-ms` 是每样本发现阶段的软预算，评估阶段另将同等预算按候选数平分；内层受 `--blind-policy-nodes` 等边界约束，选牌逐选项续搜。相同配置不保证相同轨迹深度。总成本使用 `blindPolicy.TotalExpandedNodes/TotalTransitionCount/Elapsed`；普通指标属于代表样本。

## 七、Python fallback 接口（只在未接后端或诊断时使用）

turn-solver/model.py 的 CARD_SPECS/SimState 仍可作为低成本诊断和回退路径。新增卡牌时：

    CARD_SPECS = {
        "CARD_ID": {
            "kind": "attack|skill|power",
            "hits": int,
            "aoe": bool,
            "block": True,
            "draw": "cards",
            "energy": "energy",
            "apply": {"Power": amount_or_stat_key},
            "self_apply": {"Strength": "strengthpower"},
            "force_level": "resync",
        },
    }

效果骨架不应成为继续复制全部 vanilla 语义的理由；接入 CombatSolver 后，只保留诊断、差异修复和没有后端覆盖的显式降级。

## 八、运行命令与证据入口

    PY=C:/Users/slimoon/AppData/Local/Python/bin/python.exe
    cd D:/FORSTS2/turn-solver

    # 单场验证
    $PY run_fight.py validate --seeds 3 --encounter CULTISTS_NORMAL --deck rich -v

    # 已验证的严格对拍样例
    $PY run_fight.py validate --seeds 1 --encounter CULTISTS_NORMAL --deck starter

    # 全跑图
    $PY full_run.py 3 Ironclad 0

    # 胜利局回放
    $PY replay_run.py winrun_ironclad.json -v

    # 精英战决策审计
    $PY trace_decision.py <seed> <encounter> <deck>

CombatSolver 未知随机数入口（工作目录 `D:/FORSTS2`）：

```powershell
dotnet .reference/CombatSolver-latest/tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --character IRONCLAD --encounter CULTISTS_NORMAL --seed blind-evidence-20260928 `
    --search-mode Coordinator --blind-samples 3 --blind-seed 12345 `
    --blind-aggregation MeanScore --nodes 300 --budget-ms 3000 --dop 1 `
    --out .local/blind-smoke
```

最新行为证据在 CombatSolver 仓库的 `.local/blind-policy-20260928/`，重跑入口和逐批验收见扩展交接指南；本次仅整理文档，不重跑已通过行为测试。早期 `.local/blind-evidence-final`、`blind-evidence-parallel`、`blind-evidence-endturn`、`blind-evidence-potion`、`blind-evidence-deterministic` 保留为历史证据；`blind-evidence-2` 更早，只聚合发现样本，不作为当前源码验收证据。

相关实现/证据：

- sts2-cli/src/Sts2Headless/RunSimulator.cs：inline synchronization context、TestMode、headless 初始化和等待补丁。
- sts2-cli/src/Sts2Headless/Program.cs：JSON stdin/stdout 协议。
- sts2-cli/src/Sts2Headless/GodotStubs/Core.cs：headless timer/display stub。
- D:\FORSTS2\.reference\CombatSolver-latest\tools\OfflineSearchHarness：无 Godot 搜索宿主。
- D:\FORSTS2\.reference\CombatSolver-latest\docs\OFFLINE_SEARCH_HARNESS.md：离线宿主限制、参数和 A/B 证据。
- D:\FORSTS2\.reference\CombatSolver-latest\docs\HEADLESS_TESTING.md：完整游戏 headless 与 FastMode 语义。
- D:\FORSTS2\.reference\CombatSolver-latest\src\Api\PreCombatForecastApi.cs：隔离 worker 与 sample seed API。
- D:\FORSTS2\.reference\CombatSolver-latest\src\Engine\InCombat\Simulation\CombatPredictionRngSet.cs：九条战斗 RNG 的捕获、分支和独立采样。
- D:\FORSTS2\.reference\CombatSolver-latest\src\Engine\InCombat\Simulation\CombatPredictionSimulator.cs：共享 Fork 所有权和采样 RNG 替换。
- D:\FORSTS2\.reference\CombatSolver-latest\src\Runtime\CombatRootSnapshot.cs：稳定根、未知抽牌顺序采样和样本戳。
- D:\FORSTS2\.reference\CombatSolver-latest\src\Search\BlindCombatSearch.cs：候选发现、全样本评估和当前命令聚合。
- D:\FORSTS2\.reference\CombatSolver-latest\src\Search\BlindCombatPolicy.cs：逐步观察的外层轨迹、独立内层提案和药水选择评分。
- D:\FORSTS2\.reference\CombatSolver-latest\tools\OfflineSearchHarness\BlindNativeChoices.cs：原生已揭晓选牌、逐实例匹配和 worker 取消排空。
- D:\FORSTS2\.reference\CombatSolver-latest\src\Testing\BlindSearchContracts.cs：采样独立性、九流/Fork 隔离及聚合反例。

## 九、官方中文名速查

来源：sts2-cli/localization_zhs/cards.json、monsters.json（或 turn-solver/names_zh.json）。

常见对照：BASH=痛击、THRASH=痛殴、BLUDGEON=重刃、IRON_WAVE=铁浪、UPPER_CUT=上勾拳、HAML=剁碎、INFERNAL_BLADE=地狱之刃、STOKE=添柴、TRUE_GRIT=坚毅、HAVOC=破灭、CASCADE=倾泻、CINDER=余烬、FIGHT_ME=与我一战、FLASH_OF_STEEL=亮剑、PACTS_END=契约终结、PRIMAL_FORCE=原始力量、ASCENDERS_BANE=升天者之灾、Bygone Effigy=旧日雕像、Calcified Cultist=钙化邪教徒、Damp Cultist=潮湿邪教徒、Corpse Slug=噬尸蛞蝓、Bowlbug=盛碗虫。

**禁止手译/使用一代名称。**
