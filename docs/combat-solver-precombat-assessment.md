# Combat Solver 战前预测接入评估与原型结果

原始评估对象：Combat Solver `0.28.3`，对应当时 Workshop live DLL 与提交 `57574c9b12f24db3508cd578c12749ad85cc4e`。历史原型曾将 API 改动重放到作者 `v0.29.1`（`f63c57c`）之上；当前状态见下方更新。

当前状态（2026-09-07）：PR #43 已合并到作者 `main`，public API v5 最初进入合并提交 `0552b33`；本次审计使用作者工坊条目 `0.31.2` 验证该 API。Seed Oracle 的清单只把 `0.31.2` 作为最低兼容门槛，后续主 Mod 版本按 `min_version` 下限自动满足；本文件中 `0.29.x`/`0.31.1` 的版本号和 runId 保留为历史验证记录，不代表精确依赖锁定。

## 结论

历史上作者 Workshop `0.29.1` 没有 Seed Oracle 所需的 public API；当前作者主线 `0552b33` 已提供 public API v5。完整战斗初始化和搜索放入静音、可复用的独立 headless 游戏进程，主进程只捕获、校验并展示不可变结果。主进程初始化时钉住本次会话实际载入的 Mod 文件，避免运行中工坊更新改变 worker 环境。Seed Oracle 打开面板和 Hover 都不会启动计算；玩家逐项选择确定路线或纯模拟样本。

## 已确认的限制

- `CombatRootSnapshot.Capture(CombatState)` 只接受已经存在的战斗状态；它要求本地玩家已有 `PlayerCombatState`，并从当前手牌、牌堆、能力、怪物意图和战斗 Hook 构造根快照。
- `SolverController.RequestSearch(...)` 面向当前 active combat。它检查全局 `CombatManager`、当前 Play 阶段和求解器全局会话，并会更新 Combat Solver 自身的搜索、缓存与 Overlay 状态。
- 游戏的 `CombatManager.SetUpCombat(...)` 会重置玩家战斗状态、用 `Shuffle` RNG 建立牌堆、注册全局战斗状态与网络卡牌数据库。
- `CombatRoom.StartCombat(...)` 还会生成怪物、消耗 `Niche` RNG 生成 HP、触发 `AfterRoomEntered` / `BeforeCombatStart` 等 Hook，并启动全局回合循环。
- 只构造一个 `CombatState` 不足以求解；手工复制剩余初始化流程会重复游戏和 Combat Solver 的内部实现，而且容易遗漏 Mod Hook、开战遗物、抽牌、初始能力、怪物意图及多人缩放。

因此，在地图悬停时对 live 玩家调用战斗初始化会直接干扰随机数和游戏状态；对自制的不完整副本求解则会给出看似精确但实际不可信的战损与药水结果。

## 可安全接入所需的最小上游能力

Combat Solver 需要提供一个稳定的 public PreCombat API，至少满足：

1. 输入序列化的玩家/牌组/遗物/药水、目标 Encounter、目标楼层与地图坐标，以及全部相关 RNG 快照；
2. 在与 `RunManager`、`CombatManager`、UI 和网络单例隔离的状态中完成开战与首回合初始化；
3. 复用 Combat Solver 自己维护的 Hook/Mod 兼容层；
4. 异步返回预计战损、药水使用数量/名称、搜索边界和可信度，不改动求解器当前战斗会话；
5. 在 API 内部及调用前后比较 live state/RNG 指纹，任何差异都拒绝返回结果；
6. 对显式手动操作提供独占取消、强制重算、搜索预算和并行度；取消必须终止并等待其拥有的 worker；
7. 对远端目标接收连续、已确定且无中间战斗的地图步骤，以正确建立目标 `TotalFloor`、坐标和房间历史；未决 Event 不允许跳过。

原型接入后，Seed Oracle 按“跑局指纹 + Encounter + 目标楼层/列坐标 + 中间路径 + 入战 HP 场景 + 搜索预算/并行度”缓存确定结果。面板不会建立确定路线批量队列，每一行只在玩家点击后计算；同状态同设置重新打开时直接恢复成功结果。纯模拟样本使用独立 API 和样本种子，不混入确定结果缓存。没有 public API v5 时，地图提示仍只显示 Seed Oracle 能够在独立克隆状态中证明的 Encounter、怪物阵容和相邻战斗 HP。

## 原型实现

