# Playnite 本地成就自动解锁

简体中文 · [English](README.md)

本插件监听非 Steam 游戏写入的**本地成就事件或状态文件**，弹出成就通知，并可将解锁时间同步到已有的 [SuccessStory](https://github.com/Lacro59/playnite-successstory-plugin) 手动成就列表。当前版本：**v1.1**。

它不会解锁 Steam 账号成就、绕过 DRM，也不会凭空认定成就已完成。只有定义文件不等于已解锁；游戏和本地成就来源必须实际写入解锁记录。针对 RPG Maker MV 的可选兼容方案会在**明确征得同意后**备份并修改游戏脚本，详情见下文。

## 运行条件与依赖

- **必需：** Windows 和 [Playnite](https://github.com/JosefNemec/Playnite) 桌面版。此版本基于本机 Playnite 10.62 的 SDK 构建和测试；安装包格式为 `.pext`。
- **显示成就列表、图标并记录解锁时间：** 安装 [SuccessStory](https://github.com/Lacro59/playnite-successstory-plugin)。实时同步已在 **3.7.1** 版测试。请先在 SuccessStory 中手动添加或导入对应游戏的 Steam 成就列表；本插件不会替你导入或覆盖该列表。没有 SuccessStory 时，本地监听与弹窗仍可能工作，但无法同步到它的列表。
- **自动识别 GSE 或 RUNE/CODEX：** 游戏目录中需已有相应的本地成就来源或配置。本项目**不附带** GSE、CODEX、RUNE、游戏文件或模拟器 DLL；[GSE Fork](https://github.com/Detanup01/gbe_fork) 和 Achievement Watcher Next 也不是安装依赖。

## 安装与使用

1. 从 [Releases](https://github.com/Canary-235/PlayniteLocalAchievements/releases/tag/v1.1) 下载 `LocalAchievements_1.1.pext`，用 Playnite 打开，并按提示重启。
2. 将游戏加入 Playnite，设置有效的**安装目录**和**游玩动作**。实时监听要求通过 Playnite 启动游戏。
3. 在 SuccessStory 中手动添加或导入该游戏的 Steam 成就列表。列表中的 `ApiName` 必须与游戏本地成就来源写入的 ID 相同，才能同步记录。
4. 右键游戏 → **本地成就自动解锁** → **自动识别模拟器并配置规则**。阅读识别结果与兼容性预检；若能安全定位缺失的 GSE 定义或 RUNE/CODEX 接口，插件会尝试自动补全并报告结果。
5. **通过 Playnite** 启动游戏，在游戏中达成一个*新的*成就条件。本地文件出现解锁记录后，插件会弹窗；SuccessStory 存在匹配的 `ApiName` 时，还会记录解锁时间并刷新界面计数。

默认界面语言是中文。在**附加组件 → 通用 → 本地成就自动解锁**中可选择中文或英文；保存后会询问是否立即重启。重启后生效。游戏本身提供的成就名称、描述不由插件翻译。

右键菜单还提供 **文件补全 → GSE 成就定义 / RUNE/CODEX 成就接口**，用于手动复查；**查看成就来源健康状态**用于排查；**测试成就弹窗**只测试显示，不会解锁；**立即补扫并同步本地成就**只补同步本地状态中已经写入的解锁。

### 文件补全的边界

- **GSE / Goldberg：** 仅在 Steam API DLL、App ID 和目标目录能够唯一确认时，从 Steam 的公开成就定义接口创建*缺失的* `steam_settings/achievements.json`；不会覆盖已有文件，也不会伪造解锁。
- **RUNE / CODEX：** 仅在 App ID 匹配、相邻原版 Steam API 文件能给出唯一接口版本时，备份 `steam_emu.ini` 并补写缺失的 `SteamUserStats`。不会为它生成 GSE 的 `achievements.json`，也不会伪造 `achievements.ini` 或 `Achieved=1`。
- 遇到多个候选目录、App ID 不一致、接口版本不明或游戏根本没有本地成就上报时，插件不会猜测。过去已完成但从未写入本地状态的成就，也无法自动回放。

识别到 RPG Maker MV / Greenworks 成就脚本时，插件会先询问是否允许备份并加入小型事件日志代码；游戏原有的成就调用仍保留。日志在游戏存档目录记录 ID 和时间，不会推断旧存档。移除规则时，仅在脚本没有再次变化的情况下恢复备份。

## 成就没有弹出时

打开**查看成就来源健康状态**，检查监听路径、文件写入时间、识别到的解锁 ID，以及与 SuccessStory `ApiName` 的匹配情况。配置／定义文件只能证明元数据存在，不能证明游戏真的调用了成就接口。请在游戏里达成新的明确条件，观察本地状态是否写入 GSE 的 `earned=true` 或 RUNE/CODEX 的 `Achieved=1`；如果没有，插件没有可信的解锁事件可弹。独占全屏也可能遮住桌面弹窗；无边框窗口模式通常更可靠。

## 自定义规则

不支持的本地来源可通过游戏右键菜单**导入自动成就规则文件…**选择 JSON。`log` 监听追加日志，`snapshot` 检查整个文本文件；捕获的 `achievementId` 必须对应 SuccessStory 的 `ApiName`。

```json
{
  "schemaVersion": 1,
  "name": "示例游戏",
  "sources": [
    {
      "type": "log",
      "path": "%USERPROFILE%\\AppData\\LocalLow\\Example\\Player.log",
      "encoding": "utf-8",
      "match": "Achievement unlocked: (?<achievementId>[A-Za-z0-9_]+)",
      "achievementGroup": "achievementId"
    }
  ]
}
```

可选的 `unlockedAt` 捕获组支持 ISO 时间，用于保留事件原时间。[rules](rules) 目录包含针对特定游戏的示例，**不能保证用于所有游戏**。

## 从源码构建

在 Windows PowerShell 中运行，传入包含 `Playnite.SDK.dll` 的目录：

```powershell
.\build.ps1 -PlaynitePath 'C:\Path\To\Playnite'
.\test-plugin.ps1 -PlaynitePath 'C:\Path\To\Playnite'
```

构建会在源码目录的上一级生成 `LocalAchievements_1.1.pext`。测试会联网读取 Steam 公开成就定义；如果已安装 SuccessStory，也会检查其接口。对于缺少安装目录和文件型游玩动作的旧游戏，可选用以分号分隔的 `PLAYNITE_LOCAL_ACHIEVEMENTS_GAME_ROOTS` 环境变量指定搜索根目录；更推荐直接在 Playnite 中设置安装目录。

## 致谢与来源说明

- [Playnite](https://github.com/JosefNemec/Playnite)（Josef Nemec）提供宿主程序与 SDK。
- [SuccessStory](https://github.com/Lacro59/playnite-successstory-plugin)（Lacro59，及 eFMann 等贡献者）提供本插件可选同步的成就列表与界面。它是独立项目，未打包进本插件。
- [Playnite Achievements — Santodan 分支](https://github.com/Santodan/PlayniteAchievements)（基于 [Justin Delano 的项目](https://github.com/justin-delano/PlayniteAchievements)）、uWaazy 的 [Local Achievements](https://github.com/uWaazy/Local-Achievements)、[Achievement Watcher Next](https://github.com/Shirowwww/Achievement-Watcher-Next) 为本地来源识别和交互设计的调研提供了启发。
- [GSE Fork](https://github.com/Detanup01/gbe_fork) 及其配置生成器文档帮助理解 `achievements.json` 格式与兼容性检查。本插件自行实现了有限的定义补全，**未捆绑或运行**其生成器。

以上是**思路与互操作参考**的致谢，并非声明复制了上述项目的源码或素材。安装包不含这些项目的二进制文件、图片、音效或游戏文件；项目名称和商标归各自所有。本项目与 Playnite、SuccessStory、Valve 及上述项目没有隶属关系。

## 许可证

[MIT](LICENSE)。Copyright © 2026 Canary-235。
