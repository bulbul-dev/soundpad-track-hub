using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using Serilog;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.Integration
{
    public sealed record ReplaceResult(bool Success, string Message);

    /// <summary>
    /// Замена трека в Soundpad (подходит для бесплатной версии): если Soundpad запущен,
    /// он штатно закрывается, в soundlist.spl меняется файл выбранного звука, Soundpad запускается снова.
    /// </summary>
    public sealed class SoundpadTrackReplacer
    {
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(15);

        private readonly SoundpadSoundList _list;
        private readonly SoundpadLocator _locator;
        private readonly IDialogService _dialogs;
        private readonly ILogger _log = Log.ForContext<SoundpadTrackReplacer>();

        public SoundpadTrackReplacer(SoundpadSoundList list, SoundpadLocator locator, IDialogService dialogs)
        {
            _list = list;
            _locator = locator;
            _dialogs = dialogs;
        }

        /// <summary>Soundpad перезапускается нами — окно Track Hub прятать не нужно.</summary>
        public bool IsRestartingSoundpad { get; private set; }

        public SoundpadSoundList List => _list;

        public static SoundpadTrackInfo DescribeTrack(string filePath)
        {
            TimeSpan? length = null;
            try
            {
                using var reader = new AudioFileReader(filePath);
                length = reader.TotalTime;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cannot read track length {File}", filePath);
            }

            return SoundpadTrackInfo.FromFileName(filePath, length);
        }

        public async Task<ReplaceResult> ReplaceAsync(SoundpadSound target, string newFile)
        {
            if (!_list.Exists)
            {
                return new ReplaceResult(false, "Не найден список звуков Soundpad (soundlist.spl). Запустите Soundpad хотя бы один раз.");
            }

            if (!File.Exists(newFile))
            {
                return new ReplaceResult(false, "Файл трека не найден — возможно, он перемещён или удалён.");
            }

            var track = DescribeTrack(newFile);
            var newName = string.IsNullOrEmpty(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}";
            using var process = _locator.FindProcess();

            var detail = $"Звук №{target.Index} «{target.DisplayName}» ({target.CategoryText}) будет играть новый трек «{newName}». " +
                         "Номер, категория и горячая клавиша останутся. Старый файл не удаляется, резервная копия списка сохраняется.";
            if (process is not null)
            {
                detail += "\n\nSoundpad сейчас открыт: Track Hub закроет его обычным способом, заменит трек и сразу запустит снова.";
            }

            var ok = _dialogs.Confirm("Заменить трек в Soundpad",
                $"Заменить «{target.DisplayName}» на «{newName}»?", detail,
                process is null ? "Заменить" : "Перезапустить и заменить", "Отмена");
            if (!ok)
            {
                return new ReplaceResult(false, "Замена отменена.");
            }

            if (process is null)
            {
                return Write(target, track, newName);
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
                return new ReplaceResult(false, "Не удалось закрыть Soundpad автоматически. Закройте Soundpad сами и нажмите «Поменять» ещё раз.");
            }

            IsRestartingSoundpad = true;
            try
            {
                _log.Information("Restarting Soundpad to replace sound {Index}", target.Index);
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                var exited = await Task.Run(() => process.WaitForExit((int)CloseTimeout.TotalMilliseconds));
                if (!exited)
                {
                    return new ReplaceResult(false, "Soundpad не закрылся (возможно, открыт диалог). Закройте его и повторите.");
                }

                await Task.Delay(500);
                var result = Write(target, track, newName);
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath)! })?.Dispose();
                return result;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Soundpad restart for replace failed");
                return new ReplaceResult(false, "Ошибка перезапуска Soundpad: " + ex.Message);
            }
            finally
            {
                _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => IsRestartingSoundpad = false);
            }
        }

        private ReplaceResult Write(SoundpadSound target, SoundpadTrackInfo track, string newName)
        {
            try
            {
                var backup = _list.Replace(target.Index, track, target.Url);
                _log.Information("Replaced Soundpad sound {Index} with {File}, backup {Backup}", target.Index, track.FilePath, backup);
                return new ReplaceResult(true, $"Готово: звук №{target.Index} в Soundpad теперь «{newName}».");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Soundpad sound replace failed");
                return new ReplaceResult(false, "Не удалось заменить трек: " + ex.Message);
            }
        }
    }
}
