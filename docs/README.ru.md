<div align="center">

# CS2-Bot-Improver

**Более умные и естественные боты для Counter-Strike 2**

[![Последний выпуск](https://img.shields.io/github/v/release/ed0ard/CS2-Bot-Improver?display_name=tag&sort=semver)](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)
[![Всего загрузок](https://img.shields.io/github/downloads/ed0ard/CS2-Bot-Improver/total)](https://github.com/ed0ard/CS2-Bot-Improver/releases)
[![Лицензия: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](../LICENSE)
![Платформы: Windows и Linux](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-5c6bc0)

[English](../README.md) · [简体中文](README.zh-CN.md) · **Русский**

[Возможности](#возможности) · [Установка](#установка) · [Команды](#команды) · [Panel](#руководство-по-panel-только-для-windows) · [FAQ](#часто-задаваемые-вопросы)

</div>

> Плагин для Counter-Strike 2, который улучшает прицеливание и перемещение ботов, использование гранат, индивидуальные особенности, стратегии и многое другое.

Он делает интереснее офлайн-матчи с ботами и частные игры с друзьями, размещённые самим пользователем. Плагин можно установить как на игровой клиент, так и на выделенный сервер.

> ⭐ Ваши звёзды мотивируют автора продолжать обновлять проект.

## Содержание

- [Возможности](#возможности)
- [Установка](#установка)
  - [Windows](#windows)
  - [Linux](#linux)
- [Команды](#команды)
- [Руководство по Panel](#руководство-по-panel-только-для-windows)
- [Часто задаваемые вопросы](#часто-задаваемые-вопросы)
- [Благодарности](#благодарности)
- [Лицензия](#лицензия)

## Возможности

1. Улучшает прицеливание ботов, сохраняя естественное, похожее на человеческое поведение.
2. Позволяет ботам умело использовать гранаты в зависимости от ситуации.
3. Улучшает перемещение ботов.
4. Исправляет большинство случаев, когда боты застревают.
5. Позволяет ботам покупать всё доступное снаряжение и полностью перерабатывает управление их экономикой.
6. Улучшает поведение ботов: они могут зажимать очередь, резко переводить прицел, простреливать дым и отворачиваться от светошумовых гранат.
7. Назначает каждому боту собственные нож, перчатки, скины оружия, наклейки, брелоки, модель агента, музыкальный набор, аватар и профиль.
8. Делает ботов умнее, организованнее и внимательнее к окружению.
9. Заменяет имена ботов именами профессиональных и случайных игроков. Характеристики каждого профессионального игрока основаны на статистике [HLTV](https://www.hltv.org/).
10. Убирает префикс из имён ботов.
11. Настраивает правила игры так, чтобы они лучше подходили для ботов.
12. Добавляет команды, которые делают игру разнообразнее.

## Установка

### Windows

1. Скачайте архив **CS2BotImprover.zip** со страницы [последнего выпуска](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest) и распакуйте его.

   Если выделенный сервер используется не только для матчей с ботами, скачайте **CS2BotImprover_rules_unchanged.zip**.

2. Поместите **Panel v1.4.3.exe** в любое удобное место.

   <img width="128" height="128" alt="Приложение Panel" src="https://github.com/user-attachments/assets/7271dc7d-2436-484b-8359-6531f4abd710" />

3. Откройте корневую папку CS2 и перейдите в каталог `game/csgo`.

   <img width="405" height="256" alt="Переход в каталог game/csgo" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. Скопируйте все остальные файлы из распакованного архива в `game/csgo`.

   <img width="540" height="181" alt="Копирование файлов в Windows" src="https://github.com/user-attachments/assets/6a8645fc-78e7-4f3a-92d3-5d1b6d913918" />

5. Откройте **Panel v1.4.3.exe**, выберите **Bot Mode**, затем нажмите **Launch CS2**.

   <img width="339" height="129" alt="Запуск CS2 в Bot Mode" src="https://github.com/user-attachments/assets/dc806991-c940-43cf-a614-f49012fae4a7" />

### Linux

1. Скачайте архив **CS2BotImprover_for_Linux.zip** со страницы [последнего выпуска](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest) и распакуйте его.
2. Поместите **Commands.txt** в любое удобное место.
3. Откройте корневую папку CS2 и перейдите в каталог `game/csgo`.

   <img width="405" height="256" alt="Переход в каталог game/csgo" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. Скопируйте все остальные файлы из распакованного архива в `game/csgo`.

   <img width="535" height="180" alt="Копирование файлов в Linux" src="https://github.com/user-attachments/assets/9bda7b1d-43d3-49cf-a283-27b124b894e0" />

5. Добавьте `-insecure` в параметры запуска.

   <img width="130" height="153" alt="Открытие параметров запуска" src="https://github.com/user-attachments/assets/4c775e36-3fc3-4a19-9cb1-4f0c9327838c" /><br>
   <img width="625" height="423" alt="Добавление параметра -insecure" src="https://github.com/user-attachments/assets/ac0b0c57-ee67-4e33-96fb-146d14714fc8" />

## Команды

### Прицеливание

| Команда | Описание |
| --- | --- |
| `bot_aim mixed` | Боты гибко выбирают точку прицеливания в зависимости от ситуации. Режим по умолчанию. |
| `bot_aim head` | Боты в первую очередь целятся в голову. |
| `bot_aim body` | Боты в первую очередь целятся в корпус. |
| `bot_aim` | Показывает текущий режим прицеливания. |

### Гранаты

| Команда | Описание |
| --- | --- |
| `bot_nades off` | Боты не используют гранаты. |
| `bot_nades less` | Боты используют ту же логику принятия решений, что и в обычном режиме, но с более низкими ограничениями по количеству. |
| `bot_nades normal` | Ограничения по количеству гранат почти такие же, как у игроков. Режим по умолчанию. |
| `bot_nades more` | Боты используют ту же логику принятия решений, что и в обычном режиме, но с более высокими ограничениями по количеству. |
| `bot_nades max` | Ограничения минимальны, а перед броском гранаты боты раздумывают меньше. |
| `bot_nades` | Показывает текущий режим использования гранат. |

### Скины

| Команда | Описание |
| --- | --- |
| `br_reroll` | При следующем появлении заново выбирает скины для всех ботов. |

### Покупка оружия

Введите имя оружия в игровой консоли, чтобы со следующего раунда выдать это оружие каждому боту.

<details>
<summary><strong>Допустимые имена оружия</strong></summary>

```text
elite
p250
fn57
deagle
cz75a
r8
bizon
p90
mp5sd
mp9
mp7
mac10
ump45
mag7
sawedoff
nova
xm1014
famas
galilar
m4a1
m4a1s
ak47
aug
sg556
ssg08
awp
scar20
g3sg1
negev
m249
```

</details>

`bot_buy` — боты снова будут покупать оружие как обычно.

### Профессиональные команды

Чтобы добавить в матч профессиональные команды, скопируйте нужный набор из [Commands.txt](../Commands.txt) и вставьте его в игровую консоль. В том же формате можно добавлять новые команды.

Например, чтобы добавить Vit за сторону CT, скопируйте команды, показанные ниже.

<img width="301" height="237" alt="Добавление Vit за сторону CT" src="https://github.com/user-attachments/assets/a895f3a6-58f8-47dc-b6f5-b60c1b32fecd" />

### Ножи

Наведите прицел на землю и нажмите клавишу `\`, чтобы создать там все виды ножей.

### «Перелётные снайперы» (Flying Scoutsman)

| Команда | Действие |
| --- | --- |
| `scouts_on` | Включить режим Flying Scoutsman. |
| `scouts_off` | Выключить режим Flying Scoutsman. |

Введите нужную команду после начала матча.

## Руководство по Panel (только для Windows)

### Индикаторы состояния

| Индикатор | Значение |
| --- | --- |
| 🟢 | Проблем не обнаружено. |
| 🟡 | Перезапустите CS2, чтобы применить изменения. |
| 🔴 | Отсутствуют файлы. Нажмите на красный индикатор, чтобы посмотреть их список. |

<img width="481" height="82" alt="Индикаторы состояния" src="https://github.com/user-attachments/assets/26a947e2-4e0e-423f-bce8-f220d88509a2" />

### Онлайн-режим и режим ботов

Выберите нужный режим, затем нажмите `Launch CS2`.

<img width="472" height="179" alt="Переключение режима" src="https://github.com/user-attachments/assets/3f9254fa-4cbe-4854-8fd1-0f35228fff77" />

### Настройки

Нажмите значок <img width="31" height="32" alt="Настройки" src="https://github.com/user-attachments/assets/7f94176b-79f1-4e22-9495-4589c4dea9eb" /> в правом верхнем углу, чтобы открыть `Settings`.

### Команды

Откройте `Commands`: нажмите на блок, чтобы автоматически скопировать его содержимое, или введите ключевые слова для поиска.

<img width="350" height="420" alt="Раздел Commands" src="https://github.com/user-attachments/assets/957cfafb-900d-4450-b985-13d3e8efc375" />

## Часто задаваемые вопросы

### Как играть матчи с ботами вместе с друзьями?

1. Запустите матч с ботами и введите необходимые команды. Затем введите `status` в консоли.

   <img width="597" height="141" alt="Вывод команды status" src="https://github.com/user-attachments/assets/792c4b4f-1d56-4a39-9186-b301cbff1846" />

2. Скопируйте текст после `steamid:` и добавьте перед ним `connect ` — не забудьте пробел.
3. Отправьте полную команду друзьям, чтобы они вставили её в свои консоли.

### Как вручную изменить уровень сложности?

1. Откройте корневую папку CS2 и перейдите в каталог `game/csgo/overrides`.
2. Откройте папку `Low` для лёгкого уровня сложности, `Medium` для смешанной сложности на основе статистики HLTV (по умолчанию) или `High` для экстремальной сложности.
3. Скопируйте `botprofile.vpk` в `game/csgo/overrides` до запуска игры.

### Как вручную переключиться в режим обычного сетевого матча?

1. Откройте корневую папку CS2 и перейдите в каталог `game/csgo/backup/Online`.
2. Скопируйте `gameinfo.gi` в каталог `game/csgo`, заменив файл в папке назначения.
3. Удалите `-insecure` из параметров запуска.

Чтобы после этого **снова играть с ботами**, перейдите в каталог `game/csgo/backup/WithBots`, замените файл описанным выше способом и снова добавьте параметр запуска.

### Как вручную отключить скины оружия и агентов, музыкальные наборы, ножи и перчатки ботов?

1. Откройте корневую папку CS2 и перейдите в каталог `game/csgo/addons/counterstrikesharp/plugins`.
2. Переименуйте папку `BotRandomizer` в `BotRandomizer_disabled`.
3. Откройте `addons/counterstrikesharp/configs/core.json` и задайте параметру `FollowCS2ServerGuidelines` значение `true`.

### Как вручную отключить Steam-профили ботов?

1. Откройте корневую папку CS2 и перейдите в каталог `game/csgo/addons`.
2. Переименуйте папку `BotHider` в `BotHider_disabled`.

### Как правильно запустить плагин на картах из Мастерской?

Добавьте `-disable_workshop_command_filtering` в параметры запуска.

### Как нормально играть на surf-картах?

Выполните `sv_standable_normal 0.7` в игровой консоли.

### Где можно безопасно использовать проект?

> [!WARNING]
> Проект предназначен для офлайн-матчей с ботами, частных игр с друзьями, размещённых самим пользователем, и частных выделенных серверов. Автоматическое назначение косметических предметов применяется только к ботам и не предназначено для изменения инвентаря или предметов реальных игроков; это ограничение соответствует [правилам Valve для CS2 и GSLT](https://help.steampowered.com/en/faqs/view/07AF-502E-A104-BD4B).
>
> Не используйте проект в официальном матчмейкинге Valve, на публичных серверах с защитой VAC, в [FACEIT](https://support.faceit.com/hc/en-us/articles/360015788779-What-is-deemed-to-be-a-cheat), на сторонних публичных серверах сообщества или для обхода античит-систем и средств защиты. Перед подключением к таким сервисам переключите Panel в **Онлайн-режим (Online Mode)** либо восстановите стандартные файлы игры, удалите `-insecure` и перезапустите CS2. Права по лицензии [AGPL-3.0](../LICENSE) не разрешают нарушать правила платформ или серверов. В максимальной степени, разрешённой применимым законодательством, пользователь несёт ответственность за использование вне указанной области и связанные с ним санкции в отношении GSLT, сервера, FACEIT, VAC, игры или учётной записи. Этот текст носит информационный характер и не является юридической консультацией.

## Благодарности

- [metamod-source](https://github.com/alliedmodders/metamod-source)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
- [CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)
- [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)
- [CS2_ExecAfter_No_Admin](https://github.com/ed0ard/CS2_ExecAfter_No_Admin), форк проекта [kus](https://github.com/kus)
- [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [CS2-Lib](https://github.com/ianlucas/cs2-lib) от [Lucas](https://github.com/ianlucas)
- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) от [XBribo](https://github.com/XBribo)
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) от [XBribo](https://github.com/XBribo)
- [CSGOBetterBots](https://github.com/manicogaming/CSGOBetterBots/blob/master/addons/sourcemod/data/bot_info.json) от [manico](https://github.com/manicogaming)
- [CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)
- [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI), форк проекта [Austin](https://github.com/Austinbots)
- [CS2-BotAI-for-Linux](https://github.com/Austinbots/CS2-BotAI)
- [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)
- [RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap) от [YuGeYu](https://github.com/YuGeYu)
- [Apple-Style-GUI](https://github.com/ed0ard/Apple-Style-GUI)

## Лицензия

Проект распространяется по лицензии [GNU Affero General Public License v3.0](../LICENSE).
