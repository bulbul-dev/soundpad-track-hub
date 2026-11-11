using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.Integration
{
    public sealed record SoundpadSyncResult(bool Success, int Added, string Message);

    /// <summary>
    /// Складывает скачанные треки в категорию «Soundpad Track Hub» в списке звуков Soundpad
    /// через его штатный интерфейс удалённого управления. Файлы Soundpad не изменяются.
    /// Если Soundpad закрыт — треки добавятся при следующем подключении.
    /// </summary>
    public sealed class SoundpadLibrarySync
    {
        private readonly SoundpadRemoteClient _client;
        private readonly DownloadHistoryService _history;
        private readonly ISettingsService _settings;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ILogger _log = Log.ForContext<SoundpadLibrarySync>();

        public SoundpadLibrarySync(SoundpadRemoteClient client, DownloadHistoryService history, ISettingsService settings)
        {
            _client = client;
            _history = history;
            _settings = settings;
        }

        public string CategoryName => _settings.Current.SoundpadCategoryName;

        /// <summary>Добавляет в категорию все скачанные треки, которых там ещё нет.</summary>
        public async Task<SoundpadSyncResult> SyncPendingAsync(CancellationToken ct = default)
        {
            if (!_settings.Current.AddToSoundpadCategory)
            {
                return new SoundpadSyncResult(true, 0, "Добавление в Soundpad выключено в настройках.");
            }

            var pending = _history.GetAll()
                .Where(r => !r.AddedToSoundpad && File.Exists(r.FilePath))
                .OrderBy(r => r.DownloadedAtUtc)
                .ToList();
            if (pending.Count == 0)
            {
                return new SoundpadSyncResult(true, 0, "Все треки уже в Soundpad.");
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!await _client.IsAvailableAsync(ct).ConfigureAwait(false))
                {
                    return new SoundpadSyncResult(false, 0, "Soundpad не запущен — треки добавятся, когда он откроется.");
                }

                var category = await _client.EnsureCategoryAsync(CategoryName, ct).ConfigureAwait(false);
                var added = 0;
                string? lastError = null;
                foreach (var record in pending)
                {
                    try
                    {
                        if (await _client.AddSoundToCategoryAsync(record.FilePath, category, ct).ConfigureAwait(false))
                        {
                            added++;
                        }

                        _history.MarkAddedToSoundpad(record.Id);
                    }
                    catch (SoundpadRemoteException ex)
                    {
                        lastError = ex.Message;
                        _log.Warning("Soundpad refused {File}: {Error}", record.FileName, ex.Message);
                    }
                }

                _log.Information("Soundpad sync: added {Added} of {Pending} to category {Category}", added, pending.Count, CategoryName);
                return lastError is null
                    ? new SoundpadSyncResult(true, added, added == 1
                        ? $"Трек добавлен в Soundpad → «{CategoryName}»."
                        : $"Добавлено в Soundpad → «{CategoryName}»: {added}.")
                    : new SoundpadSyncResult(false, added, lastError);
            }
            catch (SoundpadRemoteException ex)
            {
                _log.Warning("Soundpad sync failed: {Error}", ex.Message);
                return new SoundpadSyncResult(false, 0, ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
