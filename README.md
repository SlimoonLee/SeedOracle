# Seed Oracle / 种子先知

Seed Oracle 是《Slay the Spire 2》`0.111.0` 的路线信息前瞻 Mod。它在地图节点 HoverTip 中显示当前跑局已经能够确定的信息，并为每一项结果标注“确定”“当前世界线”“路线相关”或“暂不支持”。

当前版本实现：

- 普通战、精英战与 Boss 的 Encounter 队列前瞻；
- 枚举从当前位置到目标节点的可行路线，并统一用“绿色线路”“青色线路”等完整彩色线路对应不同结果；
- 每个颜色标签注明该结果包含的完整路线数；只有从当前位置连续沿同色线路走到目标才对应该结果，共线色带表示在分岔前仍有多种结果可能；
- 多种结果共用同一段地图边时，将颜色排成独立的不透明平行色带，避免后绘制路线覆盖前一条或因透明叠加而混色；
- 路线会保留到查看另一个节点或关闭地图，并按节点实际图标中心连接；路线覆盖层位于 HoverTip 下方，不再遮住文字；
- 修复中途推进后彩色路线落到地图背景层下方的问题；路线、顶部总览和 HoverTip 使用独立的显示层级；
- 地图顶部中央提供“先古 / Boss”按钮，按需展开或隐藏三幕总览；先古初始选项在序列化影子跑局中用事件独立 RNG 生成，已访问选项取自历史记录，未来幕中依赖牌组、遗物或进度的结果标成“按当前状态预测”；
- 对特兹卡塔拉、诺努佩佩、坦克斯与帕尔的动态候选池，用同一事件 RNG 分别计算条件成立和不成立的结果；只有实际选项发生变化时才显示“若条件成立：A 替换 B”，并注明其余条件保持不变；
- 不同路线结果拆成独立悬窗卡片，内容超过可用高度时由游戏原生布局自动换栏；追加全部卡片后会按每块实际面板的合并矩形重新对齐，并把整组面板夹在屏幕安全区内；
- Boss 前瞻把共同的遭遇与怪物信息只显示一次，并把不同路线掉落压缩到最多三块面板，同时保留每条彩色完整路线及其终点连线；
- 支持普通连线、羽翼之靴剩余次数和 Flight 修正下的路线可达性；
- 沿每条路线依次推进问号房间、Event、普通战、精英战和 Boss 队列，显示目标事件或战斗及怪物阵容；
- 使用 Encounter 的独立确定性种子生成怪物阵容；
- 对当前可行路线上的首场战斗（包括隔着不会改变战斗状态的宝箱、休息点或商店）用克隆的 `Niche` RNG 预测怪物 HP；
- 用克隆的 `UnknownMapPoint` RNG、赔率状态和 Hook 监听器预测路线上的连续 `?` 房间类型；
- 用克隆的 Event 队列和 Hook 监听器预测路线上的事件身份；
- 对 Random Foreseer 已支持的 31 类事件，在序列化影子跑局中初始化事件选项，并分别显示入场时原生选项已经生成的卡牌、遗物、药水与选择后的额外预测；滑脚木桥的首次无伤删牌、旺戈商店的精选遗物等不会因 Random Foreseer 只补充后续结果而遗漏；远端事件会先推进沿途已建模的奖励状态；
- 通过 Random Foreseer Adapter 依次生成从当前起第 N 次进店的基准库存，包括卡牌、遗物、药水、价格和删牌价格；
- 按可行路线推进克隆的 `Rewards`、`Shops`、卡牌/药水赔率和玩家遗物袋，显示战后金币、卡牌、药水与遗物；支持当前游戏内会改写基础奖励的遗物与自定义模式；
- 使用克隆的 `TreasureRoomRelics` RNG 与共享遗物袋预测宝箱遗物和金币，并处理银坩埚空箱与首次教学宝箱；
- 使用游戏支持的 `[gold]`、`[green]`、`[orange]` 和 `[blue]` 富文本标签显示预测状态与价格；
- 地图顶部提供“战损与模拟”按钮和可见面板；打开面板与地图悬停都不会启动 worker。每个场景有独立“计算”按钮，未选择的路线不排队；相同跑局状态与搜索设置会直接恢复缓存结果；
- 面板用与地图路线标记一致的彩色三角形、正方形、菱形等图形区分路线，悬停一行会在地图上高亮对应目标与路径；可选择单场搜索预算与并行度，并查看耗时、可信度和药水计划；
- 面板实时显示隔离 Combat Solver worker 的 PID、工作集、私有内存和静音状态；可选择保活 2、10、30 分钟、一直维持或任务结束后自动关闭，也可手动关闭及重启/预热；
- “咱俩碰一碰”页可手动运行 0～10 个假设样本，对象包括当前幕随机弱怪池、强怪池、精英池、指定普通/精英怪组和当前 Boss；指定怪组及样本结果使用游戏当前语言的本地化遭遇名称，逐样本显示战损、终局生命、回合和用药，并汇总最低、平均与最高战损；
- 纯模拟明确使用假定的怪物组与怪物生命、开局洗牌、怪物行动和其他战斗随机数，只用当前牌组、遗物、药水与生命比较威胁，不把远处未确定战斗显示成预测事实；
- 手动计算覆盖每条当前可行路线上的第一场确定战斗。目标前允许经过不会直接消耗战斗序列的篝火、宝箱或商店，目标坐标和中间楼层历史会传入隔离进程；未决事件和中间战斗不跳过计算；
- 若目标前有篝火，面板同时给出“沿途状态保持当前值”和“在前置篝火休息后入战”场景。休息分支在开战 Hook 前应用游戏当前规则算出的 HP，并明确标出尚未模拟的奖励、锻造与其他触发；
- 探测 Random Foreseer 与 Combat Solver 的版本和可调用能力；
- Debug 构建在每次地图预测前后比较 live state 指纹。

