# Floating Point Archipelago

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin and [Archipelago](https://archipelago.gg) world definition for **[Floating Point](https://store.steampowered.com/app/302380/Floating_Point/)** (Tom Francis, 2014).

Swing through procedurally generated levels collecting red bars — now as part of a multiworld randomizer.

---

## Contents

| Path | Description |
|------|-------------|
| `src/` | C# BepInEx plugin source (net35, Unity 4.3) |
| `apworld/floating_point.apworld` | Archipelago world file — drop into your AP `worlds/` folder or the client's custom worlds directory |

The canonical Python world source lives in the [Archipelago fork](https://github.com/Princesseuh/Archipelago/tree/floating-point) at `worlds/floating_point/`.

---

## Requirements

- **Floating Point** on Steam
- **BepInEx 5** win_x86 installed into the game folder
- **Archipelago** 0.5.x client / server

---

## Installing the plugin

1. Install [BepInEx 5 win_x86](https://github.com/BepInEx/BepInEx/releases) into the Floating Point game folder.
2. Copy `BepInEx/plugins/FloatingPointArchipelago/` from the release ZIP into your game's `BepInEx/plugins/` folder.
3. Launch the game. Press **F1** in-game to open the connection panel.

---

## Building from source

Requires the .NET SDK and the game installed at its default Steam path. NuGet restores `Archipelago.MultiClient.Net` (6.5.0) automatically.

```bash
cd src
dotnet build -c Release
```

The build target copies the DLL into `BepInEx/plugins/FloatingPointArchipelago/` automatically. Use `GAME_PATH` to point it at your install:

```bash
dotnet build -c Release /p:GAME_PATH="/path/to/Floating Point"
```

---

## Options (YAML)

```yaml
game: Floating Point
name: YourName

Floating Point:
  goal_type: levels_completed   # levels_completed | score | bars_collected | all_locations
  num_levels: 10                # 1–30
  levels_required: 3            # [goal: levels_completed]
  goal_score: 10000             # [goal: score]
  bars_required: 96             # [goal: bars_collected]
  trap_percentage: 15           # 0–50
  level_complete_condition: all_bars  # all_bars | press_enter
  water_access: enabled         # enabled | disabled
  level_skip: enabled           # enabled | disabled
  grapple_unlock: enabled       # enabled | disabled
```

Full option documentation is in [`apworld/floating_point/docs/setup_en.md`](https://github.com/Princesseuh/Archipelago/blob/floating-point/worlds/floating_point/docs/setup_en.md).

---

## Items

| Item | Effect |
|------|--------|
| Retract Speed Up | +5 grapple retract speed |
| Retract Bonus Up | +5 retract speed bonus |
| Bar Decay Rate Down | Bars shrink more slowly (flat) |
| Bar Decay Factor Down | Bars shrink more slowly (%) |
| Impact Penalty Down | Less score lost on impact |
| Bar Threshold Down | Music/lights activate sooner |
| Score Bonus (Small/Medium/Large) | +500 / +2000 / +5000 score |
| Extra Level | Counts as 1 completed level |
| Water Access | Unlocks going below the water surface |
| Level Skip | Skip the current level without completing it |
| Grapple Payout Speed Up | +2 rope payout speed (starts 4, max 14) |
| Grapple Unlock | Enables the grapple entirely |
| Gravity Spike (Trap) | Sudden downward impulse |
| Decay Spike (Trap) | +10 decay rate for 10 s |
| Grapple Disconnect (Trap) | Force-releases the grapple |

---

## Locations

Floating Point generates levels **infinitely** — there is no built-in level count. The mod tracks how many times you press Enter and maps each press to a numbered AP level. `num_levels` controls how many of those presses have checks attached; after that the game keeps going but no new checks are generated.

With default settings (`num_levels: 10`): **331 locations**

- 320 bar locations (32 bars × 10 levels)
- 10 level completion locations
- 1 "Connected to Archipelago" location (sent on connect — always sphere 0)

---

## License

This mod is released under the MIT License. Floating Point is © Tom Francis / Suspicious Developments.
