# osu! Replay Analyzer

A standalone C# tool that simulates osu! replay files (.osr) against beatmaps (.osu) to calculate judgement statistics from replay data.

## Features

- Parse .osr replay files and extract replay frames
- Parse .osu beatmap files and load hit objects
- **Simulate replay frames against hit objects** to calculate judgements
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

The analyzer uses a **headless replay simulation** approach:

1. **Load Beatmap**: Parse the .osu file and extract hit objects (circles, sliders, spinners)
2. **Load Replay**: Parse the .osr file and extract replay frames (cursor positions and key presses)
3. **Simulate Gameplay**: Process each hit object by:
   - Finding relevant replay frames around the hit object's time
   - Checking cursor position against hit object position
   - Detecting key presses within hit windows
   - Applying hit windows to determine judgement (GREAT/OK/MEH/MISS)
   - Tracking slider following for slider ticks and ends
4. **Calculate Statistics**: Aggregate judgements, combo, and accuracy

This approach **replicates the core judgement logic** from `DrawableHitObject` without the UI/graphics components, processing replay frames against hit objects to determine what judgements would have occurred during gameplay.

## Supported Game Modes

Currently only osu! standard (mode 0) is supported. Other game modes (Taiko, Catch, Mania) can be added by including their respective ruleset packages and implementing their judgement logic.

## Technical Details

The project uses:
- `ppy.osu.Game` - Core osu! game framework
- `ppy.osu.Game.Rulesets.Osu` - osu! standard ruleset implementation

Key components:
- **Time-Step Simulation**: Advances time in 1ms increments, mimicking the game's update loop
- **Frame Handler**: Manages replay frame state with interpolation between frames
- **Hit Windows**: Uses `HitWindows.ResultFor()` to determine judgement timing
- **Hit Detection**: Checks cursor distance from hit object position with proper radius calculations
- **Slider Tracking**: Tracks slider ball position along paths with follow circle detection
- **Mod Application**: Properly applies mods to beatmaps (e.g., Hard Rock Y-axis flip, DoubleTime speed changes)
- **Combo Calculation**: Accurately tracks combo through all judgement types

## Current Status & Accuracy

The simulator now accurately replicates osu! gameplay judgements through headless simulation. It correctly handles:

✅ Hit circles with precise timing windows
✅ Slider heads, ticks, and slider ends
✅ Mod application (including coordinate transformations for Hard Rock)
✅ Cursor position interpolation between replay frames
✅ Time-step simulation matching the game's update loop
✅ Accurate combo and accuracy calculations

Known limitations:
- Notelock mechanics not fully implemented
- Only supports osu! standard mode
- No spinner support yet

The simulation produces results that match the osu! client's judgements with high accuracy.
