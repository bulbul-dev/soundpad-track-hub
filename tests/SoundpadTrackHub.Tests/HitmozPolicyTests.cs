using System;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;
using Xunit;

namespace SoundpadTrackHub.Tests
{
    public class HitmozDownloadPolicyTests
    {
        private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private readonly HitmozDownloadPolicy _policy = new();

        private static DownloadRequest Req(string uri, string? name = null, string? mime = "audio/mpeg", string? source = null, double sourceAgeSec = 2) => new()
        {
            DownloadUri = uri,
            SuggestedFileName = name,
            MimeType = mime,
            SourceUri = source,
            SourceStartedUtc = source is null ? null : Now.AddSeconds(-sourceAgeSec),
            NowUtc = Now,
        };

        [Fact]
        public void Allows_Https_Mp3_From_Hitmoz()
        {
            var r = _policy.Evaluate(Req("https://ru.hitmoz.org/get/music/20190820/Tones_and_I_-_Dance_Monkey_66175914.mp3"));
            Assert.True(r.IsAllowed, r.Reason);
            Assert.Equal("Tones and I - Dance Monkey 66175914.mp3", r.SafeFileName);
        }

        [Fact]
        public void Allows_Cdn_Redirect_Started_From_Hitmoz_Link()
        {
            // Реальное поведение: /get/music/...mp3 на ru.hitmoz.org перенаправляет на файловый сервер.
            var r = _policy.Evaluate(Req(
                "https://cdn13.deliciousoranges.com/s1/get/music/20260506/ENZRO_-_Ty_ne_bojjsya_nochi_81423717.mp3",
                "ENZRO_-_Ty_ne_bojjsya_nochi_81423717.mp3",
                source: "https://ru.hitmoz.org/get/music/20260506/ENZRO_-_Ty_ne_bojjsya_nochi_81423717.mp3"));
            Assert.True(r.IsAllowed, r.Reason);
            Assert.Equal("cdn13.deliciousoranges.com", r.DownloadHost);
        }

        [Fact]
        public void Blocks_Other_Host_Without_Hitmoz_Source()
        {
            var r = _policy.Evaluate(Req("https://evil.example.com/track.mp3", source: "https://ru.hitmoz.org/search?q=test"));
            Assert.False(r.IsAllowed);
        }

        [Fact]
        public void Blocks_Redirect_When_Source_Is_Stale()
        {
            var r = _policy.Evaluate(Req("https://cdn.example.com/a.mp3", source: "https://ru.hitmoz.org/get/music/1/a.mp3", sourceAgeSec: 600));
            Assert.False(r.IsAllowed);
        }

        [Fact]
        public void Blocks_Http()
        {
            var r = _policy.Evaluate(Req("http://ru.hitmoz.org/get/music/1/a.mp3"));
            Assert.False(r.IsAllowed);
            Assert.Contains("HTTPS", r.Reason);
        }

        [Fact]
        public void Blocks_Lookalike_Host()
        {
            Assert.False(_policy.Evaluate(Req("https://ru.hitmoz.org.evil.com/a.mp3")).IsAllowed);
            Assert.False(_policy.Evaluate(Req("https://hitmoz.org/a.mp3")).IsAllowed);
            Assert.False(_policy.Evaluate(Req("https://ru.hitmoz.org:8443/a.mp3")).IsAllowed);
        }

        [Theory]
        [InlineData("setup.exe")]
        [InlineData("run.bat")]
        [InlineData("run.cmd")]
        [InlineData("screen.scr")]
        [InlineData("lib.dll")]
        [InlineData("script.ps1")]
        [InlineData("track.mp3.exe")]
        [InlineData("track.exe.mp3")]
        [InlineData("installer.msi")]
        [InlineData("x.vbs")]
        public void Blocks_Executables_Even_From_Hitmoz(string name)
        {
            var r = _policy.Evaluate(Req("https://ru.hitmoz.org/get/" + name, name, mime: "application/octet-stream"));
            Assert.False(r.IsAllowed);
            Assert.Contains("опасный", r.Reason);
        }

        [Fact]
        public void Blocks_Executable_In_Url_When_Name_Looks_Fine()
        {
            var r = _policy.Evaluate(Req("https://ru.hitmoz.org/get/payload.exe", "song.mp3"));
            Assert.False(r.IsAllowed);
        }

        [Theory]
        [InlineData("track.wav")]
        [InlineData("track.flac")]
        [InlineData("track")]
        public void Blocks_Non_Mp3(string name)
        {
            var r = _policy.Evaluate(Req("https://ru.hitmoz.org/get/music/1/" + name, name));
            Assert.False(r.IsAllowed);
        }

        [Theory]
        [InlineData("text/html")]
        [InlineData("text/html; charset=utf-8")]
        [InlineData("application/x-msdownload")]
        [InlineData("application/javascript")]
        public void Blocks_Wrong_Mime(string mime)
        {
            var r = _policy.Evaluate(Req("https://ru.hitmoz.org/get/music/1/a.mp3", "a.mp3", mime));
            Assert.False(r.IsAllowed);
        }

        [Theory]
        [InlineData("audio/mpeg")]
        [InlineData("audio/mp3")]
        [InlineData("application/octet-stream")]
        [InlineData("")]
        [InlineData(null)]
        public void Allows_Audio_Or_Generic_Mime(string? mime)
        {
            Assert.True(_policy.Evaluate(Req("https://ru.hitmoz.org/get/music/1/a.mp3", "a.mp3", mime)).IsAllowed);
        }

        [Fact]
        public void Sanitizes_Path_Traversal_In_Suggested_Name()
        {
            var r = _policy.Evaluate(Req("https://ru.hitmoz.org/get/music/1/a.mp3", @"..\..\Windows\evil.mp3"));
            Assert.True(r.IsAllowed);
            Assert.Equal("evil.mp3", r.SafeFileName);
        }
    }

    public class HitmozNavigationPolicyTests
    {
        [Theory]
        [InlineData("https://ru.hitmoz.org/")]
        [InlineData("https://ru.hitmoz.org/search?q=test")]
        [InlineData("about:blank")]
        public void Allows_Hitmoz_Pages(string uri) =>
            Assert.Equal(NavigationVerdict.Allow, HitmozNavigationPolicy.Evaluate(uri, false, uri));

        [Fact]
        public void Upgrades_Http() =>
            Assert.Equal(NavigationVerdict.UpgradeToHttps, HitmozNavigationPolicy.Evaluate("http://ru.hitmoz.org/", false, null));

        [Fact]
        public void Blocks_External_Sites() =>
            Assert.Equal(NavigationVerdict.BlockExternal, HitmozNavigationPolicy.Evaluate("https://ads.example.com/", false, "https://ads.example.com/"));

        [Fact]
        public void Allows_Https_Redirect_From_Hitmoz() =>
            Assert.Equal(NavigationVerdict.Allow, HitmozNavigationPolicy.Evaluate(
                "https://cdn13.deliciousoranges.com/s1/get/music/1/a.mp3", true, "https://ru.hitmoz.org/get/music/1/a.mp3"));

        [Fact]
        public void Blocks_Redirect_From_Elsewhere() =>
            Assert.Equal(NavigationVerdict.BlockExternal, HitmozNavigationPolicy.Evaluate(
                "https://cdn.example.com/a.mp3", true, "https://other.example.com/"));
    }
}
