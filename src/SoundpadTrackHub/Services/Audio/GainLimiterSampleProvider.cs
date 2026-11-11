using System;
using NAudio.Wave;

namespace SoundpadTrackHub.Services.Audio
{
    /// <summary>
    /// Усиление + пиковый limiter с упреждением (look-ahead).
    /// Гарантирует, что ни один сэмпл не превышает потолок (по умолчанию −1 dBFS),
    /// поэтому усиление не даёт цифрового клиппинга и треска.
    /// Попутно собирает статистику подавления, по которой решается, безопасно ли усиление.
    /// </summary>
    public sealed class GainLimiterSampleProvider : ISampleProvider
    {
        public const double DefaultCeilingDb = -1.0;
        public const double HeavyReductionDb = 10.0;

        private readonly ISampleProvider _source;
        private readonly object _sync = new();
        private readonly int _channels;
        private readonly float _gain;
        private readonly float _ceiling;
        private readonly int _lookahead;
        private readonly float _attackCoeff;
        private readonly float _releaseCoeff;

        // Линия задержки: сэмплы и требуемое усиление для каждого кадра.
        private readonly float[] _delaySamples;
        private readonly float[] _delayRequired;

        // Монотонная очередь для минимума требуемого усиления в окне упреждения.
        private readonly long[] _dequeIndex;
        private readonly float[] _dequeValue;
        private int _dequeHead;
        private int _dequeCount;

        private float[] _input = Array.Empty<float>();
        private long _nextIndex;
        private int _held;
        private float _envelope = 1f;
        private bool _sourceEnded;

        public GainLimiterSampleProvider(ISampleProvider source, double gainDb, double ceilingDb = DefaultCeilingDb, double lookaheadMs = 5, double releaseMs = 120)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
            _channels = Math.Max(1, source.WaveFormat.Channels);
            _gain = (float)DbToLinear(gainDb);
            _ceiling = (float)DbToLinear(Math.Min(0, ceilingDb));
            GainDb = gainDb;

            var rate = Math.Max(8000, source.WaveFormat.SampleRate);
            _lookahead = Math.Max(1, (int)Math.Round(rate * lookaheadMs / 1000.0));
            _attackCoeff = (float)(1 - Math.Exp(-1.0 / Math.Max(1, _lookahead / 4.0)));
            _releaseCoeff = (float)(1 - Math.Exp(-1.0 / Math.Max(1, rate * releaseMs / 1000.0)));

            _delaySamples = new float[_lookahead * _channels];
            _delayRequired = new float[_lookahead];
            _dequeIndex = new long[_lookahead + 1];
            _dequeValue = new float[_lookahead + 1];
        }

        public WaveFormat WaveFormat { get; }

        public double GainDb { get; }

        public long FramesProcessed { get; private set; }

        public double MaxReductionDb { get; private set; }

        private double _sumReductionDb;
        private long _heavyFrames;

        /// <summary>Среднее подавление limiter'а в dB по всему треку.</summary>
        public double MeanReductionDb => FramesProcessed == 0 ? 0 : _sumReductionDb / FramesProcessed;

        /// <summary>Доля времени, когда подавление больше <see cref="HeavyReductionDb"/>.</summary>
        public double HeavyReductionRatio => FramesProcessed == 0 ? 0 : (double)_heavyFrames / FramesProcessed;

        /// <summary>Максимальный модуль выходного сэмпла (для проверки отсутствия клиппинга).</summary>
        public float OutputPeak { get; private set; }

        public static double DbToLinear(double db) => Math.Pow(10, db / 20.0);

        /// <summary>Сброс состояния (например, после перемотки).</summary>
        public void Reset()
        {
            lock (_sync)
            {
                ResetCore();
            }
        }

        private void ResetCore()
        {
            Array.Clear(_delaySamples);
            Array.Clear(_delayRequired);
            _dequeHead = 0;
            _dequeCount = 0;
            _nextIndex = 0;
            _held = 0;
            _envelope = 1f;
            _sourceEnded = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                return ReadCore(buffer, offset, count);
            }
        }

        private int ReadCore(float[] buffer, int offset, int count)
        {
            var framesWanted = count / _channels;
            var written = 0;

            while (written < framesWanted)
            {
                if (!_sourceEnded)
                {
                    var need = (framesWanted - written) * _channels;
                    if (_input.Length < need)
                    {
                        _input = new float[need];
                    }

                    var got = _source.Read(_input, 0, need) / _channels;
                    if (got == 0)
                    {
                        _sourceEnded = true;
                        continue;
                    }

                    for (var f = 0; f < got; f++)
                    {
                        Push(_input, f * _channels);
                        if (_held == _lookahead)
                        {
                            EmitOldest(buffer, offset + written * _channels);
                            written++;
                        }
                    }
                }
                else
                {
                    if (_held == 0)
                    {
                        break;
                    }

                    EmitOldest(buffer, offset + written * _channels);
                    written++;
                }
            }

            return written * _channels;
        }

        private void Push(float[] src, int srcOffset)
        {
            var slot = (int)(_nextIndex % _lookahead);
            var peak = 0f;
            var baseIdx = slot * _channels;
            for (var c = 0; c < _channels; c++)
            {
                var s = src[srcOffset + c] * _gain;
                if (float.IsNaN(s) || float.IsInfinity(s))
                {
                    s = 0f;
                }

                _delaySamples[baseIdx + c] = s;
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }

            var required = peak > _ceiling ? _ceiling / peak : 1f;
            _delayRequired[slot] = required;

            // Монотонная очередь: убираем с конца элементы, которые не меньше нового.
            while (_dequeCount > 0)
            {
                var back = (_dequeHead + _dequeCount - 1) % _dequeValue.Length;
                if (_dequeValue[back] >= required)
                {
                    _dequeCount--;
                }
                else
                {
                    break;
                }
            }

            var tail = (_dequeHead + _dequeCount) % _dequeValue.Length;
            _dequeIndex[tail] = _nextIndex;
            _dequeValue[tail] = required;
            _dequeCount++;

            _nextIndex++;
            _held++;
        }

        private void EmitOldest(float[] buffer, int dstOffset)
        {
            var oldest = _nextIndex - _held;
            while (_dequeCount > 0 && _dequeIndex[_dequeHead] < oldest)
            {
                _dequeHead = (_dequeHead + 1) % _dequeValue.Length;
                _dequeCount--;
            }

            var target = _dequeCount > 0 ? _dequeValue[_dequeHead] : 1f;
            var coeff = target < _envelope ? _attackCoeff : _releaseCoeff;
            _envelope += (target - _envelope) * coeff;

            var slot = (int)(oldest % _lookahead);
            var g = Math.Min(_envelope, _delayRequired[slot]);

            var baseIdx = slot * _channels;
            for (var c = 0; c < _channels; c++)
            {
                var v = _delaySamples[baseIdx + c] * g;
                if (v > _ceiling) v = _ceiling;
                else if (v < -_ceiling) v = -_ceiling;
                buffer[dstOffset + c] = v;
                var a = Math.Abs(v);
                if (a > OutputPeak) OutputPeak = a;
            }

            var reductionDb = g < 1f ? -20.0 * Math.Log10(g) : 0.0;
            _sumReductionDb += reductionDb;
            if (reductionDb > HeavyReductionDb) _heavyFrames++;
            if (reductionDb > MaxReductionDb) MaxReductionDb = reductionDb;
            FramesProcessed++;

            _held--;
        }
    }
}
