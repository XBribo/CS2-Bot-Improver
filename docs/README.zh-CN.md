<div align="center">

# CS2-Bot-Improver

**让《Counter-Strike 2》的机器人更聪明、更接近真人**

[![最新版本](https://img.shields.io/github/v/release/ed0ard/CS2-Bot-Improver?display_name=tag&sort=semver)](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)
[![累计下载](https://img.shields.io/github/downloads/ed0ard/CS2-Bot-Improver/total)](https://github.com/ed0ard/CS2-Bot-Improver/releases)
[![许可证：AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](../LICENSE)
![平台：Windows 与 Linux](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-5c6bc0)

[English](../README.md) · **简体中文** · [Русский](README.ru.md)

[功能](#功能) · [安装](#安装) · [命令](#命令) · [Panel 指南](#panel-使用指南仅限-windows) · [常见问题](#常见问题)

</div>

> 一款面向《Counter-Strike 2》的机器人增强插件，改进机器人的瞄准、移动、投掷物使用、个性、战术等表现。

本项目旨在改善离线机器人对局及与好友自行托管的私人对局体验，同时支持安装在游戏客户端和专用服务器上。

> ⭐ 你的 Star 是作者持续更新的动力。

## 功能

1. 让机器人的瞄准更精准，也更接近真人表现。
2. 让机器人能够根据战况灵活使用投掷物。
3. 改进机器人的移动表现。
4. 修复大多数机器人卡住的问题。
5. 允许机器人购买所有物品，并全面改进其经济管理。
6. 优化机器人行为，使其能够扫射、甩枪、混烟和背闪。
7. 为每个机器人分配独立的刀具、手套、武器皮肤、印花、挂件、探员模型、音乐盒、头像和个人资料。
8. 让机器人更聪明、更有组织性，也更警觉周围环境。
9. 将机器人名称替换为职业选手或随机玩家名称；每位职业选手的特征均基于 [HLTV](https://www.hltv.org/) 数据。
10. 移除机器人名称前缀。
11. 调整游戏规则，使其对机器人更友好。
12. 添加一些命令，让游戏更有趣。

## 安装

### Windows

1. 在[最新版本](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)中下载 **CS2BotImprover.zip** 并解压。

   > 如果你运行的专用服务器并非只用于机器人对局，请下载 **CS2BotImprover_rules_unchanged.zip**。

2. 将 **Panel v1.4.3.exe** 放在方便使用的任意位置。

   <img width="128" height="128" alt="Panel 应用" src="https://github.com/user-attachments/assets/7271dc7d-2436-484b-8359-6531f4abd710" />

3. 打开 CS2 根目录，然后进入 `game/csgo`。

   <img width="405" height="256" alt="进入 CS2 的 game/csgo 目录" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. 复制已解压安装包中的其余全部文件，并粘贴到 `game/csgo`。

   <img width="540" height="181" alt="将 Windows 版插件文件复制到 game/csgo" src="https://github.com/user-attachments/assets/6a8645fc-78e7-4f3a-92d3-5d1b6d913918" />

5. 打开 **Panel v1.4.3.exe**，选择 **Bot Mode**，然后点击 **Launch CS2**。

   <img width="339" height="129" alt="在 Panel 中选择 Bot Mode 并启动 CS2" src="https://github.com/user-attachments/assets/dc806991-c940-43cf-a614-f49012fae4a7" />

### Linux

1. 在[最新版本](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)中下载 **CS2BotImprover_for_Linux.zip** 并解压。
2. 将 **Commands.txt** 放在方便使用的任意位置。
3. 打开 CS2 根目录，然后进入 `game/csgo`。

   <img width="405" height="256" alt="进入 CS2 的 game/csgo 目录" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. 复制已解压安装包中的其余全部文件，并粘贴到 `game/csgo`。

   <img width="535" height="180" alt="将 Linux 版插件文件复制到 game/csgo" src="https://github.com/user-attachments/assets/9bda7b1d-43d3-49cf-a283-27b124b894e0" />

5. 在启动选项中添加 `-insecure`。

   <img width="130" height="153" alt="打开 CS2 启动选项" src="https://github.com/user-attachments/assets/4c775e36-3fc3-4a19-9cb1-4f0c9327838c" /><br>
   <img width="625" height="423" alt="在启动选项中添加 insecure 参数" src="https://github.com/user-attachments/assets/ac0b0c57-ee67-4e33-96fb-146d14714fc8" />

## 命令

### 瞄准

| 命令 | 说明 |
| --- | --- |
| `bot_aim mixed` | 机器人根据战况灵活选择瞄准位置（默认） |
| `bot_aim head` | 机器人优先瞄准头部 |
| `bot_aim body` | 机器人优先瞄准躯干 |
| `bot_aim` | 查看当前瞄准模式 |

### 投掷物

| 命令 | 说明 |
| --- | --- |
| `bot_nades off` | 机器人不会使用任何投掷物 |
| `bot_nades less` | 使用与普通模式相同的决策逻辑，但数量限制更低 |
| `bot_nades normal` | 数量限制与真人玩家基本相同（默认） |
| `bot_nades more` | 使用与普通模式相同的决策逻辑，但数量限制更高 |
| `bot_nades max` | 限制最少，机器人在投掷前的思考也更少 |
| `bot_nades` | 查看当前投掷物使用模式 |

### 皮肤

| 命令 | 说明 |
| --- | --- |
| `br_reroll` | 在所有机器人下次生成时重新随机分配其皮肤 |

### 购买

在控制台中输入武器名称，即可从下一回合开始让所有机器人获得该武器。

可用的武器名称：

```text
elite    p250     fn57      deagle    cz75a    r8
bizon    p90      mp5sd     mp9       mp7      mac10
ump45    mag7     sawedoff  nova      xm1014   famas
galilar  m4a1     m4a1s     ak47      aug      sg556
ssg08    awp      scar20    g3sg1     negev    m249
```

输入 `bot_buy` 后，机器人将恢复正常购买。

### 战队

如需在对局中加入职业战队，请从 [`Commands.txt`](../Commands.txt) 复制对应命令并粘贴到游戏控制台。你也可以按照相同格式添加新战队。

例如，如果要将 Vit 加入 CT 阵营，请复制下图中的命令：

<img width="301" height="237" alt="将 Vit 加入 CT 阵营的示例命令" src="https://github.com/user-attachments/assets/a895f3a6-58f8-47dc-b6f5-b60c1b32fecd" />

### 刀具

将准星对准地面并按下键盘上的 `\`，即可在该位置生成各种刀具。

### Flying Scoutsman

| 命令 | 说明 |
| --- | --- |
| `scouts_on` | 开启 Flying Scoutsman |
| `scouts_off` | 关闭 Flying Scoutsman |

请在对局开始后输入以上命令。

## Panel 使用指南（仅限 Windows）

### 状态指示灯

| 状态 | 含义 |
| --- | --- |
| 🟢 | 未检测到问题 |
| 🟡 | 重启 CS2 以应用更改 |
| 🔴 | 文件缺失；点击红灯可查看缺失文件列表 |

<img width="481" height="82" alt="Panel 状态指示灯" src="https://github.com/user-attachments/assets/26a947e2-4e0e-423f-bce8-f220d88509a2" />

### 联机模式与机器人模式切换

选择所需模式，然后点击 `Launch CS2`。

<img width="472" height="179" alt="切换 Online Mode 与 Bot Mode" src="https://github.com/user-attachments/assets/3f9254fa-4cbe-4854-8fd1-0f35228fff77" />

### 设置

点击右上角的 <img width="31" height="32" alt="设置图标" src="https://github.com/user-attachments/assets/7f94176b-79f1-4e22-9495-4589c4dea9eb" /> 图标以打开 `Settings`。

### 命令

点击 `Commands` 后，点击任一命令块即可自动复制，也可以输入关键词搜索。

<img width="350" height="420" alt="Panel 命令搜索与复制界面" src="https://github.com/user-attachments/assets/957cfafb-900d-4450-b985-13d3e8efc375" />

## 常见问题

<details>
<summary><strong>如何与好友一起游玩机器人对局？</strong></summary>

1. 创建机器人对局并输入所需命令，然后在控制台中输入 `status`。

   <img width="597" height="141" alt="在控制台中运行 status" src="https://github.com/user-attachments/assets/792c4b4f-1d56-4a39-9186-b301cbff1846" />

2. 复制 `steamid:` 后面的文本，并在前面加上 `connect `（不要漏掉中间的空格）。
3. 将完整命令发送给好友，让他们粘贴到各自的控制台中。

</details>

<details>
<summary><strong>如何手动更改难度？</strong></summary>

1. 打开 CS2 根目录，然后进入 `game/csgo/overrides`。
2. 根据需要打开对应文件夹：`Low` 为简单难度；`Medium` 为基于 HLTV 数据的混合难度（默认）；`High` 为极高难度。
3. 启动游戏前，复制其中的 `botprofile.vpk` 并粘贴到 `game/csgo/overrides`。

</details>

<details>
<summary><strong>如何手动切换回普通在线匹配模式？</strong></summary>

1. 打开 CS2 根目录，然后进入 `game/csgo/backup/Online`。
2. 复制 `gameinfo.gi` 并粘贴到 `game/csgo`，替换目标位置的文件。
3. 从启动选项中删除 `-insecure`。

修改后，如果想要**再次游玩机器人对局**，请进入 `game/csgo/backup/WithBots`，按照上述方式替换文件，并重新添加该启动选项。

</details>

<details>
<summary><strong>如何手动禁用机器人的武器皮肤、探员皮肤、音乐盒、刀具和手套？</strong></summary>

1. 打开 CS2 根目录，然后进入 `game/csgo/addons/counterstrikesharp/plugins`。
2. 将 `BotRandomizer` 文件夹重命名为 `BotRandomizer_disabled`。
3. 打开 `addons/counterstrikesharp/configs/core.json`，将 `FollowCS2ServerGuidelines` 设置为 `true`。

</details>

<details>
<summary><strong>如何手动禁用机器人的 Steam 个人资料？</strong></summary>

1. 打开 CS2 根目录，然后进入 `game/csgo/addons`。
2. 将 `BotHider` 文件夹重命名为 `BotHider_disabled`。

</details>

<details>
<summary><strong>如何让插件在创意工坊地图上正常运行？</strong></summary>

在启动选项中添加 `-disable_workshop_command_filtering`。

</details>

<details>
<summary><strong>如何正常进行滑翔（Surf）？</strong></summary>

在游戏控制台中运行 `sv_standable_normal 0.7`。

</details>

### 可以在哪些场景中安全使用本项目？

> [!WARNING]
> 本项目适用于离线机器人对局、由用户自行托管的好友私人对局及私人专用服务器。自动饰品分配路径仅以机器人为处理对象，不以真人玩家的库存或饰品为处理目标；这一边界旨在遵循 [Valve 的 CS2 GSLT 规则](https://help.steampowered.com/zh-cn/faqs/view/07AF-502E-A104-BD4B)。
>
> 请勿将本项目用于 Valve 官方匹配、启用 VAC 的公共服务器、[FACEIT](https://support.faceit.com/hc/en-us/articles/360015788779-What-is-deemed-to-be-a-cheat)、第三方公共社区服务器，或用于规避任何反作弊或安全控制。进入上述服务前，请在 Panel 中切换回**联机模式（Online Mode）**，或手动恢复正常游戏文件、移除 `-insecure`，并重启 CS2。[AGPL-3.0 许可证](../LICENSE)授予的权利不构成违反平台或服务器规则的授权。在适用法律允许的最大范围内，用户应自行承担超出上述范围使用所产生的责任，以及由此导致的 GSLT、服务器、FACEIT、VAC、游戏或账号处罚。本声明仅供说明，不构成法律意见。

## 致谢

- [metamod-source](https://github.com/alliedmodders/metamod-source)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
- [CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)
- [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)
- [CS2_ExecAfter_No_Admin](https://github.com/ed0ard/CS2_ExecAfter_No_Admin)，fork 自 [kus](https://github.com/kus)
- [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [CS2-Lib](https://github.com/ianlucas/cs2-lib)，作者 [Lucas](https://github.com/ianlucas)
- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider)，作者 [XBribo](https://github.com/XBribo)
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)，作者 [XBribo](https://github.com/XBribo)
- [CSGOBetterBots](https://github.com/manicogaming/CSGOBetterBots/blob/master/addons/sourcemod/data/bot_info.json)，作者 [manico](https://github.com/manicogaming)
- [CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)
- [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI)，fork 自 [Austin](https://github.com/Austinbots)
- [CS2-BotAI-for-Linux](https://github.com/Austinbots/CS2-BotAI)
- [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)
- [RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap)，作者 [YuGeYu](https://github.com/YuGeYu)
- [Apple-Style-GUI](https://github.com/ed0ard/Apple-Style-GUI)

## 许可证

本项目采用 [AGPL-3.0](../LICENSE) 许可证。
