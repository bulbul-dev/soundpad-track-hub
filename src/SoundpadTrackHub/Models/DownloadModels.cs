using System;

namespace SoundpadTrackHub.Models
{
    /// <summary>
    /// Описание загрузки, которую WebView2 собирается начать.
    /// </summary>
    public sealed record DownloadRequest
    {
        /// <summary>URL загрузки, который сообщил WebView2 (может быть после перенаправления).</summary>
        public required string DownloadUri { get; init; }

        /// <summary>Имя файла, предложенное браузером (Content-Disposition или URL).</summary>
        public string? SuggestedFileName { get; init; }

        /// <summary>MIME-тип ответа.</summary>
        public string? MimeType { get; init; }

        /// <summary>
        /// Ссылка, по которой пользователь кликнул в основном фрейме (первая навигация
        /// цепочки до перенаправлений). Нужна, потому что Hitmoz отдаёт MP3 через
        /// перенаправление со своей ссылки /get/music/...mp3.
        /// </summary>
        public string? SourceUri { get; init; }

        /// <summary>Когда началась навигация по SourceUri (UTC).</summary>
        public DateTime? SourceStartedUtc { get; init; }

        public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed record DownloadPolicyResult(bool IsAllowed, string Reason, string SafeFileName, string DownloadHost)
    {
        public static DownloadPolicyResult Block(string reason, string fileName = "", string host = "") =>
            new(false, reason, fileName, host);
    }

    /// <summary>Что делать, если файл с таким именем уже есть в папке загрузок.</summary>
    public enum ExistingFileDecision
    {
        Cancel,
        Overwrite,
        KeepBoth,
    }

    /// <summary>Запись в локальной базе загрузок (%APPDATA%\SoundpadTrackHub\downloads.json).</summary>
    public sealed class DownloadRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public DateTime DownloadedAtUtc { get; set; } = DateTime.UtcNow;
        public long SizeBytes { get; set; }

        /// <summary>true — это усиленная копия, созданная Track Hub (оригинал не изменялся).</summary>
        public bool IsBoostedCopy { get; set; }

        public VolumeMode? BoostMode { get; set; }

        /// <summary>Трек уже добавлен в категорию Track Hub в Soundpad.</summary>
        public bool AddedToSoundpad { get; set; }
        public string? OriginalPath { get; set; }
    }
}
