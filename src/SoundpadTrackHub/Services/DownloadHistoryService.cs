using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Serilog;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services
{
    /// <summary>
    /// Локальная «база» загрузок: какие файлы скачал/создал Track Hub.
    /// Используется для списка загрузок и чтобы при удалении трогать только свои файлы.
    /// </summary>
    public sealed class DownloadHistoryService
    {
        private const int MaxRecords = 500;

        private readonly string _path;
        private readonly object _gate = new();
        private readonly ILogger _log = Log.ForContext<DownloadHistoryService>();
        private List<DownloadRecord> _records;

        public DownloadHistoryService(string path)
        {
            _path = path;
            _records = Load();
        }

        public event EventHandler? Changed;

        public IReadOnlyList<DownloadRecord> GetAll()
        {
            lock (_gate)
            {
                return _records.OrderByDescending(r => r.DownloadedAtUtc).ToList();
            }
        }

        public void Add(DownloadRecord record)
        {
            lock (_gate)
            {
                // Перезапись файла — одна запись на путь.
                _records.RemoveAll(r => string.Equals(r.FilePath, record.FilePath, StringComparison.OrdinalIgnoreCase));
                _records.Add(record);
                if (_records.Count > MaxRecords)
                {
                    _records = _records.OrderByDescending(r => r.DownloadedAtUtc).Take(MaxRecords).ToList();
                }
            }

            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void MarkAddedToSoundpad(string id)
        {
            lock (_gate)
            {
                var r = _records.FirstOrDefault(x => x.Id == id);
                if (r is null || r.AddedToSoundpad)
                {
                    return;
                }

                r.AddedToSoundpad = true;
            }

            Save();
        }

        public void Remove(string id)
        {
            lock (_gate)
            {
                _records.RemoveAll(r => r.Id == id);
            }

            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private List<DownloadRecord> Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new List<DownloadRecord>();
                }

                return JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(_path), JsonSettingsService.JsonOptions)
                       ?? new List<DownloadRecord>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _log.Warning(ex, "Download history is unreadable, starting empty");
                return new List<DownloadRecord>();
            }
        }

        private void Save()
        {
            List<DownloadRecord> snapshot;
            lock (_gate)
            {
                snapshot = _records.ToList();
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonSettingsService.JsonOptions), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(ex, "Failed to save download history");
            }
        }
    }
}
