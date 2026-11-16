# Blitz Shop Oracle / 极速商店先知

这是一个独立的极速模式商店预测 mod。它从当前跑局的原生存档状态建立只读影子副本，计算整组商店的购买组合、购买顺序、随机升级分支，以及拿牌、删牌、变牌和蛋类遗物带来的后续变化。

本项目不引用 Seed Oracle、Combat Solver、Random Foreseer 或 RitsuLib。启用时请关闭完整的 Seed Oracle，避免两个 mod 同时挂载同一组商店补丁。

构建

```powershell
dotnet build .\BlitzShopOracle.csproj -c Release
```

构建完成后会复制到游戏的 `mods/BlitzShopOracle` 目录。
