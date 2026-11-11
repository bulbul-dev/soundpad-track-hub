using System;
using System.IO;
using Serilog;

namespace SoundpadTrackHub.Services
{
    /// <summary>
    /// Временные файлы загрузок живут только в %TEMP%\SoundpadTrackHub.
    /// Удаляются при отмене, ошибке, после переноса в папку загрузок и при выходе.
    /// </summary>
    public sealed class TempFileManager
    {
        private readonly string _downloadsDir;
        private readonly string _renderDir;
        private readonly ILogger _log = Log.ForContext<TempFileManager>();

        public TempFileManager(string downloadsDir, string renderDir)
        {
            _downloadsDir = downloadsDir;
            _renderDir = renderDir;
        }

        public string DownloadsDirectory => _downloadsDir;

        public string CreateDownloadPath()
        {
            Directory.CreateDirectory(_downloadsDir);
            return Path.Combine(_downloadsDir, Guid.NewGuid().ToString("N") + ".mp3");
        }

        public string CreateRenderPath(string extension)
        {
            Directory.CreateDirectory(_renderDir);
            return Path.Combine(_renderDir, Guid.NewGuid().ToString("N") + extension);
        }

        /// <summary>Удаляет файл и частичные загрузки Chromium (*.crdownload) с тем же именем.</summary>
        public void Delete(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            TryDeleteFile(path);
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            if (dir is null || !Directory.Exists(dir) || string.IsNullOrEmpty(name))
            {
                return;
            }

            try
            {
                foreach (var partial in Directory.EnumerateFiles(dir, name + "*"))
                {
                    TryDeleteFile(partial);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug(ex, "Partial cleanup failed for {Path}", path);
            }
        }

        /// <summary>Полная очистка временных папок (при старте и выходе).</summary>
        public int CleanupAll()
        {
            return CleanDirectory(_downloadsDir) + CleanDirectory(_renderDir);
        }

        private int CleanDirectory(string dir)
        {
            var count = 0;
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (TryDeleteFile(file))
                    {
                        count++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "Temp cleanup failed for {Dir}", dir);
            }

            return count;
        }

        private bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug(ex, "Failed to delete temp file {Path}", path);
            }

            return false;
        }
    }
}
