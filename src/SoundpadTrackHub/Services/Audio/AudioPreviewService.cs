using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services.Audio
{
    /// <summary>
    /// Предпрослушивание через устройство вывода по умолчанию.
    /// Громкость регулируется внутри цепочки сэмплов — системная громкость Windows
    /// и громкость Soundpad не меняются.
    /// </summary>
    public sealed class AudioPreviewService : IDisposable
    {
        private readonly AudioAnalyzer _analyzer;
        private readonly ILogger _log = Log.ForContext<AudioPreviewService>();

        private WaveOutEvent? _output;
        private AudioFileReader? _reader;
        private GainLimiterSampleProvider? _limiter;
        private VolumeSampleProvider? _volume;
        private CancellationTokenSource? _cts;

        public AudioPreviewService(AudioAnalyzer analyzer)
        {
            _analyzer = analyzer;
        }

        /// <summary>Воспроизведение остановилось (конец файла, ошибка или Stop).</summary>
        public event EventHandler? Stopped;

        public string? CurrentPath { get; private set; }

        public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

        public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

        public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

        public async Task<BoostPlan> PlayAsync(string path, VolumeMode mode, double volume)
        {
            Stop();

            var cts = new CancellationTokenSource();
            _cts = cts;
            var plan = await _analyzer.PlanAsync(path, mode, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return plan;
            }

            try
            {
                _reader = new AudioFileReader(path);
                ISampleProvider chain = _reader;
                if (plan.GainDb > 0)
                {
                    _limiter = new GainLimiterSampleProvider(_reader, plan.GainDb);
                    chain = _limiter;
                }

                _volume = new VolumeSampleProvider(chain) { Volume = (float)Math.Clamp(volume, 0, 1) };
                _output = new WaveOutEvent { DesiredLatency = 200 };
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_volume);
                _output.Play();
                CurrentPath = path;
                _log.Information("Preview started {File} mode={Mode}", System.IO.Path.GetFileName(path), plan.AppliedMode);
                return plan;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Preview failed for {Path}", path);
                Stop();
                throw;
            }
        }

        public void SetVolume(double volume)
        {
            if (_volume is not null)
            {
                _volume.Volume = (float)Math.Clamp(volume, 0, 1);
            }
        }

        public void Seek(TimeSpan position)
        {
            if (_reader is null)
            {
                return;
            }

            var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position > _reader.TotalTime ? _reader.TotalTime : position;
            _reader.CurrentTime = clamped;
            _limiter?.Reset();
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;

            var output = _output;
            _output = null;
            if (output is not null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                try
                {
                    output.Stop();
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Stop failed");
                }

                output.Dispose();
            }

            _reader?.Dispose();
            _reader = null;
            _limiter = null;
            _volume = null;

            if (CurrentPath is not null)
            {
                CurrentPath = null;
                Stopped?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null)
            {
                _log.Warning(e.Exception, "Playback stopped with error");
            }

            Stop();
        }

        public void Dispose() => Stop();
    }
}
