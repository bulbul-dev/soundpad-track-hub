using System;

namespace SoundpadTrackHub.Models
{
    /// <summary>
    /// Локальные настройки Track Hub (%APPDATA%\SoundpadTrackHub\settings.json).
    /// Автозапуск хранится не здесь, а в HKCU\...\Run — источник истины там.
    /// </summary>
    public sealed class AppSettings
    {
        public const double DefaultWidth = 460;
        public const double DefaultHeight = 720;
        public const double MinWidth = 400;
        public const double MaxWidth = 640;
        public const double MinHeight = 480;
        public const string DefaultCategoryName = "Soundpad Track Hub";

        public int SchemaVersion { get; set; } = 2;

        /// <summary>Папка, куда сохраняются подтверждённые MP3.</summary>
        public string DownloadFolder { get; set; } = string.Empty;

        /// <summary>Режим громкости. По умолчанию — «Нормальная».</summary>
        public VolumeMode VolumeMode { get; set; } = VolumeMode.Normal;

        /// <summary>Громкость предпрослушивания 0..1. Не влияет на Windows и Soundpad.</summary>
        public double PreviewVolume { get; set; } = 0.8;

        /// <summary>Прикреплять окно к Soundpad (справа, если нет места — слева).</summary>
        public bool DockToSoundpad { get; set; } = true;

        /// <summary>Скрывать Track Hub, когда Soundpad закрыт.</summary>
        public bool HideWhenSoundpadClosed { get; set; } = true;

        /// <summary>Добавлять скачанные треки в отдельную категорию Soundpad.</summary>
        public bool AddToSoundpadCategory { get; set; } = true;

        /// <summary>Имя категории в списке звуков Soundpad.</summary>
        public string SoundpadCategoryName { get; set; } = DefaultCategoryName;

        /// <summary>Профиль громкости, который применится, когда Soundpad будет закрыт.</summary>
        public SoundpadVolumeProfile? PendingSoundpadProfile { get; set; }

        /// <summary>Какую редакцию пользовательского соглашения принял пользователь (null — ещё не принимал).</summary>
        public string? AcceptedAgreementVersion { get; set; }

        /// <summary>Текущая редакция соглашения. При изменении текста — поменять, и соглашение покажется снова.</summary>
        public const string AgreementVersion = "2026-11-13";

        public double WindowWidth { get; set; } = DefaultWidth;
        public double WindowHeight { get; set; } = DefaultHeight;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }

        public AppSettings Clone() => (AppSettings)MemberwiseClone();

        /// <summary>Приводит значения к допустимым диапазонам (защита от испорченного файла).</summary>
        public void Normalize(string defaultDownloadFolder)
        {
            if (string.IsNullOrWhiteSpace(DownloadFolder))
            {
                DownloadFolder = defaultDownloadFolder;
            }

            if (!Enum.IsDefined(typeof(VolumeMode), VolumeMode))
            {
                VolumeMode = VolumeMode.Normal;
            }

            if (string.IsNullOrWhiteSpace(SoundpadCategoryName) || SoundpadCategoryName.Contains('"'))
            {
                SoundpadCategoryName = DefaultCategoryName;
            }

            PreviewVolume = double.IsFinite(PreviewVolume) ? Math.Clamp(PreviewVolume, 0.0, 1.0) : 0.8;
            WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, MinWidth, MaxWidth) : DefaultWidth;
            WindowHeight = double.IsFinite(WindowHeight) ? Math.Max(WindowHeight, MinHeight) : DefaultHeight;

            if (WindowLeft is { } l && !double.IsFinite(l)) WindowLeft = null;
            if (WindowTop is { } t && !double.IsFinite(t)) WindowTop = null;
        }
    }
}
