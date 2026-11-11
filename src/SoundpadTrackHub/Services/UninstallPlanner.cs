using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services
{
    public sealed class UninstallOptions
    {
        /// <summary>Удалить базу загрузок, настройки, журналы и профиль WebView2.</summary>
        public bool DeleteUserData { get; init; }

        /// <summary>Удалить треки, скачанные/созданные Track Hub (по базе загрузок).</summary>
        public bool DeleteDownloadedTracks { get; init; }
    }

    public sealed class UninstallPlan
    {
        public List<string> Shortcuts { get; } = new();

        public List<string> TrackFiles { get; } = new();

        /// <summary>Папки, которые удаляются после выхода процесса.</summary>
        public List<string> PostExitDirectories { get; } = new();

        /// <summary>Пути, которые не будут удалены, потому что относятся к Soundpad.</summary>
        public List<string> ProtectedSkipped { get; } = new();

        /// <summary>Приложение запущено не из папки установки (например, из bin\Release).</summary>
        public string? NotRemovedRunningDirectory { get; set; }
    }

    /// <summary>
    /// Чистая логика «что удалять». Отдельно от исполнения, чтобы её можно было
    /// проверить тестами: Soundpad.exe и папки Soundpad не попадают в план никогда.
    /// </summary>
    public sealed class UninstallPlanner
    {
        public static readonly string[] ShortcutNames = { "Soundpad Track Hub.lnk", "Soundpad + Track Hub.lnk" };

        private static readonly HashSet<string> TrackExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav" };

        private readonly AppPaths _paths;
        private readonly IReadOnlyList<string> _shortcutFolders;
        private readonly IReadOnlyList<string> _soundpadDirectories;
        private readonly Func<string, bool> _fileExists;
        private readonly Func<string, bool> _dirExists;

        public UninstallPlanner(
            AppPaths paths,
            IReadOnlyList<string> shortcutFolders,
            IReadOnlyList<string> soundpadDirectories,
            Func<string, bool>? fileExists = null,
            Func<string, bool>? dirExists = null)
        {
            _paths = paths;
            _shortcutFolders = shortcutFolders;
            _soundpadDirectories = soundpadDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            _fileExists = fileExists ?? File.Exists;
            _dirExists = dirExists ?? Directory.Exists;
        }

        public IReadOnlyList<string> GetDeletableTracks(IEnumerable<DownloadRecord> history)
        {
            var result = new List<string>();
            foreach (var record in history)
            {
                var path = record.FilePath;
                if (string.IsNullOrWhiteSpace(path) || !_fileExists(path))
                {
                    continue;
                }

                if (!TrackExtensions.Contains(Path.GetExtension(path)) || IsSoundpadPath(path))
                {
                    continue;
                }

                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(path);
                }
            }

            return result;
        }

        public UninstallPlan Build(UninstallOptions options, IEnumerable<DownloadRecord> history)
        {
            var plan = new UninstallPlan();

            foreach (var folder in _shortcutFolders.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                foreach (var name in ShortcutNames)
                {
                    var lnk = Path.Combine(folder, name);
                    if (_fileExists(lnk))
                    {
                        plan.Shortcuts.Add(lnk);
                    }
                }
            }

            if (options.DeleteDownloadedTracks)
            {
                plan.TrackFiles.AddRange(GetDeletableTracks(history));
            }

            // Папка установки — только если это действительно папка Track Hub.
            if (IsTrackHubInstallDirectory(_paths.InstalledDirectory))
            {
                AddDirectory(plan, _paths.InstalledDirectory);
            }

            if (!SamePath(_paths.BaseDirectory, _paths.InstalledDirectory))
            {
                plan.NotRemovedRunningDirectory = _paths.BaseDirectory;
            }

            AddDirectory(plan, _paths.TempDirectory);

            if (options.DeleteUserData)
            {
                AddDirectory(plan, _paths.DataDirectory);
                AddDirectory(plan, _paths.LocalDataDirectory);
            }

            return plan;
        }

        public bool IsTrackHubInstallDirectory(string dir) =>
            _dirExists(dir)
            && _fileExists(Path.Combine(dir, AppPaths.ExecutableName))
            && !_fileExists(Path.Combine(dir, "Soundpad.exe"))
            && !IsSoundpadPath(dir);

        public bool IsSoundpadPath(string path)
        {
            if (string.Equals(Path.GetFileName(path), "Soundpad.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var spDir in _soundpadDirectories)
            {
                if (SamePath(path, spDir) || TargetPathResolver.IsInsideFolder(path, spDir) || TargetPathResolver.IsInsideFolder(spDir, path))
                {
                    return true;
                }
            }

            return false;
        }

        private void AddDirectory(UninstallPlan plan, string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !_dirExists(dir))
            {
                return;
            }

            if (IsSoundpadPath(dir) || _fileExists(Path.Combine(dir, "Soundpad.exe")))
            {
                plan.ProtectedSkipped.Add(dir);
                return;
            }

            plan.PostExitDirectories.Add(dir);
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\', '/'),
                    Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
