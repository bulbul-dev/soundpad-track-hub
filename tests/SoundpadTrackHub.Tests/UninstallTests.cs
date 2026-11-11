using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;
using Xunit;

namespace SoundpadTrackHub.Tests
{
    public class UninstallPlannerTests
    {
        private sealed class Env : IDisposable
        {
            public Env()
            {
                Root = new TempDir();
                Paths = new AppPaths(Root.Combine("Roaming"), Root.Combine("Local"), Root.Combine("Temp"), Root.Combine("Music"), Root.Combine("Local", "Programs", "SoundpadTrackHub"));
                Directory.CreateDirectory(Paths.InstalledDirectory);
                File.WriteAllText(Path.Combine(Paths.InstalledDirectory, AppPaths.ExecutableName), "exe");
                Directory.CreateDirectory(Paths.DataDirectory);
                Directory.CreateDirectory(Paths.LocalDataDirectory);
                Directory.CreateDirectory(Paths.TempDirectory);
                SoundpadDir = Root.Combine("Soundpad");
                Directory.CreateDirectory(SoundpadDir);
                File.WriteAllText(Path.Combine(SoundpadDir, "Soundpad.exe"), "soundpad");
                Desktop = Root.Combine("Desktop");
                Directory.CreateDirectory(Desktop);
                File.WriteAllText(Path.Combine(Desktop, "Soundpad Track Hub.lnk"), "lnk");
                File.WriteAllText(Path.Combine(Desktop, "Soundpad.lnk"), "soundpad shortcut");
                Music = Root.Combine("Music", "Soundpad Track Hub");
                Directory.CreateDirectory(Music);
            }

            public TempDir Root { get; }
            public AppPaths Paths { get; }
            public string SoundpadDir { get; }
            public string Desktop { get; }
            public string Music { get; }

            public UninstallPlanner Planner() => new(Paths, new[] { Desktop }, new[] { SoundpadDir });

            public string Track(string name)
            {
                var p = Path.Combine(Music, name);
                File.WriteAllText(p, "ID3");
                return p;
            }

            public void Dispose() => Root.Dispose();
        }

        [Fact]
        public void Default_Plan_Keeps_Tracks_And_User_Data()
        {
            using var env = new Env();
            var track = env.Track("a.mp3");
            var plan = env.Planner().Build(new UninstallOptions(), new[] { new DownloadRecord { FilePath = track } });

            Assert.Empty(plan.TrackFiles);
            Assert.Contains(env.Paths.InstalledDirectory, plan.PostExitDirectories);
            Assert.Contains(env.Paths.TempDirectory, plan.PostExitDirectories);
            Assert.DoesNotContain(env.Paths.DataDirectory, plan.PostExitDirectories);
            Assert.DoesNotContain(env.Paths.LocalDataDirectory, plan.PostExitDirectories);
            Assert.Equal(new[] { Path.Combine(env.Desktop, "Soundpad Track Hub.lnk") }, plan.Shortcuts);
        }

