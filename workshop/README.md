# 工坊发布素材

已于 2026-09-08 使用官方上传工具上传 Seed Oracle 0.1.22，仅自己可见。2026-09-15 准备沿用同一条目更新至 0.1.23。

工坊页面：<https://steamcommunity.com/sharedfiles/filedetails/?id=3797872099>

`mod_id.txt` 已保存该条目 ID，后续更新沿用本工作区。`content/` 为本地 Release 发布产物，不纳入 Git。

- `image.png`：符合工坊大小限制的封面；
- `cover-master.png`：封面原图；
- `description.zh-CN.txt`：Steam BBCode 格式的中文工坊介绍；
- `workshop.json`：包含中文介绍和三项工坊依赖的上传配置，可见性为 `private`。

编辑介绍时同步更新 `workshop.json` 的 `description` 字段；上传器只读取 JSON，不会自动读取文本文件。

## 官方工具流程

官方工具：<https://github.com/megacrit/sts2-mod-uploader>

根据 2026-09-08 查阅的官方 README、template/README.md 与 src/UploadCommand.cs：

1. 启动 `ModUploader.exe`，生成 `NewModWorkspace` 并重命名。
2. 将本 mod 的发布文件放入工作区的 `content/`。
3. 将本目录的 `workshop.json` 复制到上传工作区，确认发布时使用的可见性、标签与更新说明。
4. 将本目录的 `image.png` 复制到上传工作区根目录；封面必须小于 1 MB。
5. 发布时运行 `ModUploader.exe upload -w <workspace-folder>`。
6. 后续更新沿用上传生成的 `mod_id.txt`，替换内容并填写 `changeNote` 后再次运行同一命令。

`workshop.json` 中的 `dependencies` 使用工坊数字 ID，与 SeedOracle.json 中的 mod ID / 最低版本声明分别维护。

首次上传已完成。通过登录账号的 Steam UGC 查询确认：可见性为 Private、所有者是当前账号、标题一致、三项依赖齐全、封面存在，封面大小为 881832 字节。

上传后查询到的正文比本地草稿少了一句额外的 Mega Crit / 玩家鸣谢；五项功能、依赖鸣谢与开源信息仍保留。本地介绍暂保留原稿，后续更新前可与页面内容核对。

参考：[官方使用说明](https://github.com/megacrit/sts2-mod-uploader/blob/main/README.md)、[工作区配置说明](https://github.com/megacrit/sts2-mod-uploader/blob/main/template/README.md)、[上传实现](https://github.com/megacrit/sts2-mod-uploader/blob/main/src/UploadCommand.cs)。
