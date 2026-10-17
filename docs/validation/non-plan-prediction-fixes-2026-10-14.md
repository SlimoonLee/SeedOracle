# 普通模式预测修复与验证 · 2026-10-14

Seed Oracle **0.1.26** 已修复前次审查的 F1–F8，完成 Debug/Release 编译、隔离无头回归及本地部署。真实游戏目录的 DLL、PDB、清单与 Release 输出的 SHA-256 完全一致。原有未提交改动保留在工作区；没有提交或发布工坊。

前次报告是修复前的证据，见[可预测性审查](non-plan-predictability-audit-2026-10-14.md)。

## 修复对应关系

| 项目 | 最终行为 | 依据 |
| --- | --- | --- |
| F1 半价骰 | 使用原版 `MerchantInventory.CreateForNormalMerchant`，包含半价牌第二次价格骰及完整后续 RNG 消费 | [生成入口](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:572>) |
| F2 上架去重与蛋 Hook | 原版上架步骤维护库存 blacklist，原生执行 `ModifyMerchantCardCreationResults`；三种蛋的商店升级状态保留 | 同上；回归覆盖三蛋组合 |
| F3 会员卡折扣 | 仅在读取原生商品价格时恢复影子玩家本地身份；生成、事件与 UI 的隔离仍有效。原版商品发现回调在生成作用域内被阻止 | [隔离作用域与补丁](<D:/FORSTS2/src/Integration/ShadowIsolation.cs:37>)、[价格读取](<D:/FORSTS2/src/Forecasting/PlanningPredictionService.cs:590>) |
| F4 事件楼层筛选 | 每条路线有独立影子 run；逐房更新 ActFloor、历史、坐标与已访问事件。选择目标事件时，历史包含前置房间但尚不包含目标，符合原版时点 | [路线推进](<D:/FORSTS2/src/Forecasting/RouteWorldlinePredictor.cs:27>)、[事件队列](<D:/FORSTS2/src/Forecasting/EventQueueSimulation.cs:17>) |
| F5 奖励条件 | 普通地图战斗奖励使用 BranchDependent，展示为“胜利后的基础掉落”；明确全歼、未新增奖励或改写奖励状态的假设，Boss 压缩视图同样保留条件 | [奖励适配器](<D:/FORSTS2/src/Integration/RandomForeseerRewardAdapter.cs:35>)、[通用提示条件](<D:/FORSTS2/src/UI/MapForecastTooltipBuilder.cs:245>) |
| F6 条件/截断提示 | 单结果、多结果、Boss 和尚无结果的截断视图统一显示条件。截断或移动尚不可用时不保留聚合 Exact；规划视图收窄时也不提升已有条件结果 | [准确度降级](<D:/FORSTS2/src/Forecasting/MapForecastService.cs:174>)、[通用提示](<D:/FORSTS2/src/UI/MapForecastTooltipBuilder.cs:215>) |
| F7 路线分组 | 商店库存、价格、半价、删牌价与准确度参加分组；卡牌包含升级、序列化状态及附魔；事件还包含结果种类、来源槽位与覆盖信息 | [内容键](<D:/FORSTS2/src/Forecasting/ForecastContentKey.cs:6>)、[路线分组](<D:/FORSTS2/src/UI/RouteMarkerPlan.cs:112>) |
| F8 事件结果语义 | 区分选牌候选、重抽序列、卡包候选、战斗胜利条件与部分内容。AromaOfChaos、Symbiote、WhisperingHollow 和 MorphicGrove 的变牌保留原牌槽位到结果的映射；初始提示与未覆盖选项/后续页分别注明 | [事件适配器](<D:/FORSTS2/src/Integration/RandomForeseerEventAdapter.cs:124>)、[事件显示](<D:/FORSTS2/src/UI/MapForecastTooltipBuilder.cs:778>) |

战损悬窗与手动面板补充了“求解器选定打法、终局不代表最优或实战保证”的说明。此修复没有把普通战前 API 转为 blind 策略，也没有把未决定的选择伪装为唯一结果。

另修正构建范围：SeedOracle 项目排除兄弟项目 `seedsearch/**`，避免其 .NET 10/WinForms 源码和生成程序集属性被编入游戏 Mod。

## 本轮验证

