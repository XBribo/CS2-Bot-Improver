# Pro Opening Replay

`ProOpeningReplay` is a CounterStrikeSharp plugin that makes bots copy professional openings. Each round it indexes the local pro-round manifest by side, player count, spawn starts, and effective economy. It selects one professional round per team, assigns bots to pro players whose loadout value fits each bot budget, applies the target loadout, then replays native `.cs2rec` movement snapshots, active weapon, and grenade projectiles until handoff or the route data ends.

## Build The Dataset

The current workflow reads native `.dem` files directly, including `.dem` members inside HLTV `.rar`/`.zip`/`.7z` archives. Each archive can contain multiple maps; the extractor selects members whose name matches the requested `--map` aliases.

Required tools:

- Python 3.10 or newer
- `demoparser2`
- `7z` for archive listing
- `unrar` for RAR5 extraction

Install the Python dependency if needed:

```bash
python3 -m pip install demoparser2 requests tqdm
```

Build the runtime manifests and `.cs2rec` records used by the plugin:

```bash
CS2-Bot-Improver/tools/pro_opening_replay/export_all_cs2rec.sh
```

The `extract` command scans every `.dem` file and every `.dem` member inside supported archives, reads the demo header's actual map name, and writes each route to that map's manifest and record directory. Keep `--stride 1` for native replay parity; the `.cs2rec` reader consumes one record per simulation tick. New exports use the v3 gzip compact layout and are expanded to native tick buffers at load time. `--jobs 0` uses all available CPU cores. `--economy-sample-seconds` controls how long after freeze end the pro balance/loadout economy snapshot is taken. Use `--map de_inferno` only when intentionally filtering to one map. Use `--strict` only when debugging; by default the extractor skips a corrupt archive or demo and keeps building the rest.

The wrapper script defaults to `DEMOS_DIR=~/code/betterbot/demos`, `JOBS=8`, `MAX_TASKS_PER_CHILD=1`, `RESET=1`, and enables a tqdm progress bar. Override those values from the shell when needed:

```bash
JOBS=4 RESET=0 CS2-Bot-Improver/tools/pro_opening_replay/export_all_cs2rec.sh --map de_inferno
```

## Build The Plugin

The plugin targets .NET 8 and CounterStrikeSharp API `1.0.367`.

```bash
dotnet build CS2-Bot-Improver/addons/counterstrikesharp/plugins/ProOpeningReplay/ProOpeningReplay.csproj -c Release
```

The compiled files are written to:

```text
CS2-Bot-Improver/addons/counterstrikesharp/plugins/ProOpeningReplay/bin/Release/net8.0/
```

## Install On A Linux CS2 Server

Install Metamod and CounterStrikeSharp on the server first. Then create the plugin directory under the CS2 game folder:

```bash
mkdir -p /path/to/cs2/game/csgo/addons/counterstrikesharp/plugins/ProOpeningReplay/data
```

Copy these files:

```bash
cp CS2-Bot-Improver/addons/counterstrikesharp/plugins/ProOpeningReplay/bin/Release/net8.0/ProOpeningReplay.dll \
  /path/to/cs2/game/csgo/addons/counterstrikesharp/plugins/ProOpeningReplay/

cp CS2-Bot-Improver/addons/counterstrikesharp/plugins/ProOpeningReplay/bin/Release/net8.0/ProOpeningReplay.deps.json \
  /path/to/cs2/game/csgo/addons/counterstrikesharp/plugins/ProOpeningReplay/

cp -r CS2-Bot-Improver/addons/counterstrikesharp/plugins/ProOpeningReplay/data/de_dust2_openings_manifest.json \
  CS2-Bot-Improver/addons/counterstrikesharp/plugins/ProOpeningReplay/data/de_dust2_openings_manifest_records \
  /path/to/cs2/game/csgo/addons/counterstrikesharp/plugins/ProOpeningReplay/data/
```

Start or changelevel the server once. If `config.json` does not exist, the plugin creates it automatically in the same plugin directory.

