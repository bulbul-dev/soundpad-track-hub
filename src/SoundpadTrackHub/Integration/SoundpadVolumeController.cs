using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.Integration
{
    public sealed record VolumeApplyResult(bool Applied, string Message);

    /// <summary>
    /// Применяет профили «Нормально / Громко / Громко 2 / Супер громко» к настройке
    /// Soundpad «Нормализация громкости». Soundpad перечитывает её только при запуске
    /// и перезаписывает при выходе, поэтому:
    ///  • Soundpad закрыт — значения пишутся сразу;
    ///  • Soundpad запущен — с подтверждения он штатно закрывается (WM_CLOSE), значения пишутся,
    ///    Soundpad запускается снова; либо профиль применяется, когда пользователь сам закроет Soundpad.
    /// </summary>
    public sealed class SoundpadVolumeController
    {
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(15);

        private readonly ISoundpadSettingsStore _store;
        private readonly SoundpadLocator _locator;
        private readonly SoundpadWatcher _watcher;
        private readonly ISettingsService _settings;
        private readonly IDialogService _dialogs;
        private readonly ILogger _log = Log.ForContext<SoundpadVolumeController>();

        public SoundpadVolumeController(ISoundpadSettingsStore store, SoundpadLocator locator, SoundpadWatcher watcher, ISettingsService settings, IDialogService dialogs)
        {
            _store = store;
            _locator = locator;
            _watcher = watcher;
            _settings = settings;
            _dialogs = dialogs;
            _watcher.StateChanged += OnSoundpadStateChanged;
        }

        /// <summary>Soundpad перезапускается нами — окно Track Hub прятать не нужно.</summary>
        public bool IsRestartingSoundpad { get; private set; }

        public event EventHandler? Changed;

        public SoundpadNormalization? ReadCurrent()
        {
            try
            {
                return _store.Read();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Cannot read Soundpad normalization settings");
                return null;
            }
        }

        public SoundpadVolumeProfile? CurrentProfile =>
            ReadCurrent() is { } current ? SoundpadVolumeProfileExtensions.Match(current) : null;

        public SoundpadVolumeProfile? PendingProfile => _settings.Current.PendingSoundpadProfile;

        public async Task<VolumeApplyResult> ApplyAsync(SoundpadVolumeProfile profile)
        {
            var target = profile.Settings();
            using var process = _locator.FindProcess();
            if (process is null)
            {
                return Write(profile, "Soundpad применит их при следующем запуске.");
            }

            var choice = _dialogs.Choose(
                "Громкость Soundpad",
                $"Применить режим «{profile.DisplayName()}»? Soundpad нужно перезапустить.",
                $"Нормализация громкости: {target.Describe()}.\n" +
                "Soundpad читает эту настройку только при запуске. Track Hub закроет Soundpad обычным способом, " +
                "запишет настройку и сразу запустит его снова. Текущее воспроизведение прервётся.",
                new[]
                {
                    new DialogButton("Перезапустить Soundpad", DialogButtonKind.Primary),
                    new DialogButton("Когда закрою сам"),
                    new DialogButton("Отмена"),
                },
                DialogIcon.Question);

            if (choice == 1)
            {
                _settings.Update(s => s.PendingSoundpadProfile = profile);
                Changed?.Invoke(this, EventArgs.Empty);
                return new VolumeApplyResult(false, $"Режим «{profile.DisplayName()}» применится, когда вы закроете Soundpad.");
            }

            if (choice != 0)
            {
                return new VolumeApplyResult(false, "Громкость не изменена.");
            }

            string? exePath = null;
            try
            {
                exePath = process.MainModule?.FileName;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Cannot read Soundpad path");
            }

            var hwnd = _locator.FindMainWindow(process.Id);
            if (hwnd == IntPtr.Zero || exePath is null)
            {
                _settings.Update(s => s.PendingSoundpadProfile = profile);
                return new VolumeApplyResult(false, "Не удалось перезапустить Soundpad автоматически. Закройте его сами — режим применится после закрытия.");
            }

            IsRestartingSoundpad = true;
            try
            {
                _log.Information("Restarting Soundpad to apply volume profile {Profile}", profile);
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                var exited = await Task.Run(() => process.WaitForExit((int)CloseTimeout.TotalMilliseconds));
                if (!exited)
                {
                    _settings.Update(s => s.PendingSoundpadProfile = profile);
                    return new VolumeApplyResult(false, "Soundpad не закрылся (возможно, открыт диалог). Режим применится, когда Soundpad будет закрыт.");
                }

                await Task.Delay(500);
                var result = Write(profile, "Soundpad перезапущен.");
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath)! })?.Dispose();
                return result;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Soundpad restart failed");
                return new VolumeApplyResult(false, "Ошибка перезапуска Soundpad: " + ex.Message);
            }
            finally
            {
                // Даём Soundpad время подняться, чтобы Track Hub не спрятался между закрытием и запуском.
                _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => IsRestartingSoundpad = false);
            }
        }

        private VolumeApplyResult Write(SoundpadVolumeProfile profile, string suffix)
        {
            try
            {
                _store.Write(profile.Settings());
                _settings.Update(s => s.PendingSoundpadProfile = null);
                _log.Information("Soundpad normalization set to {Profile}", profile);
                Changed?.Invoke(this, EventArgs.Empty);
                return new VolumeApplyResult(true, $"Громкость Soundpad: «{profile.DisplayName()}» ({profile.Settings().Describe()}). {suffix}");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to write Soundpad settings");
                return new VolumeApplyResult(false, "Не удалось записать настройку Soundpad: " + ex.Message);
            }
        }

        private void OnSoundpadStateChanged(object? sender, SoundpadConnectionState state)
        {
            if (state != SoundpadConnectionState.Closed || _settings.Current.PendingSoundpadProfile is not { } pending)
            {
                return;
            }

            // Soundpad уже записал свои настройки при выходе — теперь наши значения не будут перезаписаны.
            Write(pending, "Применено после закрытия Soundpad.");
        }
    }
}
