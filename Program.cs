using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Skinning;

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
                var score = analyzer.AnalyzeReplay(replayPath);

                Console.WriteLine($"Accuracy: {score.Accuracy:P2}");
                Console.WriteLine($"Max Combo: {score.MaxCombo}/{GetMaxComboFromStats(score)}");
                Console.WriteLine();

                var great = score.Statistics.GetValueOrDefault(HitResult.Great, 0);
                var ok = score.Statistics.GetValueOrDefault(HitResult.Ok, 0);
                var meh = score.Statistics.GetValueOrDefault(HitResult.Meh, 0);
                var miss = score.Statistics.GetValueOrDefault(HitResult.Miss, 0);

                Console.WriteLine($"GREAT: {great}");
                Console.WriteLine($"OK: {ok}");
                Console.WriteLine($"MEH: {meh}");
                Console.WriteLine($"MISS: {miss}");
                Console.WriteLine();

                var smallTickHit = score.Statistics.GetValueOrDefault(HitResult.SmallTickHit, 0);
                var smallTickMiss = score.Statistics.GetValueOrDefault(HitResult.SmallTickMiss, 0);
                var smallTickMax = score.MaximumStatistics.GetValueOrDefault(HitResult.SmallTickHit, 0);

                var largeTickHit = score.Statistics.GetValueOrDefault(HitResult.LargeTickHit, 0);
                var largeTickMiss = score.Statistics.GetValueOrDefault(HitResult.LargeTickMiss, 0);
                var largeTickMax = score.MaximumStatistics.GetValueOrDefault(HitResult.LargeTickHit, 0);

                var sliderTailHit = score.Statistics.GetValueOrDefault(HitResult.SliderTailHit, 0);
                var sliderTailMax = score.MaximumStatistics.GetValueOrDefault(HitResult.SliderTailHit, 0);

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

        static int GetMaxComboFromStats(ScoreInfo score)
        {
            return score.MaximumStatistics
                .Where(kvp => kvp.Key.AffectsCombo())
                .Sum(kvp => kvp.Value);
        }
    }

    class ReplayAnalyzer : LegacyScoreDecoder
    {
        private readonly WorkingBeatmap workingBeatmap;
        private readonly Dictionary<int, Ruleset> rulesets = new()
        {
            { 0, new OsuRuleset() }
        };

        public ReplayAnalyzer(string beatmapPath)
        {
            workingBeatmap = LoadBeatmap(beatmapPath);
        }

        public ScoreInfo AnalyzeReplay(string replayPath)
        {
            using var stream = File.OpenRead(replayPath);
            var score = Parse(stream);
            return score.ScoreInfo;
        }

        protected override Ruleset GetRuleset(int rulesetId)
        {
            if (!rulesets.TryGetValue(rulesetId, out var ruleset))
            {
                throw new NotSupportedException($"Ruleset {rulesetId} is not supported. Only osu! standard (0) is supported.");
            }
            return ruleset;
        }

        protected override WorkingBeatmap GetBeatmap(string md5Hash)
        {
            return workingBeatmap;
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
