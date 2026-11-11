using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services.Audio
{
    public sealed record BoostedCopyResult(bool Created, string? OutputPath, BoostPlan Plan, string Message);

    /// <summary>
    /// Создаёт ОТДЕЛЬНУЮ усиленную копию трека. Оригинал открывается только на чтение
    /// и никогда не перезаписывается. MP3 кодируется системным кодеком Media Foundation;
    /// если он недоступен — копия сохраняется в WAV (Soundpad поддерживает оба формата).
    /// </summary>
    public sealed class BoostedCopyService
    {
        private readonly AudioAnalyzer _analyzer;
        private readonly TempFileManager _temp;
        private readonly ILogger _log = Log.ForContext<BoostedCopyService>();

        public BoostedCopyService(AudioAnalyzer analyzer, TempFileManager temp)
        {
            _analyzer = analyzer;
            _temp = temp;
        }

        public static string BuildCopyName(string originalPath, VolumeMode mode, string extension) =>
            Path.GetFileNameWithoutExtension(originalPath) + mode.FileSuffix() + extension;

        public async Task<BoostedCopyResult> CreateCopyAsync(string originalPath, VolumeMode mode, CancellationToken ct = default)
        {
            if (mode == VolumeMode.Normal)
            {
                return new BoostedCopyResult(false, null, BoostPlan.Normal(), "Выберите режим «Громкая» или «Супер громкая», чтобы создать усиленную копию.");
            }

            if (!File.Exists(originalPath))
            {
                return new BoostedCopyResult(false, null, BoostPlan.Normal(mode), "Исходный файл не найден.");
            }

            var plan = await _analyzer.PlanAsync(originalPath, mode, ct).ConfigureAwait(false);
            if (plan.WasDowngraded)
            {
                return new BoostedCopyResult(false, null, plan, plan.Warning ?? "Усиление небезопасно для этого трека.");
            }

            var folder = Path.GetDirectoryName(originalPath)!;
            return await Task.Run(() => Render(originalPath, folder, plan, ct), ct).ConfigureAwait(false);
        }

        private BoostedCopyResult Render(string originalPath, string folder, BoostPlan plan, CancellationToken ct)
        {
            string? tmp = null;
            try
            {
                tmp = _temp.CreateRenderPath(".mp3");
                if (TryEncodeMp3(originalPath, tmp, plan.GainDb))
                {
                    ct.ThrowIfCancellationRequested();
                    return Publish(tmp, originalPath, folder, plan, ".mp3");
                }

                _temp.Delete(tmp);
                tmp = _temp.CreateRenderPath(".wav");
                using (var reader = new AudioFileReader(originalPath))
                {
                    var limiter = new GainLimiterSampleProvider(reader, plan.GainDb);
                    WaveFileWriter.CreateWaveFile16(tmp, limiter);
                }

                ct.ThrowIfCancellationRequested();
                return Publish(tmp, originalPath, folder, plan, ".wav");
            }
            catch (OperationCanceledException)
            {
                _temp.Delete(tmp);
                return new BoostedCopyResult(false, null, plan, "Создание копии отменено.");
            }
            catch (Exception ex)
            {
                _temp.Delete(tmp);
                _log.Error(ex, "Boosted copy failed for {Path}", originalPath);
                return new BoostedCopyResult(false, null, BoostPlan.Normal(plan.RequestedMode),
                    "Не удалось безопасно применить усиление: " + ex.Message + ". Оригинал не изменён.");
            }
        }

        private bool TryEncodeMp3(string originalPath, string outputPath, double gainDb)
        {
            try
            {
                MediaFoundationApi.Startup();
                using var reader = new AudioFileReader(originalPath);
                var limiter = new GainLimiterSampleProvider(reader, gainDb);
                var pcm16 = new SampleToWaveProvider16(limiter);
                MediaFoundationEncoder.EncodeToMp3(pcm16, outputPath, 192_000);
                return new FileInfo(outputPath).Length > 0;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Media Foundation MP3 encoder unavailable, falling back to WAV");
                return false;
            }
        }

        private BoostedCopyResult Publish(string tmp, string originalPath, string folder, BoostPlan plan, string extension)
        {
            var target = TargetPathResolver.GetUniquePath(folder, BuildCopyName(originalPath, plan.AppliedMode, extension));
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Копия не может заменить оригинал.");
            }

            File.Move(tmp, target, overwrite: false);
            _log.Information("Boosted copy created {Target}", target);
            var fmt = extension == ".wav" ? " (WAV: системный MP3-кодер недоступен)" : string.Empty;
            return new BoostedCopyResult(true, target, plan, $"Создана копия «{Path.GetFileName(target)}»{fmt}. Оригинал не изменён.");
        }
    }
}