        [Fact]
        public void Never_Plans_Soundpad_Files()
        {
            using var env = new Env();
            var plan = env.Planner().Build(
                new UninstallOptions { DeleteUserData = true, DeleteDownloadedTracks = true },
                new[]
                {
                    new DownloadRecord { FilePath = Path.Combine(env.SoundpadDir, "Soundpad.exe") },
                    new DownloadRecord { FilePath = Path.Combine(env.SoundpadDir, "sound.mp3") },
                });

            var everything = plan.TrackFiles.Concat(plan.PostExitDirectories).Concat(plan.Shortcuts).ToList();
            Assert.DoesNotContain(everything, p => p.StartsWith(env.SoundpadDir, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(everything, p => p.EndsWith("Soundpad.lnk", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Deletes_Only_Tracks_Recorded_By_Track_Hub()
        {
            using var env = new Env();
            var ours = env.Track("ours.mp3");
            var boosted = env.Track("ours (громкая +6 dB).wav");
            env.Track("users-own.mp3"); // не в базе — остаётся
            var plan = env.Planner().Build(new UninstallOptions { DeleteDownloadedTracks = true },
                new[] { new DownloadRecord { FilePath = ours }, new DownloadRecord { FilePath = boosted }, new DownloadRecord { FilePath = Path.Combine(env.Music, "missing.mp3") } });

            Assert.Equal(new[] { ours, boosted }.OrderBy(x => x), plan.TrackFiles.OrderBy(x => x));
        }

        [Fact]
        public void Refuses_Install_Directory_Containing_Soundpad_Exe()
        {
            using var env = new Env();
            File.WriteAllText(Path.Combine(env.Paths.InstalledDirectory, "Soundpad.exe"), "!");
            var plan = env.Planner().Build(new UninstallOptions(), Array.Empty<DownloadRecord>());
            Assert.DoesNotContain(env.Paths.InstalledDirectory, plan.PostExitDirectories);
        }

        [Fact]
        public void Reports_Dev_Build_Folder_As_Not_Removed()
        {
            using var env = new Env();
            var devPaths = new AppPaths(env.Root.Combine("Roaming"), env.Root.Combine("Local"), env.Root.Combine("Temp"), env.Root.Combine("Music"), env.Root.Combine("src", "bin"));
            var plan = new UninstallPlanner(devPaths, new[] { env.Desktop }, new[] { env.SoundpadDir }).Build(new UninstallOptions(), Array.Empty<DownloadRecord>());
            Assert.Equal(env.Root.Combine("src", "bin"), plan.NotRemovedRunningDirectory);
            Assert.DoesNotContain(env.Root.Combine("src", "bin"), plan.PostExitDirectories);
        }

        [Fact]
        public void Cleanup_Script_Waits_For_Process_And_Escapes_Paths()
        {
            var script = UninstallService.BuildCleanupScript(1234, new[] { @"C:\Users\O'Brien\AppData\Local\Programs\SoundpadTrackHub" });
            Assert.Contains("Wait-Process -Id 1234", script);
            Assert.Contains(@"'C:\Users\O''Brien\AppData\Local\Programs\SoundpadTrackHub'", script);
            Assert.Contains("Soundpad.exe", script); // защитная проверка перед удалением папки
        }
    }

    public class UninstallDialogTests
    {
        private sealed class ScriptedDialogs : IDialogService
        {
            private readonly Queue<int> _answers;

            public ScriptedDialogs(params int[] answers) => _answers = new Queue<int>(answers);

            public List<string> Messages { get; } = new();

            public List<string[]> Buttons { get; } = new();

            public int Choose(string title, string message, string? detail, IReadOnlyList<DialogButton> buttons, DialogIcon icon = DialogIcon.Question)
            {
                Messages.Add(message);
                Buttons.Add(buttons.Select(b => b.Text).ToArray());
                return _answers.Count > 0 ? _answers.Dequeue() : -1;
            }

            public bool Confirm(string title, string message, string? detail, string confirmText, string cancelText, bool danger = false) =>
                Choose(title, message, detail, new[] { new DialogButton(confirmText), new DialogButton(cancelText) }) == 0;

            public void Info(string title, string message, string? detail = null, DialogIcon icon = DialogIcon.Info) => Messages.Add(message);
        }

        private sealed class NullAutostart : IAutostartService
        {
            public bool IsEnabled => false;

            public bool SetEnabled(bool enabled) => true;

            public void RemoveAll()
            {
            }
        }

        private static (UninstallService Service, TempDir Dir) Create(ScriptedDialogs dialogs, bool withTrack)
        {
            var dir = new TempDir();
            var paths = new AppPaths(dir.Combine("R"), dir.Combine("L"), dir.Combine("T"), dir.Combine("M"), dir.Combine("bin"));
            var history = new DownloadHistoryService(dir.Combine("downloads.json"));
            if (withTrack)
            {
                var track = dir.Combine("t.mp3");
                File.WriteAllText(track, "ID3");
                history.Add(new DownloadRecord { FilePath = track });
            }

            var planner = new UninstallPlanner(paths, Array.Empty<string>(), Array.Empty<string>());
            return (new UninstallService(dialogs, new NullAutostart(), planner, history), dir);
        }

        [Fact]
        public void Main_Question_Uses_Required_Text_And_Buttons()
        {
            var dialogs = new ScriptedDialogs(1); // «Отмена»
            var (svc, dir) = Create(dialogs, withTrack: false);
            using (dir)
            {
                Assert.Null(svc.AskOptions());
                Assert.Equal("Удалить Soundpad Track Hub? Ваши аудиотреки останутся на компьютере", dialogs.Messages[0]);
                Assert.Equal(new[] { "Удалить", "Отмена" }, dialogs.Buttons[0]);
                Assert.Single(dialogs.Messages);
            }
        }

        [Fact]
        public void Asks_Separately_About_Data_And_Tracks()
        {
            var dialogs = new ScriptedDialogs(0, 0, 0); // Удалить, оставить данные, оставить треки
            var (svc, dir) = Create(dialogs, withTrack: true);
            using (dir)
            {
                var options = svc.AskOptions();
                Assert.NotNull(options);
                Assert.False(options!.DeleteUserData);
                Assert.False(options.DeleteDownloadedTracks);
                Assert.Equal(3, dialogs.Messages.Count);
                Assert.Contains("базу", dialogs.Messages[1]);
                Assert.Contains("аудиотреки", dialogs.Messages[2]);
            }
        }

        [Fact]
        public void Success_Message_Says_Tracks_Kept()
        {
            var dialogs = new ScriptedDialogs();
            var (svc, dir) = Create(dialogs, withTrack: false);
            using (dir)
            {
                svc.ShowResult(new UninstallReport(0, 0, true, null, Array.Empty<string>()));
                Assert.Equal("Soundpad Track Hub успешно удалён. Аудиотреки сохранены", dialogs.Messages.Single());
            }
        }
    }
}
