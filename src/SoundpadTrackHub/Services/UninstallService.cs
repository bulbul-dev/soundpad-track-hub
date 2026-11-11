using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using Serilog;
using SoundpadTrackHub.Infrastructure;

namespace SoundpadTrackHub.Services
{
    public sealed record UninstallReport(int TracksDeleted, int TracksFailed, bool TracksKept, string? NotRemovedDirectory, IReadOnlyList<string> ProtectedSkipped);

    /// <summary>
    /// Удаление Soundpad Track Hub. Не трогает Soundpad.exe, файлы и реестр Soundpad.
    /// </summary>
    public sealed class UninstallService
    {
        public const string MainQuestion = "Удалить Soundpad Track Hub? Ваши аудиотреки останутся на компьютере";
        public const string SuccessKeptTracks = "Soundpad Track Hub успешно удалён. Аудиотреки сохранены";
        public const string Title = "Удаление Soundpad Track Hub";

        private const string LegacyAppRegistryKey = @"Software\SoundpadTrackHub";
        public const string AppsAndFeaturesKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundpadTrackHub";

        /// <summary>Запись, которую создаёт установщик Inno Setup (AppId + "_is1").</summary>
        public const string InstallerAppsAndFeaturesKey = AppsAndFeaturesKey + "_is1";

        private readonly IDialogService _dialogs;
        private readonly IAutostartService _autostart;
        private readonly UninstallPlanner _planner;
        private readonly DownloadHistoryService _history;
        private readonly ILogger _log = Log.ForContext<UninstallService>();

        public UninstallService(IDialogService dialogs, IAutostartService autostart, UninstallPlanner planner, DownloadHistoryService history)
        {
            _dialogs = dialogs;
            _autostart = autostart;
            _planner = planner;
            _history = history;
        }

        public static IReadOnlyList<string> DefaultShortcutFolders() => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        };

        /// <summary>Диалоги подтверждения. null — пользователь отменил удаление.</summary>
        public UninstallOptions? AskOptions()
        {
            var confirmed = _dialogs.Confirm(Title, MainQuestion, null, "Удалить", "Отмена", danger: true);
            if (!confirmed)
            {
                _log.Information("Uninstall cancelled at main confirmation");
                return null;
            }

            var dataChoice = _dialogs.Choose(
                Title,
                "Удалить также базу загрузок и настройки?",
                "История загрузок, настройки, журналы и данные сайта во встроенном браузере (cookies). Аудиофайлы этим пунктом не удаляются.",
                new[] { new DialogButton("Оставить", DialogButtonKind.Primary), new DialogButton("Удалить данные", DialogButtonKind.Danger) },
                DialogIcon.Question);

            var tracks = _planner.GetDeletableTracks(_history.GetAll());
            var deleteTracks = false;
            if (tracks.Count > 0)
            {
                var trackChoice = _dialogs.Choose(
                    Title,
                    "Удалить также скачанные аудиотреки?",
                    $"Будут удалены только файлы, скачанные или созданные Track Hub: {tracks.Count} шт. Остальные файлы в папке не затрагиваются. Это действие необратимо.",
                    new[] { new DialogButton("Оставить треки", DialogButtonKind.Primary), new DialogButton("Удалить треки", DialogButtonKind.Danger) },
                    DialogIcon.Warning);
                deleteTracks = trackChoice == 1;
            }

            return new UninstallOptions { DeleteUserData = dataChoice == 1, DeleteDownloadedTracks = deleteTracks };
        }

