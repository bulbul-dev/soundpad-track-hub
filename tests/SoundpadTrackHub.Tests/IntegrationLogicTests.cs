using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Data;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Integration;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;
using Xunit;

namespace SoundpadTrackHub.Tests
{
    public class DockPlacementTests
    {
        private static readonly PixelRect Work = new(0, 0, 2560, 1392);

        [Fact]
        public void Docks_Right_When_There_Is_Room()
        {
            var r = DockPlacementCalculator.Calculate(new PixelRect(200, 150, 900, 750), Work, 460, 480, 6);
            Assert.Equal(DockSide.Right, r.Side);
            Assert.Equal(new PixelRect(906, 150, 1366, 750), r.Bounds);
        }

        [Fact]
        public void Docks_Left_When_No_Room_On_Right()
        {
            // Реальный случай с проверки: Soundpad 1280 px шириной у x=830, Track Hub 640 px.
            var r = DockPlacementCalculator.Calculate(new PixelRect(830, 270, 2110, 1245), Work, 640, 480, 6);
            Assert.Equal(DockSide.Left, r.Side);
            Assert.Equal(184, r.Bounds.Left);
            Assert.Equal(270, r.Bounds.Top);
            Assert.Equal(975, r.Bounds.Height);
        }

        [Fact]
        public void Overlays_Screen_Edge_When_Soundpad_Is_Maximized()
        {
            var r = DockPlacementCalculator.Calculate(new PixelRect(0, 0, 2560, 1392), Work, 460, 480, 6);
            Assert.Equal(DockSide.Overlay, r.Side);
            Assert.Equal(2560 - 460, r.Bounds.Left);
        }

        [Fact]
        public void Height_Is_Clamped_To_Minimum_And_Work_Area()
        {
            var small = DockPlacementCalculator.Calculate(new PixelRect(100, 1300, 500, 1350), Work, 460, 480, 6);
            Assert.Equal(480, small.Bounds.Height);
            Assert.True(small.Bounds.Bottom <= Work.Bottom);

            var tall = DockPlacementCalculator.Calculate(new PixelRect(100, -50, 500, 3000), Work, 460, 480, 6);
            Assert.Equal(Work.Height, tall.Bounds.Height);
            Assert.Equal(0, tall.Bounds.Top);
        }
    }

    public class AutostartTests
    {
        private sealed class FakeStore : IRunKeyStore
        {
            public Dictionary<string, string> Values { get; } = new();

            public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;

            public void Set(string name, string value) => Values[name] = value;

            public void Delete(string name) => Values.Remove(name);
        }

        [Fact]
        public void Enable_Writes_Quoted_Path_With_Autostart_Flag_And_Disable_Removes()
        {
            var store = new FakeStore();
            var svc = new AutostartService(store, @"C:\Users\u\AppData\Local\Programs\SoundpadTrackHub\SoundpadTrackHub.exe");
            Assert.False(svc.IsEnabled);
            Assert.True(svc.SetEnabled(true));
            Assert.Equal("\"C:\\Users\\u\\AppData\\Local\\Programs\\SoundpadTrackHub\\SoundpadTrackHub.exe\" --autostart", store.Values["SoundpadTrackHub"]);
            Assert.True(svc.IsEnabled);
            svc.SetEnabled(false);
            Assert.Empty(store.Values);
        }

        [Fact]
        public void RemoveAll_Removes_Legacy_Entries()
        {
            var store = new FakeStore();
            store.Set("SoundpadTrackHub", "x");
            store.Set("SoundpadTrackHubWatcher", "y");
            store.Set("Soundpad", "keep");
            new AutostartService(store, @"C:\a.exe").RemoveAll();
            Assert.Equal(new[] { "Soundpad" }, store.Values.Keys.ToArray());
        }
    }

    public class SingleInstanceTests
    {
        [Fact]
        public void Second_Instance_Is_Rejected()
        {
            var name = "STHTest-" + Guid.NewGuid().ToString("N");
            using var first = SingleInstanceGuard.Acquire(name);
            Assert.True(first.IsFirstInstance);

            // Mutex реентерабелен в своём потоке; вторая копия — это другой поток/процесс.
            var secondIsFirst = true;
            var t = new System.Threading.Thread(() =>
            {
                using var second = SingleInstanceGuard.Acquire(name);
                secondIsFirst = second.IsFirstInstance;
            });
            t.Start();
            t.Join();
            Assert.False(secondIsFirst);
        }

        [Fact]
        public void Second_Instance_Can_Signal_First()
        {
            var name = "STHTest-" + Guid.NewGuid().ToString("N");
            using var first = SingleInstanceGuard.Acquire(name);
            using var signaled = new System.Threading.ManualResetEventSlim();
            first.ListenForActivation(() => signaled.Set());
            using (var second = SingleInstanceGuard.Acquire(name))
            {
                second.SignalFirstInstance();
            }

            Assert.True(signaled.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    public class ConverterTests
    {
        [Fact]
        public void EnumEquals_Does_Not_Write_Back_Unchecked_Value()
        {
            var c = new EnumEqualsConverter();
            Assert.Equal(true, c.Convert(VolumeMode.Loud, typeof(bool), "Loud", null!));
            Assert.Equal(VolumeMode.SuperLoud, c.ConvertBack(true, typeof(VolumeMode), "SuperLoud", null!));
            Assert.Same(Binding.DoNothing, c.ConvertBack(false, typeof(VolumeMode), "SuperLoud", null!));
        }
    }

    public class NoMockInProductionTests
    {
        [Fact]
        public void App_Assembly_Contains_No_Mock_Providers()
        {
            var types = typeof(App).Assembly.GetTypes().Select(t => t.Name);
            Assert.DoesNotContain(types, n => n.Contains("Mock", StringComparison.OrdinalIgnoreCase)
                                           || n.Contains("Fake", StringComparison.OrdinalIgnoreCase)
                                           || n.Contains("Demo", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void App_Does_Not_Use_HttpClient_For_Hitmoz()
        {
            // Hitmoz открывается только в WebView2; HttpClient в приложении не используется.
            var refs = typeof(App).Assembly.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                .Where(f => f.FieldType == typeof(System.Net.Http.HttpClient));
            Assert.Empty(refs);
        }
    }
}
