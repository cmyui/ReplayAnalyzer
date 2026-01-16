# osu! Replay Analyzer

A standalone C# tool that analyzes osu! replay files (.osr) and calculates detailed judgement statistics from beatmaps (.osu).

## Features

- Parse .osr replay files
- Parse .osu beatmap files
- Calculate accuracy and combo statistics
- Show detailed hit judgement breakdown:
  - GREAT, OK, MEH, MISS counts
  - Slider tick statistics
  - Slider end statistics

## Requirements

- .NET 8.0 SDK or later

## Building

```bash
dotnet restore
dotnet build
```

## Usage

```bash
dotnet run -- <replay.osr> <beatmap.osu>
```

### Example

```bash
dotnet run -- /path/to/replay.osr /path/to/beatmap.osu
```

### Output Format

```
Accuracy: 99.04%
Max Combo: 245/245

GREAT: 173
OK: 3
MEH: 0
MISS: 0

SLIDER TICK: 4/4
SLIDER END: 65/65
```

## How It Works

The analyzer uses the official osu! game libraries to:

1. Load and parse the beatmap file using `LegacyBeatmapDecoder`
2. Load and parse the replay file using `LegacyScoreDecoder`
3. Extract hit statistics from the replay data
4. Display the judgement results in a readable format

## Supported Game Modes

Currently only osu! standard (mode 0) is supported. Other game modes (Taiko, Catch, Mania) can be added by including their respective ruleset packages.

## Technical Details

The project uses:
- `ppy.osu.Game` - Core osu! game framework
- `ppy.osu.Game.Rulesets.Osu` - osu! standard ruleset implementation

Hit results are extracted directly from the replay file, which contains pre-calculated judgements. The tool does not perform full replay simulation.

## Limitations

- Only supports osu! standard mode
- Requires both the replay and beatmap files
- Does not perform full gameplay simulation (uses stored statistics from replay)
