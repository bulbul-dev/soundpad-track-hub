using System;
using System.IO;
using System.Linq;
using SoundpadTrackHub.Integration;
using Xunit;

namespace SoundpadTrackHub.Tests
{
    public class SoundListTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sth-soundlist-" + Guid.NewGuid().ToString("N"));

        public SoundListTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string WriteList()
        {
            var path = Path.Combine(_dir, "soundlist.spl");
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Soundlist>\n" +
                "  <Sound hash=\"1-2-3\" url=\"C:\\old\\a.mp3\" artist=\"Старый\" title=\"Трек А\" duration=\"1:00\" addedOn=\"2025-01-01\"/>\n" +
                "  <Sound hash=\"4-5-6\" url=\"C:\\old\\b.mp3\" title=\"Трек Б\" duration=\"2:00\" addedOn=\"2025-01-02\"/>\n" +
                "  <Categories>\n    <Category name=\"Все\" icon=\"x\">\n      <Category name=\"Моя\" icon=\"gen_M\">\n        <Sound id=\"1\"/>\n      </Category>\n    </Category>\n  </Categories>\n" +
                "</Soundlist>\n");
            return path;
        }

        [Fact]
        public void Load_ReadsSoundsAndNestedCategories()
        {
            var list = new SoundpadSoundList(WriteList(), Path.Combine(_dir, "bak"));
            var sounds = list.Load();
            Assert.Equal(2, sounds.Count);
            Assert.Equal("Старый - Трек А", sounds[0].DisplayName);
            Assert.Equal("Без категории", sounds[0].CategoryText);
            Assert.Equal(2, sounds[1].Index);
            Assert.Equal("Моя", sounds[1].CategoryText);
        }

        [Fact]
        public void Replace_ChangesOnlyTargetSound_KeepsCategories_MakesBackup()
        {
            var path = WriteList();
            var track = Path.Combine(_dir, "VILLIAN, madk1d - DINASTIYA 81965580.mp3");
            File.WriteAllBytes(track, new byte[] { 1, 2, 3 });
            var list = new SoundpadSoundList(path, Path.Combine(_dir, "bak"));

            var backup = list.Replace(2, SoundpadTrackInfo.FromFileName(track, TimeSpan.FromSeconds(150)), @"C:\old\b.mp3");

            Assert.True(File.Exists(backup));
            var sounds = list.Load();
            Assert.Equal(@"C:\old\a.mp3", sounds[0].Url);
            Assert.Equal(track, sounds[1].Url);
            Assert.Equal("VILLIAN, madk1d - DINASTIYA", sounds[1].DisplayName);
            Assert.Equal("2:30", sounds[1].Duration);
            Assert.Equal("Моя", sounds[1].CategoryText);
            var xml = File.ReadAllText(path);
            Assert.Contains("hash=\"1-2-3\"", xml);
            Assert.DoesNotContain("hash=\"4-5-6\"", xml);
        }

        [Fact]
        public void Replace_RefusesWhenListChanged()
        {
            var path = WriteList();
            var track = Path.Combine(_dir, "x.mp3");
            File.WriteAllBytes(track, new byte[] { 1 });
            var list = new SoundpadSoundList(path, Path.Combine(_dir, "bak"));
            Assert.Throws<InvalidOperationException>(() => list.Replace(1, SoundpadTrackInfo.FromFileName(track, null), @"C:\other.mp3"));
            Assert.Contains(@"C:\old\a.mp3", File.ReadAllText(path));
        }

        [Theory]
        [InlineData("ENZRO - Ty ne bojjsya nochi 81423717.mp3", "ENZRO", "Ty ne bojjsya nochi")]
        [InlineData("просто трек.mp3", "", "просто трек")]
        public void TrackInfo_ParsesFileName(string file, string artist, string title)
        {
            var info = SoundpadTrackInfo.FromFileName(Path.Combine(_dir, file), null);
            Assert.Equal(artist, info.Artist);
            Assert.Equal(title, info.Title);
        }
    }
}
