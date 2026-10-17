# 0.1.39 预测误播全牌组动画

原版基线重新核验：v0.111.0，DLL SHA256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`。本机本轮启动日志确认实际加载的是本地 Seed Oracle 0.1.38，工坊0.1.24因同ID旧版本被禁用。

## 根因

`MerchantChoiceBranchService.ExpandRest` 遍历牌组，为每张可升级卡恢复一个独立的规划状态，然后调用 `PlanningPredictionService.UpgradeCard`。旧版该方法直接执行 `CardCmd.Upgrade(card)`，既没有进入对应分支的 `ShadowIsolation`，也没有关闭动画。

外层隔离对象是玩家副本A；逐张恢复的分支属于副本B、C等。旧版 `ShadowIsolationPatch` 只排除最顶层隔离对象。新副本保留真实玩家的NetId，原版 `LocalContext.IsMine` 返回true，`CardCmd.Upgrade` 遂向真实 `NRun.GlobalUi.CardPreviewContainer` 添加 `NCardUpgradeVfx`。该动画最后把牌送回牌组，因每个锻造目标都执行一次，看起来像整副牌被重新加入。

因此触发条件是预测展开了前置休息点的锻造分支，而非真实领取了所有牌。缓存命中不会重做这些分支；重新计算时又会出现，符合间歇发生的表现。

## 修复前复现

在复制资料和隔离游戏进程中，保留旧实现，仅加入观察探针，执行与 `ExpandRest` 相同的嵌套快照／逐张升级路径。观察原生 `NCardUpgradeVfx.Create` 调用数及真实原生UI容器子节点数：

```text
shadow-presentation OBSERVED smith_branches=10 locally_owned_shadows=10 upgrade_vfx=10 preview_nodes=10 native_ui=True
```

期望零动画的回归检查在旧版明确失败。原始报告与日志保存为 `.local/shadow-animation-20261017/before-fix-report.txt`、`before-fix-godot.log`。未操作正在运行的真实游戏，也未更改真实牌组。

## 修复

- `UpgradeCard` 自行进入正确玩家副本的隔离范围，并以 `CardPreviewStyle.None` 执行原生升级。升级与历史记录仍发生在副本上。
- 创建规划副本后，按玩家对象身份登记到 `ConditionalWeakTable`，避免依赖玩家编号或当前最顶层的执行范围。弱引用登记不会永久保留已经不用的跑局副本。
- `LocalContext.IsMe/IsMine` 对已登记副本持续关闭，覆盖其他嵌套副本及范围结束后才触发的卡牌预览。种子总览使用的独立副本也登记。
- 会员卡价格 getter 仍可临时按本地玩家计算价格，但这种临时开放不使副本卡牌获得动画权限。真实玩家对象的判断保持原状。

## 验证范围

新增原生界面回归：全部可升级牌的逐张模拟、实际休息点→商店的分支展开、隔离范围结束后的升级回调、默认原生加牌及预览、嵌套隔离、价格计算范围及真实玩家归属。检查副本升级／加牌确实生效，同时升级动画创建次数、真实卡牌预览节点增量为零；另核对真实跑局指纹不变。

本轮最终报告和部署/包内哈希记录位于 `.local/shadow-animation-20261017/`。真实游戏的长时间交互及第三方Mod全组合不作为本次已验证项。

## 修复后结果

```text
shadow-presentation OBSERVED smith_branches=10 locally_owned_shadows=0 upgrade_vfx=0 preview_nodes=0 native_ui=True
shadow-presentation PASS: smith_targets=10 route_smith_branches=10 after_scope_upgrade=applied raw_card_add=applied previews=0 nested=isolated price=restricted live=unchanged real_ownership=preserved
```

Debug、Release均0警告0错误。完整回归通过：原生商店12例、128种普通选牌、42种原生普通购买组合、7个送货员购牌后掉落对照、26种重掷顺序、3种交错领取、55条羽翼之靴路线和Shift滚轮锁图。512路线悬停194ms、分配89055704字节、缓存0ms、基础库存生成1次。事件106支持、15降级、0不确定、0失败；战斗模拟3例成功，真实跑局及复制资料纯度检查通过。

身份判断位于频繁调用的本地归属查询中，使用无捕获遍历，避免每次查询分配闭包。编译期间工坊Random Foreseer从0.13.18自动更新为0.13.19，旧版本子目录被移除；本地`local.props`已指向实际存在的0.13.19，未改动该依赖本身。