- 主线程用 `RunManager.ToSave` 捕获完整 `SerializableRun`，清除非语义时间/平台字段，修正 `SerializableMapPoint.CanBeModified` 的省略值反序列化不对称，并移除恢复后会从空对象变成缺失值的事件历史 `variables:{}`；非空变量原样保留。规范化字节与当前房间身份共同形成 SHA-256 状态令牌。
- Combat Solver 在游戏目录下管理带所有权标记的私有运行区，硬链接游戏文件并镜像主进程实际加载的精确 Mod 集合。一个 worker 会话使用独立用户目录、关闭 Steam 和 NoGC，并把隔离副本中的主音量、BGM、音效与环境音全部写为零；真实用户设置不变。
- 相同游戏根、用户根与 Mod 集合的连续请求复用同一 worker。每项完成后必须先返回主菜单并等待后台活动归零，再写出匹配 runId 的 ready 屏障；失败、超时、取消、显式关闭或达到所选空闲期限会终止该进程。“一直维持”只取消空闲计时，不取消所有权清理。
- worker 直接恢复完整跑局、加载当前幕地图，再把恢复状态重新规范化并与输入逐字节核对。核对通过后，先补记连续的中间非战斗地图历史、应用可选入战 HP，再用目标坐标经游戏原生房间入口建立战斗，使目标楼层/坐标派生种子、开战遗物、Mod Hook、怪物 HP/意图、抽牌和全部 RNG 消费都发生在子进程。
- 搜索复用 Combat Solver 现有短搜索管线，返回预计战损、药水 ID/标题/回合/槽位、边界、可信度、结束回合和诊断日志。
- 返回前切回主线程，重新检查活动 `RunState`、无 active combat 及完整状态令牌。任何变化返回 `LiveStateChanged`；失败、超时与取消均为显式状态。
- worker 环境关闭 PreCombat API，因而即使加载 Seed Oracle 也不会递归启动下一层 worker。
- v5 公开 worker PID、忙闲、工作集、私有内存、峰值、静音状态与可空空闲期限，并提供显式停止和重启/预热。界面可选择保活 2、10、30 分钟、一直维持，或在 reusable 屏障后立即关闭；切换有限期限会为已待命 worker 重新计时，多样本批次内部仍复用一个进程。
- `SimulateAsync` 只接受当前幕原生遭遇。它在完整快照恢复哈希通过后、怪物生成之前替换遭遇局部 RNG，以及洗牌、怪物行动、Niche 和其他战斗相关 RNG；`UpFront`、地图、奖励与主进程 RNG 不变。

## 接入范围

Seed Oracle 枚举从当前位置出发的路线，并只保留每条路线上的第一场确定战斗。目标前可以经过已解析的篝火、宝箱或商店；出现中间战斗或未决 Event 时停止延伸。面板用沿用地图标记体系的彩色三角形、正方形、菱形等图形标识路线，悬停行会高亮地图目标和路径；每行单独计算。对远端基线明确写为“沿途状态保持当前值”，若有前置篝火则另建“休息后入战 HP”场景。后者只应用游戏当前休息回复量；拿牌、拿遗物、药水、购买、锻造和其他触发仍须在实际状态变化后重新计算。

“咱俩碰一碰”与确定路线分栏显示。玩家可选 0～10 次，以及当前幕随机弱怪池、随机强怪池、随机精英池、指定普通怪组、指定精英怪组或当前 Boss。遭遇标题统一通过游戏 `LocString.GetFormattedText()` 解析为当前语言；每个样本显示怪组、预计战损、终局生命、用药、结束回合和搜索可信度，批次汇总最低/平均/最高战损。界面持续声明怪物组与生命、开局洗牌、怪物行动和其他战斗随机数均为假设，仅供威胁比较。

该方案仍有明显成本：每次 worker 会话的首次预测需要建立文件镜像并启动完整游戏进程，后续逐项预测会复用已经回到空闲状态的进程；Windows 是当前唯一实现的平台，第三方 Mod 只有在能够被同一 headless Mod 集合加载时才能参与。它换取的是清楚可审计的进程隔离，而不是在 live 单例上做不可证明的回滚。

## 验证结果

