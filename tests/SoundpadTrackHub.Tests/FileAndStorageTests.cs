using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;
using Xunit;

namespace SoundpadTrackHub.Tests
{
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sth-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    public class FileNameTests
    {
        [Theory]
        [InlineData("ENZRO_-_Ty_ne_bojjsya_nochi_81423717.mp3", "ENZRO - Ty ne bojjsya nochi 81423717.mp3")]
        [InlineData("a%20b.MP3", "a b.mp3")]
        [InlineData("con.mp3", "_con.mp3")]
        [InlineData("bad<>:\"|?*name.mp3", "bad name.mp3")]
        [InlineData("/etc/passwd/../x.mp3", "x.mp3")]
        [InlineData("   ", "")]
        public void Sanitize(string input, string expected) => Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

        [Fact]
        public void Unique_Path_Adds_Number()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\m\a.mp3", @"C:\m\a (2).mp3" };
            Assert.Equal(@"C:\m\a (3).mp3", TargetPathResolver.GetUniquePath(@"C:\m", "a.mp3", existing.Contains));
        }

        [Fact]
        public void Existing_File_Is_Not_Overwritten_Without_Decision()
        {
            Func<string, bool> exists = p => p == @"C:\m\a.mp3";
            Assert.Null(TargetPathResolver.Resolve(@"C:\m", "a.mp3", ExistingFileDecision.Cancel, exists));
            Assert.Equal(@"C:\m\a (2).mp3", TargetPathResolver.Resolve(@"C:\m", "a.mp3", ExistingFileDecision.KeepBoth, exists));
            Assert.Equal(@"C:\m\a.mp3", TargetPathResolver.Resolve(@"C:\m", "a.mp3", ExistingFileDecision.Overwrite, exists));
            Assert.Equal(@"C:\m\b.mp3", TargetPathResolver.Resolve(@"C:\m", "b.mp3", ExistingFileDecision.Cancel, exists));
        }

        [Fact]
        public void Inside_Folder_Check()
        {
            Assert.True(TargetPathResolver.IsInsideFolder(@"C:\m\x\a.mp3", @"C:\m"));
            Assert.False(TargetPathResolver.IsInsideFolder(@"C:\m\..\Windows\a.mp3", @"C:\m"));
            Assert.False(TargetPathResolver.IsInsideFolder(@"C:\music2\a.mp3", @"C:\music"));
        }
    }

    public class Mp3ValidatorTests
    {
        [Fact]
        public void Accepts_Id3_And_Frame_Sync()
        {
            Assert.True(Mp3Validator.LooksLikeMp3Header(Encoding.ASCII.GetBytes("ID3\u0004\0\0\0\0\0\0"), out _));
            Assert.True(Mp3Validator.LooksLikeMp3Header(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, out _));
        }

        [Fact]
        public void Rejects_Exe_And_Html()
        {
            Assert.False(Mp3Validator.LooksLikeMp3Header(Encoding.ASCII.GetBytes("MZ\u0090\0"), out var r1));
            Assert.Contains("исполняемый", r1);
            Assert.False(Mp3Validator.LooksLikeMp3Header(Encoding.ASCII.GetBytes("<!DOCTYPE html>"), out var r2));
            Assert.Contains("веб-страницу", r2);
        }

        [Fact]
        public void Rejects_Tiny_File()
        {
            using var dir = new TempDir();
            var path = dir.Combine("a.mp3");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ID3"));
            Assert.False(Mp3Validator.LooksLikeMp3(path, out _));
        }
    }

    public class TempFileManagerTests
    {
        [Fact]
        public void Delete_Removes_File_And_Partial_Download()
        {
            using var dir = new TempDir();
            var temp = new TempFileManager(dir.Combine("dl"), dir.Combine("render"));
            var path = temp.CreateDownloadPath();
            File.WriteAllText(path, "x");
            File.WriteAllText(Path.ChangeExtension(path, ".mp3.crdownload"), "partial");

            temp.Delete(path);

            Assert.Empty(Directory.GetFiles(dir.Combine("dl")));
        }

        [Fact]
        public void CleanupAll_Empties_Temp_Folders()
        {
            using var dir = new TempDir();
            var temp = new TempFileManager(dir.Combine("dl"), dir.Combine("render"));
            File.WriteAllText(temp.CreateDownloadPath(), "x");
            File.WriteAllText(temp.CreateRenderPath(".wav"), "x");
            Assert.Equal(2, temp.CleanupAll());
        }
    }

    public class SettingsServiceTests
    {
        [Fact]
        public void Defaults_Volume_Normal_And_Default_Folder()
        {
            using var dir = new TempDir();
            var svc = new JsonSettingsService(dir.Combine("settings.json"), @"C:\Music\STH");
            Assert.Equal(VolumeMode.Normal, svc.Current.VolumeMode);
            Assert.Equal(@"C:\Music\STH", svc.Current.DownloadFolder);
        }

        [Fact]
        public void Roundtrip_Persists_Volume_Mode_And_Writes_Marker()
        {
            using var dir = new TempDir();
            var file = dir.Combine("settings.json");
            var marker = dir.Combine("downloads_path.txt");
            var svc = new JsonSettingsService(file, @"C:\Music\STH", marker);
            svc.Update(s =>
            {
                s.VolumeMode = VolumeMode.SuperLoud;
                s.PreviewVolume = 5; // будет ограничено 1.0
                s.DownloadFolder = @"D:\Tracks";
            });

            var reloaded = new JsonSettingsService(file, @"C:\Music\STH");
            Assert.Equal(VolumeMode.SuperLoud, reloaded.Current.VolumeMode);
            Assert.Equal(1.0, reloaded.Current.PreviewVolume);
            Assert.Equal(@"D:\Tracks", File.ReadAllText(marker));
        }

        [Fact]
        public void Corrupted_File_Falls_Back_To_Defaults()
        {
            using var dir = new TempDir();
            var file = dir.Combine("settings.json");
            File.WriteAllText(file, "{ not json");
            var svc = new JsonSettingsService(file, @"C:\Music\STH");
            Assert.Equal(VolumeMode.Normal, svc.Current.VolumeMode);
            Assert.True(File.Exists(file + ".broken"));
        }
    }

    public class DownloadHistoryTests
    {
        [Fact]
        public void Add_Replaces_Record_For_Same_Path_And_Persists()
        {
            using var dir = new TempDir();
            var file = dir.Combine("downloads.json");
            var h = new DownloadHistoryService(file);
            h.Add(new DownloadRecord { FilePath = @"C:\m\a.mp3", FileName = "a.mp3" });
            h.Add(new DownloadRecord { FilePath = @"C:\M\A.mp3", FileName = "A.mp3" });
            h.Add(new DownloadRecord { FilePath = @"C:\m\b.mp3", FileName = "b.mp3" });

            var reloaded = new DownloadHistoryService(file);
            Assert.Equal(2, reloaded.GetAll().Count);
        }
    }
}
