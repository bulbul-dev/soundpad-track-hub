using System.Linq;
using System.Text;
using SoundpadTrackHub.Integration;
using SoundpadTrackHub.Models;
using Xunit;

namespace SoundpadTrackHub.Tests
{
    public class SoundpadRemoteParsingTests
    {
        // Реальный ответ Soundpad 4.0.35 на GetCategories(false, false).
        private const string CategoriesXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Categories>
  <Category index=""1"" type=""1"" name=""Все звуки"" hidden=""true""/>
  <Category index=""2"" name=""Мои звуки""/>
  <Category index=""8"" name=""вечеринка""/>
  <Category index=""9"" name=""Soundpad Track Hub""/>
</Categories>";

        [Fact]
        public void Parses_Categories_And_Skips_All_Sounds()
        {
            var cats = SoundpadRemoteClient.ParseCategories(CategoriesXml);
            Assert.DoesNotContain(cats, c => c.Index == 1);
            Assert.Equal(9, SoundpadRemoteClient.FindCategory(cats, "soundpad track hub ")!.Index);
            Assert.Null(SoundpadRemoteClient.FindCategory(cats, "Другая"));
        }

        [Fact]
        public void Parses_Sound_Urls_From_Category()
        {
            const string xml = @"<Categories><Category index=""9"" name=""Soundpad Track Hub"">
<Sound index=""356"" url=""D:\плагин для саундбада\музыа\ENZRO - Ty ne bojjsya nochi 81423717.mp3"" artist=""ENZRO"" duration=""3:39""/>
</Category></Categories>";
            var urls = SoundpadRemoteClient.ParseSoundUrls(xml);
            Assert.Equal(@"D:\плагин для саундбада\музыа\ENZRO - Ty ne bojjsya nochi 81423717.mp3", urls.Single());
        }

        [Fact]
        public void Quote_Rejects_Injection()
        {
            Assert.Equal("\"C:\\a b.mp3\"", SoundpadRemoteClient.Quote(@"C:\a b.mp3"));
            Assert.Throws<SoundpadRemoteException>(() => SoundpadRemoteClient.Quote("x\"); DoStopSound(\""));
        }

        [Fact]
        public void EnsureOk_Throws_On_Error_Response()
        {
            SoundpadRemoteClient.EnsureOk("R-200");
            var ex = Assert.Throws<SoundpadRemoteException>(() => SoundpadRemoteClient.EnsureOk("R-204: File does not exist."));
            Assert.Contains("R-204", ex.Message);
        }

        [Fact]
        public void Request_Encoding_Handles_Cyrillic_Paths()
        {
            // Soundpad читает команды в ANSI-кодировке системы: кириллица должна кодироваться без «?».
            const string command = @"DoAddSound(""D:\плагин\музыка.mp3"", 9, 1)";
            var candidates = SoundpadRemoteClient.CandidateEncodings();
            Assert.Contains(candidates, e => SoundpadRemoteClient.CanEncode(e, command));
            Assert.Contains(candidates, e => e.CodePage == 65001);
        }
    }

    public class SoundpadVolumeProfileTests
    {
        [Theory]
        [InlineData(SoundpadVolumeProfile.Normal, 80, true, null)]
        [InlineData(SoundpadVolumeProfile.Loud, 95, true, null)]
        [InlineData(SoundpadVolumeProfile.Loud2, 107, true, null)]
        [InlineData(SoundpadVolumeProfile.SuperLoud, 107, false, 30)]
        public void Profiles_Match_Screenshots(SoundpadVolumeProfile profile, int db, bool dynamic, int? fixedDb)
        {
            var s = profile.Settings();
            Assert.Equal(db, s.TargetDecibel);
            Assert.Equal(dynamic, s.Dynamic);
            Assert.Equal(fixedDb, s.FixedAdjustmentDecibel);
        }

        [Fact]
        public void Match_Recognizes_Current_Soundpad_Values()
        {
            // Значения, найденные в реестре: 107 дБ, фиксированная, подстройка 30 дБ.
            Assert.Equal(SoundpadVolumeProfile.SuperLoud, SoundpadVolumeProfileExtensions.Match(new SoundpadNormalization(107, false, 30)));
            // В динамическом режиме значение подстройки не важно.
            Assert.Equal(SoundpadVolumeProfile.Loud2, SoundpadVolumeProfileExtensions.Match(new SoundpadNormalization(107, true, 30)));
            Assert.Null(SoundpadVolumeProfileExtensions.Match(new SoundpadNormalization(90, true, null)));
        }
    }

    public class HitmozSearchParsingTests
    {
        [Fact]
        public void Parses_Search_Results_And_Keeps_Only_Hitmoz_Mp3_Links()
        {
            const string json = @"[
 {""id"":""track-id-64291868"",""artist"":""Кино, Виктор Цой"",""title"":""Хочу перемен"",""duration"":""05:17"",
  ""download"":""https://ru.hitmoz.org/get/music/20190518/Kino_Viktor_Cojj_-_KHochu_peremen_64291868.mp3""},
 {""id"":""track-id-2"",""artist"":""A"",""title"":""B"",""duration"":""1:00"",""download"":""https://evil.example.com/x.exe""},
 {""id"":""bad\""id"",""artist"":""A"",""title"":""B"",""duration"":""1:00"",""download"":""""}
]";
            var tracks = HitmozWebViewController.ParseTracks(json);
            Assert.Equal(2, tracks.Count);
            Assert.Equal("Хочу перемен", tracks[0].Title);
            Assert.EndsWith(".mp3", tracks[0].DownloadUrl);
            Assert.Equal(string.Empty, tracks[1].DownloadUrl);
        }

        [Fact]
        public void Empty_Or_Null_Result_Gives_Empty_List()
        {
            Assert.Empty(HitmozWebViewController.ParseTracks("[]"));
            Assert.Empty(HitmozWebViewController.ParseTracks("null"));
        }

        [Theory]
        [InlineData("track-id-64291868", true)]
        [InlineData("x'];alert(1);//", false)]
        [InlineData("", false)]
        public void Track_Id_Is_Validated_Before_Use_In_Script(string id, bool ok) =>
            Assert.Equal(ok, HitmozWebViewController.IsSafeTrackId(id));
    }
}