远端路线预测会在提示中注明条件：沿途事件选择、奖励选择、宝箱跳过、商店购买、补货和删牌若改变相关状态，后续结果也会随之改变。多人宝箱的投票结果同样属于条件分支。

当前开发版使用 Combat Solver `feat/precombat-api` 分支提供的 public API v5。战斗会在独立 headless 游戏进程中初始化和求解，主进程调用前后核对完整跑局/RNG 状态令牌。确定路线 API 接收目标地图坐标和连续的非战斗路径历史，使目标 `TotalFloor`、怪物局部种子及地图相关开战状态一致；纯模拟 API 则在隔离快照校验之后替换样本战斗 RNG。隔离用户设置中的主音量、BGM、音效与环境音全部强制为零。远端确定路线数值仍是清楚标注的条件结果：沿途若拿牌、拿遗物、用药、购买、锻造或触发其他状态变化，应在实际选择后重新计算。
设计评估与验证边界见 [docs/combat-solver-precombat-assessment.md](docs/combat-solver-precombat-assessment.md)。

## 当前兼容性

Seed Oracle `0.1.17` 需要 [SlimoonLee/CombatSolver](https://github.com/SlimoonLee/CombatSolver) 的 `upstream/precombat-api-rfc` 分支（本地 Mod 版本 `0.29.4`、public API v5）。[Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver) 的 `v0.29.1` 尚未包含该接口，不能直接替代这个扩展版本。

## 构建

复制 `local.props.example` 为 `local.props`，按本机路径调整后运行：

```powershell
dotnet build .\SeedOracle.csproj -c Release
```

默认会把 `SeedOracle.dll`、PDB 和清单部署到游戏目录的 `mods/SeedOracle`。

依赖：

- STS2-RitsuLib `0.5.13+`
- Random Foreseer `0.13.10+`
- Combat Solver 本地扩展版 `0.29.4+`（基于作者 `v0.29.1`，必须包含 public API v5；作者原版目前不提供此接口）

依赖能力审计见 [docs/capability-audit.md](docs/capability-audit.md)。

## 相关项目

- [STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)
- [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer)
- [Combat Solver](https://github.com/Torch1230/CombatSolver)（作者原版）
- [Combat Solver pre-combat API Fork](https://github.com/SlimoonLee/CombatSolver)（Seed Oracle 当前所需扩展）

## 许可证

Seed Oracle 自有源码按 [MIT License](LICENSE) 发布。游戏、游戏资产以及单独安装的依赖不属于本许可证；详情见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