## Configuration

Default `config.json`:

```json
{
  "Enabled": true,
  "DatasetPathTemplate": "data/{map}_openings_manifest.json",
  "MapName": "",
  "DatasetPath": "",
  "ApplyLoadouts": true,
  "PreserveUsefulEquipment": true,
  "TransferSavedUtility": true,
  "ThrowGrenades": true,
  "DriftTeleportEnabled": true,
  "DriftTeleportThreshold": 120,
  "DriftTeleportCooldown": 0.75,
  "StopOnEnemyContact": true,
  "StopOnFlash": false,
  "StopOnAudibleEnemyNoise": false,
  "SpawnMatchTolerance": 24,
  "HumanSpawnBlockRadius": 72,
  "MatchSelectionDelay": 3.2,
  "LoadoutApplyDelay": 0.25,
  "HandoffDistance": 1800,
  "HandoffFovDegrees": 90,
  "FootstepHandoffDistance": 1150,
  "MaxUtilityBeyondThrown": 0,
  "EnforcePistolRoundMatching": true,
  "SuppressBotEngagementWhileReplaying": true,
  "SuppressReplayAttackInput": true,
  "KeepBotPerceptionDuringReplay": true,
  "UseNativeBotControllerReplay": true
}
```

Important options:

- `PreserveUsefulEquipment`: keeps useful saved rifles and only buys missing target items, so bots do not waste money replacing an equivalent long gun.
- `DatasetPathTemplate`: per-map manifest path. `{map}` is replaced with the current CS2 map name, e.g. `data/de_inferno_openings_manifest.json`.
- `TransferSavedUtility`: before buying missing grenades, moves saved surplus flashes, smokes, HE grenades, molotovs, incendiaries, and decoys from teammates whose selected replay does not need them to teammates whose selected replay does.
- `StopOnFlash`: stops replay control for a bot when it is flashed, so normal blind behavior and aim penalties can apply.
- `StopOnAudibleEnemyNoise`: stops replay control when a nearby enemy footstep/sound is heard and turns the bot toward the sound before normal bot AI resumes.
- `SuppressReplayAttackInput`: removes primary/secondary attack buttons only at runtime before native playback. Exported `.cs2rec` files keep the original attack inputs; grenades still use the manifest's projectile replay path.
- `HumanSpawnBlockRadius`: excludes a pro start position when a human player is already occupying that space.
- `MatchSelectionDelay`: seconds after round start before matching routes and budgets, normally after BotBuy has finished its buy/drop timers.
- `LoadoutApplyDelay`: seconds after matching before the target replay loadout is applied.
- `HandoffDistance` and `HandoffFovDegrees`: stop replay when a live enemy is close enough and in front of the bot.
- `FootstepHandoffDistance`: fallback hearing radius used for `player_footstep` events when the game event does not provide a radius.

After editing config or replacing the dataset, run:

```text
css_proreplay_reload
```

Check runtime status with:

```text
css_proreplay_status
```

## Runtime Behavior

At round start, the plugin waits for the configured match delay, then groups usable bots by T and CT. Each side must match one professional round as a team: every selected pro player loadout must fit the corresponding bot budget, and occupied human starts are excluded. If the live side is on a pistol round, only pistol replay rounds are eligible.

During freeze, matched bots are moved to the selected pro start positions and receive the selected pro loadouts. At freeze end, each bot follows the extracted `.cs2rec` route through the native BotController replay path, with active weapon and due grenade throws replayed alongside movement. Spawned grenade projectiles consume the corresponding grenade from the bot inventory.

Loadouts are copied from the selected pro player. The bot money is set to its folded budget minus the target loadout value, where the budget includes current money plus useful carried equipment and nearby dropped weapons assigned to the closest owner.

Replay control stops for a bot when the opening finishes, the bot or attacker is hurt, an enemy enters the configured handoff cone, the bot is flashed, or a nearby enemy footstep/sound is heard. Normal CS2 bot behavior then takes over for the rest of the round.
