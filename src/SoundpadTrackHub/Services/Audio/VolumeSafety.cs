using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Serilog;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services.Audio
{
    /// <summary>Итог проверки: какой режим реально применяется.</summary>
    public sealed record BoostPlan(VolumeMode RequestedMode, VolumeMode AppliedMode, string? Warning)
    {
        public double GainDb => AppliedMode.GainDb();

        public bool WasDowngraded => RequestedMode != AppliedMode;

        public static BoostPlan Normal(VolumeMode requested = VolumeMode.Normal, string? warning = null) =>
            new(requested, VolumeMode.Normal, warning);
    }

    public sealed record LimiterStats(double MeanReductionDb, double HeavyReductionRatio, double MaxReductionDb, bool Decoded);

    /// <summary>
    /// Решает, можно ли применить усиление без сильных искажений. Limiter исключает
    /// клиппинг, но если ему приходится постоянно «давить» трек на 7+ dB, звук
    /// становится плоским и искажённым — тогда используется нормальная громкость.
    /// </summary>
    public static class VolumeSafety
    {
        public const double MaxMeanReductionDb = 7.0;
        public const double MaxHeavyRatio = 0.15;

        public const string SuperLoudCaption = "Усиление может вызвать искажения на некоторых треках";

        public static BoostPlan Decide(VolumeMode requested, LimiterStats stats)
        {
            if (requested == VolumeMode.Normal)
            {
                return BoostPlan.Normal();
            }

            if (!stats.Decoded)
            {
                return BoostPlan.Normal(requested,
                    "Не удалось проанализировать файл, усиление не применено. Используется нормальная громкость.");
            }

            if (stats.MeanReductionDb > MaxMeanReductionDb || stats.HeavyReductionRatio > MaxHeavyRatio)
            {
                return BoostPlan.Normal(requested,
                    $"Трек уже очень громкий: режим «{requested.DisplayName()}» дал бы сильные искажения. Используется нормальная громкость.");
            }

            return new BoostPlan(requested, requested, null);
        }
    }

    /// <summary>Прогоняет файл через limiter без вывода звука и кэширует результат.</summary>
    public sealed class AudioAnalyzer
    {
        private readonly ConcurrentDictionary<string, LimiterStats> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger _log = Log.ForContext<AudioAnalyzer>();

        public Task<BoostPlan> PlanAsync(string path, VolumeMode mode, CancellationToken ct = default)
        {
            if (mode == VolumeMode.Normal)
            {
                return Task.FromResult(BoostPlan.Normal());
            }

            return Task.Run(() => VolumeSafety.Decide(mode, Analyze(path, mode, ct)), ct);
        }

        public LimiterStats Analyze(string path, VolumeMode mode, CancellationToken ct = default)
        {
            string key;
            try
            {
                var info = new FileInfo(path);
                key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{mode}";
            }
            catch (Exception)
            {
                return new LimiterStats(0, 0, 0, false);
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            try
            {
                using var reader = new AudioFileReader(path);
                var limiter = new GainLimiterSampleProvider(reader, mode.GainDb());
                var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
                while (limiter.Read(buffer, 0, buffer.Length) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var stats = new LimiterStats(limiter.MeanReductionDb, limiter.HeavyReductionRatio, limiter.MaxReductionDb, limiter.FramesProcessed > 0);
                _cache[key] = stats;
                _log.Information("Analyzed {File} for {Mode}: meanGR={Mean:F1}dB heavy={Heavy:P0} max={Max:F1}dB",
                    Path.GetFileName(path), mode, stats.MeanReductionDb, stats.HeavyReductionRatio, stats.MaxReductionDb);
                return stats;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Audio analysis failed for {Path}", path);
                return new LimiterStats(0, 0, 0, false);
            }
        }
    }
}