        /// <summary>
        /// Выполняет удаление внутри процесса и планирует удаление папок после выхода.
        /// Вызывающий код должен сразу после этого завершить приложение.
        /// </summary>
        public UninstallReport Execute(UninstallOptions options)
        {
            var plan = _planner.Build(options, _history.GetAll());
            _log.Information("Uninstall: shortcuts={Shortcuts} tracks={Tracks} dirs={Dirs} protected={Protected}",
                plan.Shortcuts.Count, plan.TrackFiles.Count, plan.PostExitDirectories.Count, plan.ProtectedSkipped.Count);

            _autostart.RemoveAll();

            foreach (var lnk in plan.Shortcuts)
            {
                TryDeleteFile(lnk);
            }

            var deleted = 0;
            var failed = 0;
            foreach (var track in plan.TrackFiles)
            {
                if (TryDeleteFile(track)) deleted++;
                else failed++;
            }

            // Ключ прошлой версии Track Hub и запись в «Установка и удаление программ» (install.ps1).
            // Ключи Soundpad (HKCU\Software\Leppsoft) не трогаем.
            foreach (var key in new[] { LegacyAppRegistryKey, AppsAndFeaturesKey, InstallerAppsAndFeaturesKey })
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to remove registry key {Key}", key);
                }
            }

            ScheduleDirectoryRemoval(plan.PostExitDirectories);

            return new UninstallReport(deleted, failed, !options.DeleteDownloadedTracks, plan.NotRemovedRunningDirectory, plan.ProtectedSkipped);
        }

        public void ShowResult(UninstallReport report)
        {
            var message = report.TracksKept
                ? SuccessKeptTracks
                : "Soundpad Track Hub успешно удалён. Скачанные треки удалены по вашему выбору";

            var details = new List<string>();
            if (!report.TracksKept && report.TracksFailed > 0)
            {
                details.Add($"Не удалось удалить файлов: {report.TracksFailed} (возможно, они открыты в Soundpad).");
            }

            if (report.NotRemovedDirectory is not null)
            {
                details.Add($"Track Hub запущен не из папки установки, поэтому папка «{report.NotRemovedDirectory}» не удалена — удалите её вручную, если она не нужна.");
            }

            details.Add("Soundpad и его файлы не изменялись.");
            _dialogs.Info(Title, message, string.Join(Environment.NewLine, details), DialogIcon.Success);
        }

        /// <summary>PowerShell-скрипт, который дожидается выхода процесса и удаляет папки Track Hub.</summary>
        public static string BuildCleanupScript(int processId, IEnumerable<string> directories)
        {
            static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine($"try {{ Wait-Process -Id {processId} -Timeout 120 }} catch {{ }}");
            sb.AppendLine("Start-Sleep -Milliseconds 1500");
            sb.AppendLine("$targets = @(" + string.Join(", ", directories.Select(Quote)) + ")");
            sb.AppendLine("foreach ($t in $targets) {");
            sb.AppendLine("  for ($i = 0; $i -lt 20; $i++) {");
            sb.AppendLine("    if (-not (Test-Path -LiteralPath $t)) { break }");
            sb.AppendLine("    if (Test-Path -LiteralPath (Join-Path $t 'Soundpad.exe')) { break }");
            sb.AppendLine("    Remove-Item -LiteralPath $t -Recurse -Force");
            sb.AppendLine("    if (-not (Test-Path -LiteralPath $t)) { break }");
            sb.AppendLine("    Start-Sleep -Milliseconds 700");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
            return sb.ToString();
        }

        private void ScheduleDirectoryRemoval(IReadOnlyCollection<string> directories)
        {
            if (directories.Count == 0)
            {
                return;
            }

            try
            {
                var script = Path.Combine(Path.GetTempPath(), $"SoundpadTrackHub-cleanup-{Guid.NewGuid():N}.ps1");
                // UTF-8 с BOM: Windows PowerShell 5.1 корректно читает кириллицу в путях.
                File.WriteAllText(script, BuildCleanupScript(Environment.ProcessId, directories), new UTF8Encoding(true));

                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
                {
                    psi.ArgumentList.Add(arg);
                }

                using var _ = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to schedule post-exit cleanup");
            }
        }

        private bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "Failed to delete {Path}", path);
                return false;
            }
        }
    }
}
