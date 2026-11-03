# 0.1.42 地图预演误结算与真实删牌排查

## 真实现场

已保全 `C:\Users\slimoon\AppData\Roaming\SlayTheSpire2\logs\godot.log` 和历史 `1793437394.run`，证据在 `.local/shadow-safety-20261031/`。同时复制了仍存在的 current_run 及其备份，未恢复或改写真实存档。

日志确认运行的是工坊 SeedOracle 0.1.41、游戏 0.111.0。22058 行记录静默猎手战胜双尾鼠，22066–22068 行领取金币、药水并打开选牌；其后大量重复奖励生成，22181 行突然对同一场战斗登记失败并写入真实跑局历史。历史种子为 `ZK136UP041M3`，最后房间仍记载 37/70 HP，`was_abandoned=false`，死亡遭遇为 `TWO_TAILED_RATS_NORMAL`。

现场没有保存触发死亡的调用栈或当时悬停的地图节点。日志吻合预演死亡误触发真实结算，但不足以确定这一次是哪个事件/选项触发，不能声称已重现原用户输入。

## 原版机制与修复

1. 原版 `CreatureCmd.Kill(IReadOnlyCollection<Creature>, bool)` 从传入玩家取得跑局，但失败时调用进程全局 `CombatManager.LoseCombat`、`RunManager.OnEnded`、音乐与 `NRun.ShowGameOverScreen`。影子跑局死亡因此也能登记真实跑局失败、写进度并删除真实当前存档。修复在 Kill 入口按玩家对象身份分流；非战斗影子只执行生命和死亡 hooks，不进入全局结算。保留原版防死顺序、强制死亡及非战斗多人保护。混合真实/影子目标或原生战斗对象停止预演。
2. 瓶中精灵的防死调用 `PotionModel.OnUseWrapper`，原包装还会登记全局战斗效果。影子非战斗使用只执行药水消耗、本体、hooks 与影子历史，不登记真实 CombatManager。
3. 瓦库的领主阳伞 `AfterRoomEntered` 启动未等待的 `PurchaseEverything`，直接修改真实地图/牌组按钮，并通过 `MerchantCardRemovalEntry` 调用 `OneOffSynchronizer.DoLocalMerchantCardRemoval`。该同步器忽略商品所属玩家，固定针对真实 LocalPlayer 发送消息和删牌。修复让影子阳伞返回可等待的模型购买任务，屏蔽这条 UI/网络路径；删牌入口按影子对象身份使用指定目标，保留费用、历史、移除 hook 和购买完成逻辑。
4. 试炼 `DoubleDown` 打开真实放弃跑局弹框。原事件枚举虽然把它标为 RunEnding，仍会执行战斗探测。修复跳过该探测、拒绝重放结束跑局步骤，并在影子 `DoubleDown` 入口阻止 UI。

隔离按注册的玩家对象身份判断，作用域结束后仍有效；真实玩家不会因为 NetId 相同而被当成影子。没有通过临时锁血掩盖致死分支，也不需要调用真实 OnEnded 再伪造返回值来兼容第三方 postfix。

## 预测边界

领主阳伞入店会自动获得商品，并强制免费删牌；商品遗物还可能要求变牌、选牌或奖励选择。没有指定这些选择时，该分支降级并停止后续推演。当前规划界面尚未提供全部阳伞入店选择，因而可能显示暂不支持；不会默认选第一张，也不会打开真实界面补选。

## 本轮验证与本机安装

在独立 APPDATA/LOCALAPPDATA 和复制的资料下运行原版 headless 游戏，启用 RitsuLib、Random Foreseer 0.13.20、Combat Solver 与当前 SeedOracle Debug。修复后的完整 `-SelfTest` 与规划审计进程退出码为 0；最终报告和日志分别保存在 `.local/shadow-safety-20261031/verified-release-smoke-report.txt` 与 `verified-release-smoke.log`。

- `shadow-death-isolation PASS`：直接、间接、作用域结束后的死亡，以及原版满屋芝士低血选项均正确致死影子；蜥蜴尾巴、瓶中精灵恢复量及消耗正确，强制死亡绕过防死，混合真实/影子目标拒绝执行。测试进程中的 live 跑局、全局管理器状态与进度序列化值未变，`OnEnded`、战斗失败与结束界面调用为 0。
- `shadow-merchant-isolation PASS`：阳伞自动获得 7 张商品卡并仅删除指定的影子牌，免费费用、删牌次数、库存与历史正确；缺少选择时明确拒绝。真实删牌同步器、原始阳伞后台任务、地图输入改动与放弃确认框调用均为 0。
- 既有地图布局、Shift 滚动、普通购买和送货员补货、多组选牌/重掷、路线性能与战斗模拟回归通过。使用当前 Random Foreseer 0.13.20 的最终自测事件审计结果为 `ok=110 degraded=11 nondeterministic=0 failed=0`；降级项保留原有能力边界，不能当成已预测结果。
- Debug 和 Release 编译均为 0 警告、0 错误。

已备份并替换本机当前实际使用的工坊目录 `D:\SteamLibrary\steamapps\workshop\content\2868840\3797872099` 中 DLL、PDB 和清单，版本 `0.1.42`，程序集版本 `0.1.42.0`。发布包使用当前 Random Foreseer 0.13.20 构建；逐文件 SHA-256 与 Release 构建输出一致，完整安装回执在 `.local/shadow-safety-20261031/install-receipt.json`。DLL SHA-256：`AEFAA284836289DC30CAEA7CABB00AFD4FC9D3389398AB2A1BACE4646CBF20CC`。原安装备份在该证据目录的 `installed-before-0.1.42/`。

用户原地图输入和完整 Mod 组合的实际交互尚未重放；重启游戏后应重点验证低血浏览远端事件、瓦库房间预览商店，以及真实进店后的正常阳伞删牌。构建、隔离回归和安装哈希不能代替该交互验证。本次 0.1.42 工坊上传已通过 Steam UGC 无缓存查询，Git 推送回执随后写入本目录。
