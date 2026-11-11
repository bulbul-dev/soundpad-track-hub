using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services
{
    public interface ISettingsService
    {
        AppSettings Current { get; }

        event EventHandler? Changed;

        void Update(Action<AppSettings> change);

        void Save();
    }

    /// <summary>
    /// Настройки в JSON. Запись атомарная (временный файл + замена), испорченный
    /// файл не роняет приложение — используются значения по умолчанию.
    /// </summary>
    public sealed class JsonSettingsService : ISettingsService
    {
        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _path;
        private readonly string? _downloadsMarkerPath;
        private readonly string _defaultDownloadFolder;
        private readonly ILogger _log = Log.ForContext<JsonSettingsService>();
        private readonly object _gate = new();

        public JsonSettingsService(string path, string defaultDownloadFolder, string? downloadsMarkerPath = null)
        {
            _path = path;
            _defaultDownloadFolder = defaultDownloadFolder;
            _downloadsMarkerPath = downloadsMarkerPath;
            Current = Load();
        }

        public AppSettings Current { get; private set; }

        public event EventHandler? Changed;

        public void Update(Action<AppSettings> change)
        {
            lock (_gate)
            {
                var copy = Current.Clone();
                change(copy);
                copy.Normalize(_defaultDownloadFolder);
                Current = copy;
            }

            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Save()
        {
            AppSettings snapshot;
            lock (_gate)
            {
                snapshot = Current.Clone();
            }

            try
            {
                var dir = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);

                // Маркер для автономного удаления: где лежат загрузки.
                if (_downloadsMarkerPath is not null)
                {
                    File.WriteAllText(_downloadsMarkerPath, snapshot.DownloadFolder, new UTF8Encoding(false));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(ex, "Failed to save settings to {Path}", _path);
            }
        }

        private AppSettings Load()
        {
            AppSettings settings;
            try
            {
                settings = File.Exists(_path)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _log.Warning(ex, "Settings file is unreadable, defaults are used");
                TryBackupBrokenFile();
                settings = new AppSettings();
            }

            settings.Normalize(_defaultDownloadFolder);
            return settings;
        }

        private void TryBackupBrokenFile()
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Copy(_path, _path + ".broken", overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug(ex, "Failed to back up broken settings");
            }
        }
    }
}
