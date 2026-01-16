using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.IO.Legacy;
using osu.Game.Replays;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
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
        private readonly IBeatmap playableBeatmap;
        private readonly OsuRuleset ruleset;

        public ReplayAnalyzer(string beatmapPath)
        {
            workingBeatmap = LoadBeatmap(beatmapPath);
            ruleset = new OsuRuleset();
            playableBeatmap = workingBeatmap.GetPlayableBeatmap(ruleset.RulesetInfo);
        }

        public ReplaySimulationResult AnalyzeReplay(string replayPath)
        {
            var replay = LoadReplay(replayPath);
            return SimulateReplay(replay);
        }

        private Replay LoadReplay(string replayPath)
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
            sr.ReadInt32(); // Mods
            sr.ReadString(); // HP graph
            sr.ReadDateTime(); // Date

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

            return replay;
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

            int currentCombo = 0;
            int frameIndex = 0;
            var replayFrames = replay.Frames.Cast<OsuReplayFrame>().ToList();

            // Process each hit object
            foreach (var hitObject in playableBeatmap.HitObjects)
            {
                if (hitObject is HitCircle circle)
                {
                    var circleResult = ProcessHitCircle(circle, replayFrames, ref frameIndex);

                    if (!result.Statistics.ContainsKey(circleResult))
                        result.Statistics[circleResult] = 0;
                    result.Statistics[circleResult]++;

                    if (circleResult.IsHit())
                    {
                        currentCombo++;
                        result.MaxCombo = Math.Max(result.MaxCombo, currentCombo);
                    }
                    else if (circleResult == HitResult.Miss)
                    {
                        currentCombo = 0;
                    }
                }
                else if (hitObject is Slider slider)
                {
                    var sliderResults = ProcessSlider(slider, replayFrames, ref frameIndex);

                    foreach (var sliderResult in sliderResults)
                    {
                        if (!result.Statistics.ContainsKey(sliderResult))
                            result.Statistics[sliderResult] = 0;
                        result.Statistics[sliderResult]++;

                        if (sliderResult.AffectsCombo())
                        {
                            if (sliderResult.IsHit())
                            {
                                currentCombo++;
                                result.MaxCombo = Math.Max(result.MaxCombo, currentCombo);
                            }
                            else if (sliderResult.BreaksCombo())
                            {
                                currentCombo = 0;
                            }
                        }
                    }
                }
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

        private HitResult ProcessHitCircle(HitCircle circle, List<OsuReplayFrame> frames, ref int frameIndex)
        {
            var hitWindows = circle.HitWindows;
            const float hitRadius = 64f; // OsuHitObject.OBJECT_RADIUS

            // Find frames around the hit circle's time
            while (frameIndex < frames.Count && frames[frameIndex].Time < circle.StartTime - hitWindows.WindowFor(HitResult.Meh))
                frameIndex++;

            double closestTime = double.MaxValue;
            OsuReplayFrame? hitFrame = null;

            // Look for a click within the hit window
            for (int i = frameIndex; i < frames.Count; i++)
            {
                var frame = frames[i];
                double timeOffset = frame.Time - circle.StartTime;

                if (timeOffset > hitWindows.WindowFor(HitResult.Meh))
                    break;

                if (frame.Actions.Count > 0)
                {
                    // Check if cursor is within hit circle
                    float distance = Vector2.Distance(frame.Position, circle.StackedPosition);

                    if (distance <= hitRadius && Math.Abs(timeOffset) < Math.Abs(closestTime))
                    {
                        closestTime = timeOffset;
                        hitFrame = frame;
                    }
                }
            }

            if (hitFrame != null)
            {
                return hitWindows.ResultFor(closestTime);
            }

            return HitResult.Miss;
        }

        private List<HitResult> ProcessSlider(Slider slider, List<OsuReplayFrame> frames, ref int frameIndex)
        {
            var results = new List<HitResult>();
            var hitWindows = slider.HitWindows;

            // Process slider head (same as hit circle)
            var headResult = ProcessHitCircle(slider.HeadCircle, frames, ref frameIndex);
            results.Add(headResult);

            // Process slider ticks and repeats
            foreach (var nested in slider.NestedHitObjects)
            {
                if (nested is SliderTick tick)
                {
                    // Check if player is tracking the slider at tick time
                    bool tracking = IsTrackingSlider(slider, frames, tick.StartTime);
                    results.Add(tracking ? HitResult.LargeTickHit : HitResult.LargeTickMiss);
                }
                else if (nested is SliderRepeat repeat)
                {
                    bool tracking = IsTrackingSlider(slider, frames, repeat.StartTime);
                    results.Add(tracking ? HitResult.LargeTickHit : HitResult.LargeTickMiss);
                }
                else if (nested is SliderTailCircle tail)
                {
                    bool tracking = IsTrackingSlider(slider, frames, tail.StartTime);
                    results.Add(tracking ? HitResult.SliderTailHit : HitResult.IgnoreMiss);
                }
            }

            return results;
        }

        private bool IsTrackingSlider(Slider slider, List<OsuReplayFrame> frames, double time)
        {
            // Find frame at the given time
            var frame = frames.FirstOrDefault(f => Math.Abs(f.Time - time) < 50);
            if (frame == null) return false;

            // Get slider position at this time
            var progress = (time - slider.StartTime) / slider.Duration;
            progress = Math.Clamp(progress, 0, 1);

            // Simple approximation: check if cursor is reasonably close to slider
            // In reality this should trace the slider path
            const float followRadius = 64f * 2.4f;
            float distance = Vector2.Distance(frame.Position, slider.StackedPosition);

            return distance <= followRadius;
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
