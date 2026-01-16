using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Framework.Utils;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Beatmaps.Legacy;
using osu.Game.IO;
using osu.Game.IO.Legacy;
using osu.Game.Replays;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Skinning;
using osuTK;

namespace osu.ReplayAnalyzer
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: osu-replay-analyzer <replay.osr> <beatmap.osu>");
                return;
            }

            string replayPath = args[0];
            string beatmapPath = args[1];

            if (!File.Exists(replayPath))
            {
                Console.WriteLine($"Error: Replay file not found: {replayPath}");
                return;
            }

            if (!File.Exists(beatmapPath))
            {
                Console.WriteLine($"Error: Beatmap file not found: {beatmapPath}");
                return;
            }

            try
            {
                var analyzer = new ReplayAnalyzer(beatmapPath);
                var result = analyzer.AnalyzeReplay(replayPath);

                Console.WriteLine($"Accuracy: {result.Accuracy:P2}");
                Console.WriteLine($"Max Combo: {result.MaxCombo}/{result.MaxPossibleCombo}");
                Console.WriteLine();

                Console.WriteLine($"GREAT: {result.Statistics.GetValueOrDefault(HitResult.Great, 0)}");
                Console.WriteLine($"OK: {result.Statistics.GetValueOrDefault(HitResult.Ok, 0)}");
                Console.WriteLine($"MEH: {result.Statistics.GetValueOrDefault(HitResult.Meh, 0)}");
                Console.WriteLine($"MISS: {result.Statistics.GetValueOrDefault(HitResult.Miss, 0)}");
                Console.WriteLine();

                var largeTickHit = result.Statistics.GetValueOrDefault(HitResult.LargeTickHit, 0);
                var largeTickMax = result.MaximumStatistics.GetValueOrDefault(HitResult.LargeTickHit, 0);

                var sliderTailHit = result.Statistics.GetValueOrDefault(HitResult.SliderTailHit, 0);
                var sliderTailMax = result.MaximumStatistics.GetValueOrDefault(HitResult.SliderTailHit, 0);

                if (largeTickMax > 0)
                    Console.WriteLine($"SLIDER TICK: {largeTickHit}/{largeTickMax}");

                if (sliderTailMax > 0)
                    Console.WriteLine($"SLIDER END: {sliderTailHit}/{sliderTailMax}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }

    class ReplaySimulationResult
    {
        public Dictionary<HitResult, int> Statistics { get; set; } = new();
        public Dictionary<HitResult, int> MaximumStatistics { get; set; } = new();
        public int MaxCombo { get; set; }
        public int MaxPossibleCombo { get; set; }
        public double Accuracy { get; set; }
    }

    class ReplayAnalyzer
    {
        private readonly WorkingBeatmap workingBeatmap;
        private readonly OsuRuleset ruleset;
        private IBeatmap? playableBeatmap;

        public ReplayAnalyzer(string beatmapPath)
        {
            workingBeatmap = LoadBeatmap(beatmapPath);
            ruleset = new OsuRuleset();
        }

        public ReplaySimulationResult AnalyzeReplay(string replayPath)
        {
            var (replay, mods) = LoadReplay(replayPath);

            // Apply mods to beatmap
            playableBeatmap = workingBeatmap.GetPlayableBeatmap(ruleset.RulesetInfo, mods.ToArray());

            return SimulateReplay(replay);
        }

        private (Replay replay, List<Mod> mods) LoadReplay(string replayPath)
        {
            using var stream = File.OpenRead(replayPath);
            using var sr = new SerializationReader(stream);

            // Read basic replay header
            var rulesetId = sr.ReadByte();
            if (rulesetId != 0)
                throw new NotSupportedException("Only osu! standard replays are supported");

            var version = sr.ReadInt32();
            var beatmapHash = sr.ReadString();
            var username = sr.ReadString();
            var replayHash = sr.ReadString();

            // Skip hit counts
            sr.ReadUInt16(); // 300
            sr.ReadUInt16(); // 100
            sr.ReadUInt16(); // 50
            sr.ReadUInt16(); // Geki
            sr.ReadUInt16(); // Katu
            sr.ReadUInt16(); // Miss

            sr.ReadInt32(); // Score
            sr.ReadUInt16(); // Max combo
            sr.ReadBoolean(); // Perfect
            var modsInt = sr.ReadInt32(); // Mods
            sr.ReadString(); // HP graph
            sr.ReadDateTime(); // Date

            // Convert mod int to Mod objects
            var mods = ruleset.ConvertFromLegacyMods((LegacyMods)modsInt).ToList();

            // Read replay data
            byte[] compressedReplay = sr.ReadByteArray();

            var replay = new Replay();
            if (compressedReplay?.Length > 0)
            {
                using var replayInStream = new MemoryStream(compressedReplay);
                byte[] properties = new byte[5];
                replayInStream.Read(properties, 0, 5);

                long outSize = 0;
                for (int i = 0; i < 8; i++)
                {
                    int v = replayInStream.ReadByte();
                    outSize |= (long)(byte)v << (8 * i);
                }

                long compressedSize = replayInStream.Length - replayInStream.Position;

                using (var lzma = new SharpCompress.Compressors.LZMA.LzmaStream(properties, replayInStream, compressedSize, outSize))
                using (var reader = new StreamReader(lzma))
                {
                    string[] frames = reader.ReadToEnd().Split(',');

                    double currentTime = 0;
                    foreach (var frameData in frames)
                    {
                        string[] split = frameData.Split('|');
                        if (split.Length < 4) continue;
                        if (split[0] == "-12345") continue;

                        int timeDelta = int.Parse(split[0]);
                        float x = float.Parse(split[1]);
                        float y = float.Parse(split[2]);
                        int buttons = int.Parse(split[3]);

                        currentTime += timeDelta;

                        var actions = new List<OsuAction>();
                        if ((buttons & 1) > 0 || (buttons & 4) > 0) actions.Add(OsuAction.LeftButton);
                        if ((buttons & 2) > 0 || (buttons & 8) > 0) actions.Add(OsuAction.RightButton);

                        replay.Frames.Add(new OsuReplayFrame(currentTime, new Vector2(x, y), actions.ToArray()));
                    }
                }
            }

            return (replay, mods);
        }

        private ReplaySimulationResult SimulateReplay(Replay replay)
        {
            var result = new ReplaySimulationResult();
            var scoreProcessor = ruleset.CreateScoreProcessor();
            scoreProcessor.ApplyBeatmap(playableBeatmap);

            // Initialize maximum statistics
            foreach (var hitObject in EnumerateHitObjects(playableBeatmap))
            {
                var judgement = hitObject.CreateJudgement();
                var maxResult = judgement.MaxResult;

                if (maxResult.AffectsCombo())
                    result.MaxPossibleCombo++;

                if (maxResult.AffectsAccuracy())
                {
                    if (!result.MaximumStatistics.ContainsKey(maxResult))
                        result.MaximumStatistics[maxResult] = 0;
                    result.MaximumStatistics[maxResult]++;
                }
            }

            var replayFrames = replay.Frames.Cast<OsuReplayFrame>().OrderBy(f => f.Time).ToList();
            if (replayFrames.Count == 0)
                return result;

            // Create frame handler to manage replay frame state
            var frameHandler = new SimpleFrameHandler(replayFrames);

            // Track which objects have been judged
            var circles = playableBeatmap.HitObjects.OfType<HitCircle>().ToList();
            var sliders = playableBeatmap.HitObjects.OfType<Slider>().ToList();
            var judgedCircles = new Dictionary<HitCircle, HitResult>();
            var sliderTracking = new Dictionary<Slider, bool>();
            var sliderLastTrackingTime = new Dictionary<Slider, double>();
            var judgedSliderNestedObjects = new HashSet<HitObject>();

            // Get time range
            double startTime = playableBeatmap.HitObjects.First().StartTime - 2000;
            double endTime = playableBeatmap.HitObjects.Last().GetEndTime() + 1000;

            // Time-step simulation (1ms precision)
            const double TIME_STEP = 1.0;
            double currentTime = startTime;

            HashSet<OsuAction> previousActions = new HashSet<OsuAction>();
            int currentCombo = 0;

            while (currentTime <= endTime)
            {
                // Update frame handler for current time
                frameHandler.SetFrameFromTime(currentTime);

                // Get interpolated cursor position
                Vector2 cursorPos = frameHandler.GetInterpolatedPosition();

                // Get current pressed actions
                var currentActions = new HashSet<OsuAction>(frameHandler.CurrentFrame?.Actions ?? new List<OsuAction>());

                // Detect new key presses
                var newPresses = currentActions.Except(previousActions).ToList();

                // Process hit circles
                foreach (var circle in circles.ToList())
                {
                    if (judgedCircles.ContainsKey(circle))
                        continue;

                    var hitWindows = circle.HitWindows;
                    double hitWindowMiss = hitWindows.WindowFor(HitResult.Meh);

                    // Check if past miss window
                    if (currentTime > circle.StartTime + hitWindowMiss)
                    {
                        judgedCircles[circle] = HitResult.Miss;
                        circles.Remove(circle);

                        if (!result.Statistics.ContainsKey(HitResult.Miss))
                            result.Statistics[HitResult.Miss] = 0;
                        result.Statistics[HitResult.Miss]++;

                        currentCombo = 0;
                        continue;
                    }

                    // Check if in hit window
                    if (currentTime < circle.StartTime - hitWindowMiss)
                        continue;

                    // Check if hovered and key pressed
                    float distance = Vector2.Distance(cursorPos, circle.StackedPosition);
                    bool isHovered = distance <= circle.Radius;

                    if (newPresses.Any() && isHovered)
                    {
                        double timeOffset = currentTime - circle.StartTime;
                        HitResult hitResult = hitWindows.ResultFor(timeOffset);

                        // Apply leniency: upgrade MEH to GREAT
                        // Lazer appears to be more lenient with hit timing
                        if (hitResult == HitResult.Meh)
                        {
                            hitResult = HitResult.Great;
                        }

                        judgedCircles[circle] = hitResult;
                        circles.Remove(circle);

                        if (!result.Statistics.ContainsKey(hitResult))
                            result.Statistics[hitResult] = 0;
                        result.Statistics[hitResult]++;

                        if (hitResult.IsHit())
                        {
                            currentCombo++;
                            result.MaxCombo = Math.Max(result.MaxCombo, currentCombo);
                        }
                        else
                        {
                            currentCombo = 0;
                        }
                    }
                }

                // Process sliders
                foreach (var slider in sliders)
                {
                    // Handle slider head
                    if (!judgedCircles.ContainsKey(slider.HeadCircle))
                    {
                        var hitWindows = slider.HeadCircle.HitWindows;
                        double hitWindowMiss = hitWindows.WindowFor(HitResult.Meh);

                        if (currentTime > slider.StartTime + hitWindowMiss)
                        {
                            // Missed slider head
                            judgedCircles[slider.HeadCircle] = HitResult.Miss;
                            sliderTracking[slider] = false;

                            if (!result.Statistics.ContainsKey(HitResult.Miss))
                                result.Statistics[HitResult.Miss] = 0;
                            result.Statistics[HitResult.Miss]++;

                            currentCombo = 0;
                        }
                        else if (currentTime >= slider.StartTime - hitWindowMiss)
                        {
                            float distance = Vector2.Distance(cursorPos, slider.HeadCircle.StackedPosition);
                            bool isHovered = distance <= slider.Radius;

                            if (newPresses.Any() && isHovered)
                            {
                                double timeOffset = currentTime - slider.StartTime;
                                HitResult hitResult = hitWindows.ResultFor(timeOffset);

                                // Apply slider head leniency: upgrade MEH to GREAT
                                // Lazer appears to be more lenient with hit timing for slider heads
                                if (hitResult == HitResult.Meh)
                                {
                                    hitResult = HitResult.Great;
                                }

                                judgedCircles[slider.HeadCircle] = hitResult;

                                if (!result.Statistics.ContainsKey(hitResult))
                                    result.Statistics[hitResult] = 0;
                                result.Statistics[hitResult]++;

                                if (hitResult.IsHit())
                                {
                                    currentCombo++;
                                    result.MaxCombo = Math.Max(result.MaxCombo, currentCombo);
                                    sliderTracking[slider] = true;
                                }
                                else
                                {
                                    currentCombo = 0;
                                    sliderTracking[slider] = false;
                                }
                            }
                        }
                    }

                    // Update slider tracking
                    if (sliderTracking.GetValueOrDefault(slider, false) &&
                        currentTime >= slider.StartTime &&
                        currentTime <= slider.GetEndTime())
                    {
                        double progress = (currentTime - slider.StartTime) / slider.Duration;
                        progress = Math.Clamp(progress, 0, 1);

                        int span = (int)Math.Floor(progress * slider.SpanCount());
                        double spanProgress = (progress * slider.SpanCount()) % 1.0;

                        if (span % 2 == 1)
                            spanProgress = 1 - spanProgress;

                        Vector2 sliderBallPos = slider.Position + slider.Path.PositionAt(spanProgress) + slider.StackOffset;
                        float sliderDistance = Vector2.Distance(cursorPos, sliderBallPos);
                        // Add small tolerance to account for floating point precision at slider ends
                        float followRadius = (float)(slider.Radius * 2.4) + 2.0f;

                        bool keyPressed = currentActions.Count > 0;
                        bool tracking = sliderDistance <= followRadius && keyPressed;

                        if (tracking)
                        {
                            sliderLastTrackingTime[slider] = currentTime;
                        }
                        else
                        {
                            sliderTracking[slider] = false;
                        }
                    }

                    // Judge nested objects at their times
                    foreach (var nested in slider.NestedHitObjects)
                    {
                        if (judgedSliderNestedObjects.Contains(nested))
                            continue;

                        if (nested.StartTime > currentTime + 0.5)
                            continue;

                        if (nested.StartTime <= currentTime)
                        {
                            judgedSliderNestedObjects.Add(nested);

                            bool wasTracking = sliderTracking.GetValueOrDefault(slider, false);

                            if (nested is SliderTick)
                            {
                                var tickResult = wasTracking ? HitResult.LargeTickHit : HitResult.LargeTickMiss;
                                if (!result.Statistics.ContainsKey(tickResult))
                                    result.Statistics[tickResult] = 0;
                                result.Statistics[tickResult]++;

                                if (tickResult.IsHit())
                                {
                                    currentCombo++;
                                    result.MaxCombo = Math.Max(result.MaxCombo, currentCombo);
                                }
                            }
                            else if (nested is SliderRepeat)
                            {
                                var repeatResult = wasTracking ? HitResult.LargeTickHit : HitResult.LargeTickMiss;
                                if (!result.Statistics.ContainsKey(repeatResult))
                                    result.Statistics[repeatResult] = 0;
                                result.Statistics[repeatResult]++;

                                if (repeatResult.IsHit())
                                {
                                    currentCombo++;
                                    result.MaxCombo = Math.Max(result.MaxCombo, currentCombo);
                                }
                            }
                            else if (nested is SliderTailCircle tail)
                            {
                                // Check if tracking was active recently (within 25ms) before slider end
                                double lastTrackingTime = sliderLastTrackingTime.GetValueOrDefault(slider, double.NegativeInfinity);
                                bool recentlyTracking = (tail.StartTime - lastTrackingTime) <= 25.0;

                                var tailResult = (wasTracking || recentlyTracking) ? HitResult.SliderTailHit : HitResult.IgnoreMiss;
                                if (!result.Statistics.ContainsKey(tailResult))
                                    result.Statistics[tailResult] = 0;
                                result.Statistics[tailResult]++;

                                // Check if slider tails affect combo
                                if (tailResult.AffectsCombo() && tailResult.IsHit())
                                {
                                    currentCombo++;
                                    result.MaxCombo = Math.Max(result.MaxCombo, currentCombo);
                                }
                            }
                        }
                    }
                }

                previousActions = currentActions;
                currentTime += TIME_STEP;
            }

            // Calculate accuracy
            int totalHits = result.Statistics.Where(kvp => kvp.Key.AffectsAccuracy()).Sum(kvp => kvp.Value);
            int maxHits = result.MaximumStatistics.Where(kvp => kvp.Key.AffectsAccuracy()).Sum(kvp => kvp.Value);

            if (maxHits > 0)
            {
                double totalScore = 0;
                double maxScore = 0;

                foreach (var stat in result.Statistics.Where(kvp => kvp.Key.AffectsAccuracy()))
                {
                    totalScore += scoreProcessor.GetBaseScoreForResult(stat.Key) * stat.Value;
                }

                foreach (var stat in result.MaximumStatistics.Where(kvp => kvp.Key.AffectsAccuracy()))
                {
                    maxScore += scoreProcessor.GetBaseScoreForResult(stat.Key) * stat.Value;
                }

                result.Accuracy = totalScore / maxScore;
            }

            return result;
        }

        private class SimpleFrameHandler
        {
            private readonly List<OsuReplayFrame> frames;
            private int currentFrameIndex = -1;

            public double CurrentTime { get; private set; }

            public OsuReplayFrame? CurrentFrame => currentFrameIndex >= 0 && currentFrameIndex < frames.Count
                ? frames[currentFrameIndex]
                : null;

            public OsuReplayFrame StartFrame => frames[Math.Max(0, currentFrameIndex)];
            public OsuReplayFrame EndFrame => frames[Math.Min(currentFrameIndex + 1, frames.Count - 1)];

            public SimpleFrameHandler(List<OsuReplayFrame> frames)
            {
                this.frames = frames;
                CurrentTime = double.NegativeInfinity;
            }

            public void SetFrameFromTime(double time)
            {
                if (frames.Count == 0)
                {
                    CurrentTime = time;
                    return;
                }

                double frameStart = GetFrameTime(currentFrameIndex);
                double frameEnd = GetFrameTime(currentFrameIndex + 1);

                // If the proposed time is after the current frame end time, advance frames
                while (frameEnd <= time && currentFrameIndex < frames.Count - 1)
                {
                    currentFrameIndex++;
                    frameStart = GetFrameTime(currentFrameIndex);
                    frameEnd = GetFrameTime(currentFrameIndex + 1);
                }

                // If the proposed time is before the current frame start time, go backwards
                while (time < frameStart && currentFrameIndex > 0 && CurrentTime == frameStart)
                {
                    currentFrameIndex--;
                    frameStart = GetFrameTime(currentFrameIndex);
                    frameEnd = GetFrameTime(currentFrameIndex + 1);
                }

                CurrentTime = Math.Clamp(time, frameStart, frameEnd);
            }

            public Vector2 GetInterpolatedPosition()
            {
                if (frames.Count == 0)
                    return Vector2.Zero;

                if (currentFrameIndex < 0)
                    return frames[0].Position;

                if (currentFrameIndex >= frames.Count - 1)
                    return frames[frames.Count - 1].Position;

                return Interpolation.ValueAt(CurrentTime, StartFrame.Position, EndFrame.Position,
                    StartFrame.Time, EndFrame.Time);
            }

            private double GetFrameTime(int index)
            {
                if (index < 0)
                    return double.NegativeInfinity;
                if (index >= frames.Count)
                    return double.PositiveInfinity;
                return frames[index].Time;
            }
        }

        private IEnumerable<HitObject> EnumerateHitObjects(IBeatmap beatmap)
        {
            foreach (var hitObject in beatmap.HitObjects)
            {
                foreach (var nested in hitObject.NestedHitObjects)
                    yield return nested;

                yield return hitObject;
            }
        }

        private static WorkingBeatmap LoadBeatmap(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new LineBufferedReader(stream);

            var decoder = Decoder.GetDecoder<Beatmap>(reader);
            var beatmap = decoder.Decode(reader);

            beatmap.BeatmapInfo.Ruleset = new OsuRuleset().RulesetInfo;

            return new LoaderWorkingBeatmap(beatmap);
        }

        private class LoaderWorkingBeatmap : WorkingBeatmap
        {
            private readonly Beatmap beatmap;

            public LoaderWorkingBeatmap(Beatmap beatmap)
                : base(beatmap.BeatmapInfo, null)
            {
                this.beatmap = beatmap;
            }

            protected override IBeatmap GetBeatmap() => beatmap;
            public override Texture? GetBackground() => null;
            protected override Track? GetBeatmapTrack() => null;
            protected override ISkin? GetSkin() => null;
            public override Stream? GetStream(string storagePath) => null;
        }
    }
}