- `PRECOMBAT-API-FINAL-004`（runId `7da852343994495e923dec58bf624b28`）：双进程往返通过，内层返回毛绒伏地虫预计战损 `5`、药水 `0`、边界 `None`，外层完整状态令牌保持不变。
- `PRECOMBAT-API-SEED-STACK-005`（runId `1961ef8c7d484da691e07cec99074215`）：RitsuLib、Combat Solver、Random Foreseer、Seed Oracle 四 Mod 同时加载；Seed Oracle 成功探测初版 API，worker 对同一精确 Mod 集合完成恢复和求解，没有递归创建 worker。
- `PRECOMBAT-POTION-METRICS-006`（runId `5e281451a3534051b605577cd51c9038`）：强制用药短搜索在结果协议中返回 `FIRE_POTION`、本地化标题“火焰药水”、第 `3` 回合和槽位 `0`，验证非空药水动作链路。
- `PRECOMBAT-REMOTE-CAMPFIRE-007`（runId `fe93b060ada64e78864fca8d825aeb85`）：从含历史 Event 的完整快照恢复，补记“篝火→宝箱”路径，用目标坐标进入第 11 层战斗；休息后 `66 HP` 入战，返回战损 `12`、最终 `54 HP`。
- `PRECOMBAT-API-MANUAL-V2-008`（runId `f8db7c03ea9444cc89483495c985f221`）：public API v2 双进程往返通过；空 Event 变量规范化、非空保留、目标坐标和入战 HP 覆盖生效，live 状态令牌不变。
- `SEEDORACLE-PRECOMBAT-PANEL-V2-STACK-FINAL`（runId `501485530e6a4453814bbb742aae9666`）：RitsuLib、Random Foreseer、Combat Solver API v2 与 Debug Seed Oracle 同时加载；地图创建阶段的自检通过面板/顶部按钮构造、显示层级、路线层、悬窗边界及完整预测回归。该 headless 时点尚未开放地图旅行，因此手动场景数为 `0`。
- `PRECOMBAT-API-SEED-STACK-0.29.1-FINAL`（runId `2e038fa1be6b45a19a7a5096c9be0f16`）：两层 API 改动重放到作者 `0.29.0` 后，四 Mod 组合加载本地 Combat Solver `0.29.1.0`；外层 Seed Oracle 记录 `precombat_public_api_v2=true`，API 返回入战 `79 HP`、战损 `5`、药水 `0`、边界 `None`，调用前后 live 状态令牌一致。
- `PRECOMBAT-API-MANUAL-CACHE-REUSE-009`（runId `08fc0ae91d39417b9c234bbc5d2da6ea`）：四 Mod 组合加载本地 Combat Solver `0.29.2` 与 Seed Oracle `0.1.14`，外层同时记录 `precombat_public_api_v3=true`、`reusable_isolated_worker=true` 与 `isolated_worker_audio_muted=true`。相同快照的两次强制预测使用同一 PID，计数为 `starts=1 / reuses=1`，战损均为 `5`，四类隔离音量均为 `0`，live 状态令牌不变。首次 game startup 约 `12.57 s`，复用请求为 `0.6 ms`。
- `PRECOMBAT-API-V4-WORKER-SIMULATION-010`（runId `61f19e8db7ae4f9a8a217424e602f075`）：四 Mod 组合加载 Combat Solver `0.29.3` 与 Seed Oracle `0.1.15`；预热返回 PID、工作集、私有内存和静音标记，同一 worker 完成两次一致的确定预测和一个带内层 RNG 应用确认的假设样本，计数为 `starts=1 / reuses=3`。样本结束自动关闭，live 状态令牌保持不变；Seed Oracle 的 UI 自检同时要求 11 个次数选项、6 个对象选项和完整风险声明。
- `SEEDORACLE-PRECOMBAT-V4-UI-011`（runId `505a961bceaf4e3ab8ffa74e347420b5`）：四 Mod 组合在 Seed Oracle UI 自检开启时成功构造新面板，验证 0～10 共 11 个次数选项、6 类模拟对象、两种后台保留策略、内存状态、关闭/重启控件，以及包含怪物生命、开局洗牌和战斗随机数的完整风险声明。
- `PRECOMBAT-API-V5-UPSTREAM-0291-013`（runId `e2236a4ec85041dc95c0b3417ed0b688`）：API v5 重放到作者 `v0.29.1` 后通过四 Mod 回归；2/10/30 分钟和一直维持均生效，同一 worker `starts=1 / reuses=3`，内存与静音状态可见，确定预测、假设 RNG、自动关闭及 live 状态不变均通过。
- `SEEDORACLE-PRECOMBAT-V5-LOCALIZATION-014`（runId `e15b744f36e84d85aa8771cf02328121`）：UI 自检验证五种后台策略，并逐一要求当前幕原生遭遇标题经 `LocString.GetFormattedText()` 解析，不再显示原始 `LocString … .title`。
- `PRECOMBAT-MOD-PIN-015`（runId `8382cbd40ce74a44ac2cac7e444f6403`）：十 Mod 组合加载 Combat Solver `0.29.5`、Seed Oracle `0.1.18` 与 HowlFromBeyondBgm `1.1.5` 等主进程版本；工坊式原子替换后，启动期硬链接快照仍保留原文件。两次确定预测、一次假设样本、worker 复用、内存、静音、全部保活期限、自动关闭和 live 状态不变均通过。
- Combat Solver 与 Seed Oracle Release 均为 `0` warning、`0` error；Windows 结构门禁通过。地图上的异步刷新、文字换行和真实鼠标悬停仍需可见游戏验收。
