using System;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;
using SoundpadTrackHub.Services.Audio;
using Xunit;

namespace SoundpadTrackHub.Tests
{
    public class GainLimiterTests
    {
        private static ISampleProvider Sine(double amplitude, double seconds, int rate = 44100, int channels = 2)
        {
            var gen = new SignalGenerator(rate, channels) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = amplitude };
            return gen.Take(TimeSpan.FromSeconds(seconds));
        }

        private static float[] ReadAll(ISampleProvider p)
        {
            var result = new System.Collections.Generic.List<float>();
            var buf = new float[4096];
            int n;
            while ((n = p.Read(buf, 0, buf.Length)) > 0)
            {
                result.AddRange(buf.Take(n));
            }

            return result.ToArray();
        }

        [Theory]
        [InlineData(6.0)]
        [InlineData(12.0)]
        public void Never_Exceeds_Ceiling_On_Full_Scale_Signal(double gainDb)
        {
            var limiter = new GainLimiterSampleProvider(Sine(1.0, 1.0), gainDb);
            var output = ReadAll(limiter);
            var ceiling = GainLimiterSampleProvider.DbToLinear(GainLimiterSampleProvider.DefaultCeilingDb);
            Assert.True(output.Max(Math.Abs) <= ceiling + 1e-6, $"peak {output.Max(Math.Abs)}");
            Assert.True(limiter.MaxReductionDb > gainDb);
        }

        [Fact]
        public void Output_Length_Equals_Input_Length()
        {
            var input = ReadAll(Sine(0.5, 0.37));
            var output = ReadAll(new GainLimiterSampleProvider(Sine(0.5, 0.37), 6));
            Assert.Equal(input.Length, output.Length);
        }

        [Fact]
        public void Quiet_Signal_Is_Amplified_By_Gain_Without_Limiting()
        {
            // −20 dBFS + 6 dB = −14 dBFS, limiter не должен вмешиваться.
            var output = ReadAll(new GainLimiterSampleProvider(Sine(0.1, 0.5), 6));
            var peak = output.Max(Math.Abs);
            Assert.InRange(peak, 0.1 * 1.99, 0.1 * 2.0 + 1e-3);
        }

        [Fact]
        public void Safety_Rejects_Heavy_Limiting_For_SuperLoud()
        {
            // Почти квадратный громкий сигнал: +12 dB потребовало бы постоянного подавления ~12 dB.
            var limiter = new GainLimiterSampleProvider(Sine(1.0, 1.0), 12);
            ReadAll(limiter);
            var plan = VolumeSafety.Decide(VolumeMode.SuperLoud, new LimiterStats(limiter.MeanReductionDb, limiter.HeavyReductionRatio, limiter.MaxReductionDb, true));
            Assert.True(plan.WasDowngraded);
            Assert.Equal(VolumeMode.Normal, plan.AppliedMode);
            Assert.NotNull(plan.Warning);
        }

        [Fact]
        public void Safety_Allows_Boost_On_Quiet_Track()
        {
            var limiter = new GainLimiterSampleProvider(Sine(0.15, 1.0), 12);
            ReadAll(limiter);
            var plan = VolumeSafety.Decide(VolumeMode.SuperLoud, new LimiterStats(limiter.MeanReductionDb, limiter.HeavyReductionRatio, limiter.MaxReductionDb, true));
            Assert.False(plan.WasDowngraded);
            Assert.Equal(12, plan.GainDb);
        }

        [Fact]
        public void Safety_Falls_Back_When_File_Cannot_Be_Decoded()
        {
            var plan = VolumeSafety.Decide(VolumeMode.Loud, new LimiterStats(0, 0, 0, Decoded: false));
            Assert.Equal(VolumeMode.Normal, plan.AppliedMode);
            Assert.NotNull(plan.Warning);
        }

        [Fact]
        public void Volume_Modes_Map_To_Expected_Gain()
        {
            Assert.Equal(0, VolumeMode.Normal.GainDb());
            Assert.Equal(6, VolumeMode.Loud.GainDb());
            Assert.Equal(12, VolumeMode.SuperLoud.GainDb());
            Assert.Equal("Усиление может вызвать искажения на некоторых треках", VolumeSafety.SuperLoudCaption);
        }

        [Fact]
        public async System.Threading.Tasks.Task Boosted_Copy_Never_Touches_Original()
        {
            using var dir = new TempDir();
            var original = dir.Combine("song.wav");
            WaveFileWriter.CreateWaveFile16(original, Sine(0.1, 0.5));
            var before = File.ReadAllBytes(original);

            var temp = new TempFileManager(dir.Combine("tmp-dl"), dir.Combine("tmp-render"));
            var service = new BoostedCopyService(new AudioAnalyzer(), temp);
            var result = await service.CreateCopyAsync(original, VolumeMode.Loud);

            Assert.True(result.Created, result.Message);
            Assert.NotNull(result.OutputPath);
            Assert.NotEqual(original, result.OutputPath);
            Assert.Contains("громкая +6 dB", Path.GetFileName(result.OutputPath));
            Assert.Equal(before, File.ReadAllBytes(original));
            Assert.Empty(Directory.GetFiles(dir.Combine("tmp-render")));
        }

        [Fact]
        public async System.Threading.Tasks.Task Boosted_Copy_Refuses_Normal_Mode()
        {
            using var dir = new TempDir();
            var service = new BoostedCopyService(new AudioAnalyzer(), new TempFileManager(dir.Combine("a"), dir.Combine("b")));
            var result = await service.CreateCopyAsync(dir.Combine("x.mp3"), VolumeMode.Normal);
            Assert.False(result.Created);
        }
    }
}
