# 规划战斗验证 · 2026-09-08

环境：游戏 v0.111.0，SeedOracle Debug，本地 CombatSolver API v6。隔离游戏目录、独立 APPDATA/LOCALAPPDATA、关闭 Steam、无头 worker；未操作玩家实际进行中的跑局。

| 种子 / 场景 | 结果 | 证据 |
| --- | --- | --- |
| PLANCOMBAT01 / 普通战斗 | 3/3 完成，规划 HP 56 → 52、48、56 | [原始记录](planning-combat-PLANCOMBAT01.txt) |
| PLANCOMBAT02 / 问号点战斗 | 3/3 完成，规划 HP 64 → 64、64、64 | [原始记录](planning-combat-PLANCOMBAT02.txt) |
| PLANCOMBAT03 / 茂密植被前页回血再战斗 | 3/3 完成，战斗入口 HP 75 → 57、60、52；前页回放、战后奖励和独立重复执行一致 | [原始记录](planning-combat-PLANCOMBAT03.txt) |
| PLANCOMBAT04 / 木偶战斗后返回事件 | 3/3 完成，战斗入口 HP 58 → 75、75、75；原生 Resume 后完成事件，每次生成 1 瓶药水，重复执行一致 | 本次工具执行结果；逐行报告未另行归档 |
| PLANCOMBAT05 / 普通战斗收口检查 | 3/3 完成，战斗入口 HP 54 → 46、54、54 | [原始记录](planning-combat-PLANCOMBAT05.txt) |
| 原有面板 Debug 自检 | 通过：8 类地图点、164 条路线变体、商店、奖励、地图及模拟控件结构 | [输出](planning-combat-ui-selftest.txt) |

每个样本均检查结果写回、下一快照 HP/药水保留、过期参照拒绝；主进程牌组、HP、药水、遗物、RNG、历史、地图拓扑、遭遇计数和规则指纹不变。每轮仍执行原生奖励分组及初始事件审计，`nondeterministic=0 failed=0`；锁定或定制界面的降级项如实保留。

复跑入口（SeedOracle 仓库根目录；将路径替换为本机游戏、账号和依赖目录）：

```powershell
$common = @{
    Sts2Dir = 'C:\path\to\Slay the Spire 2'
    AccountDataRoot = 'C:\path\to\SlayTheSpire2\steam\<account>'
    RitsuLibDir = 'C:\path\to\STS2-RitsuLib'       # 工坊根目录或 lib/<版本>
    RandomForeseerDir = 'C:\path\to\RandomForeseer' # 工坊根目录或 lib/<版本>
    CombatSolverDir = 'C:\path\to\CombatSolver'     # 工坊根目录或本地 API v6 输出
}
pwsh -NoProfile -File smoke/run-planning-smoke.ps1 @common -Seed PLANCOMBAT01
pwsh -NoProfile -File smoke/run-planning-smoke.ps1 @common -Seed PLANCOMBAT02 -SimulationCase unknown
pwsh -NoProfile -File smoke/run-planning-smoke.ps1 @common -Seed PLANCOMBAT03 -SimulationCase event -SimulationEvent DenseVegetation
pwsh -NoProfile -File smoke/run-planning-smoke.ps1 @common -Seed PLANCOMBAT04 -SimulationCase event -SimulationEvent BattlewornDummy -SelfTest
```

定位并修复的失败：初始事件尚未稳定就取样；规划序列化丢失地图；原生序列化省略 false 地图标记却默认还原为 true；`AllEncounters` 遗漏事件模型；父进程的自检环境变量被 worker 继承。worker 保持完整存档恢复校验，没有跳过差异检查。

范围：测试使用修改过的规划牌组、HP 和药水；未逐个端到端运行所有事件、所有首领和所有遗物组合。一次完整 AutoSlay 请求在 120 秒上限内未完成，已终止；不能算作本轮完整跑局通过。最终无头进程正常退出时仍有 Godot 资源释放告警，没有把退出码为 0 解释为资源零泄漏。无头控件检查不证明实际屏幕布局和鼠标操作。模拟选择是未来规划的参照，不能视作真实战斗结果保证。