专用回归代码为 [PredictionAccuracyRegressionTests.cs](<D:/FORSTS2/src/Validation/PredictionAccuracyRegressionTests.cs:21>)，只编入 Debug，在 `-SelfTest` 时运行；Release 不包含该测试代码。冒烟脚本现在要求专用回归和原有 SelfTest 都实际通过，不能仅凭游戏进程退出码判断成功。

| 检查 | 结果 |
| --- | --- |
| Debug / Release 编译 | 通过，均 0 警告、0 错误 |
| 原版商店差分 | **12 份库存通过**：4 个不同 RNG 初态/遗物配置，每个连续进店 3 次；配置为无额外遗物、三种蛋、会员卡、会员卡+快递员 |
| 商店差分内容 | 所有商品身份与卡牌状态、价格、半价、删牌价相同；生成后完整规划状态令牌也相同，覆盖 RNG 与遗物袋续用 |
| 折扣与隔离 | 独立核对 50% / 40% 价格与删牌价；价格作用域退出后，本地 UI 身份重新关闭 |
| 事件楼层门槛 | PunchOff 的 5→6、SlipperyBridge 的 6→7 两个案例与原版 `PullNextEvent` 一致 |
| 路线分组 | 同进店序号不同价格、同 ID 不同升级、不同附魔状态的结果均分开 |
| 截断显示 | 单结果视图及没有已生成路线内容的视图保留截断说明；准确度不会保留 Exact |
| 事件与奖励语义 | 变牌保留每个来源槽位，包含重复原牌 ID；显示目标映射及事件覆盖限制；基础战斗奖励标为条件结果 |
| 跑局与资料纯度 | 专用回归前后 live fingerprint 及序列化资料记录一致 |
| 原有地图 SelfTest | 通过：8 类节点、8 个浮窗、13 个商店条目，未知房间、HP、宝箱、先古条件及相关 UI 控件检查通过 |
| 奖励/药水/事件续页回归 | 原生奖励组、药水链和 Trial/TinkerTime/BrainLeech 续页检查通过 |
| 事件审计计数 | `ok=106 degraded=15 nondeterministic=0 failed=0`；这是审计计数，不表示完整覆盖 121 个不同事件 |
| 规划战斗模拟 | 3/3 Succeeded；生命/用药参照回写、过期参照拒绝和真实跑局不变检查通过 |

本轮完整通过的执行种子：`ACCURACY_FIX_20261014`。命令：

```powershell
pwsh -NoProfile -File .\smoke\run-planning-smoke.ps1 `
  -Sts2Dir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2' `
  -AccountDataRoot 'C:\Users\slimoon\AppData\Roaming\SlayTheSpire2\steam\76561198444364716' `
  -SelfTest -Seed ACCURACY_FIX_20261014 -TimeoutSeconds 240
```

上述账号目录仅用于读取输入；脚本把资料复制到 `.smoke/planning-audit/Roaming`，在独立无头游戏内运行。

可检查的原始证据：[回归报告](<D:/FORSTS2/.local/prediction-fix-20261014/smoke-report.txt>)、[本轮游戏日志](<D:/FORSTS2/.local/prediction-fix-20261014/godot.log>)、[部署记录](<D:/FORSTS2/.local/prediction-fix-20261014/deployment.json>)。

## 交付与边界

- 本地部署：`D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\SeedOracle`，版本 `0.1.26`。
- Release 包：[SeedOracle-0.1.26.zip](<D:/FORSTS2/artifacts/SeedOracle-0.1.26.zip>)。
- DLL SHA-256：`0AB50EE9FA3DCA298779FF631B5B2C1A203C632C5F1510FA835BD3AA3AE700A7`。
- 原本地文件备份：[deployment-backup](<D:/FORSTS2/.local/prediction-fix-20261014/deployment-backup>)。

原版 DLL 仍为前次核验的 SHA-256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`，本轮使用同一 0.111.0 基线和本机依赖。

需要重启实际游戏才能载入更新；可见窗口中的鼠标操作和长文本布局尚需重启后观察。无头退出仍有 Godot 资源释放告警，本轮没有把退出码 0 解释为资源零泄漏。未决定的事件/奖励/购物/战斗动作仍是条件，未支持的后续页和其他 Mod 内容仍明确保留覆盖边界。
