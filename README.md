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
  goal_type: levels_completed   # levels_completed | bars_collected | all_locations
  num_levels: 50                # 1–200; controls how many bar milestone and level completion checks exist
  levels_required: 50           # [goal: levels_completed] number of level completions needed
  bars_required: 400            # [goal: bars_collected] total bars to collect
  trap_percentage: 22           # 0–50
  water_access: enabled         # enabled | disabled
  grapple_unlock: enabled       # enabled | disabled
  starting_retract_speed: very_slow  # very_slow | moderate | near_default
```

---

## Locations

Total locations = `2 * num_levels + 23` (default `num_levels: 50` → **123 locations**)

| Type | Count | Description |
|------|-------|-------------|
| Cumulative bar milestones | N | "Total Bars - 8 Collected", "Total Bars - 16 Collected", ... (every 8 bars) |
| Level completion checks | N | "Level Complete 1", "Level Complete 2", ... |
| Single-level best-bar milestones | 16 | "Best Single Level - 2 Bars" through "Best Single Level - 32 Bars" (every 2) |
| Score milestones | 6 | 50k / 100k / 250k / 500k / 750k / 1M |
| Connected | 1 | Sent immediately on connecting to the server |

Level completion checks and single-level milestones above 24 bars require **Water Access**.

---

## Items

| Item | Count | Effect |
|------|-------|--------|
| Retract Speed Up | 20 | +1.0 grapple retract base speed (starts at 2, vanilla 15) |
| Retract Bonus Up | 15 | +2.0 retract speed bonus (starts at 3, vanilla 25) |
| Bar Decay Rate Down | 10 | −0.6 flat decay per tick (starts at 26, floor 20) |
| Bar Decay Factor Down | 10 | +0.00025 decay factor (starts at 0.9955, cap 0.998) |
| Impact Penalty Down | 10 | −0.6 score lost on wall impact (starts at 26, floor 20) |
| Bar Threshold Down | 8 | −500 bar height needed for music/lights (starts at 6500, floor 2500) |
| Grapple Payout Speed Up | 10 | +1.5 rope payout speed (starts at 1, cap 16) |
| Water Access | 1 | Unlocks going below the water surface |
| Grapple Unlock | 1 | Enables the grapple entirely (guaranteed sphere 1) |
| Score Bonus (Small) | 10 | +10,000 score |
| Score Bonus (Medium) | 5 | +40,000 score |
| Score Bonus (Large) | 3 | +100,000 score |
| Gravity Spike (Trap) | 5 | Sudden downward impulse |
| Decay Spike (Trap) | 5 | +10 decay rate for 10 s |
| Grapple Disconnect (Trap) | 5 | Force-releases the grapple |
| Level Skip (Trap) | 4 | Immediately advances to the next level |

Physics upgrades intentionally start well below vanilla values — upgrades are meant to feel earned.

---

## License

This mod is released under the MIT License. Floating Point is © Tom Francis / Suspicious Developments.
