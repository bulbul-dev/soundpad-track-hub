using System;
using System.IO;

namespace SoundpadTrackHub.Infrastructure
{
    /// <summary>
    /// Все пути, которыми владеет Track Hub. Пути Soundpad здесь отсутствуют намеренно.
    /// </summary>
    public sealed class AppPaths
    {
        public const string AppFolderName = "SoundpadTrackHub";
        public const string ExecutableName = "SoundpadTrackHub.exe";

        public AppPaths(string roamingAppData, string localAppData, string tempRoot, string musicFolder, string baseDirectory)
        {
            DataDirectory = Path.Combine(roamingAppData, AppFolderName);
            LocalDataDirectory = Path.Combine(localAppData, AppFolderName);
            InstalledDirectory = Path.Combine(localAppData, "Programs", AppFolderName);
            TempDirectory = Path.Combine(tempRoot, AppFolderName);
            DefaultDownloadFolder = Path.Combine(musicFolder, "Soundpad Track Hub");
            BaseDirectory = baseDirectory;
        }

        public static AppPaths CreateDefault()
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrWhiteSpace(music))
            {
                music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
            }

            return new AppPaths(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.GetTempPath(),
                music,
                AppContext.BaseDirectory);
        }

        /// <summary>%APPDATA%\SoundpadTrackHub — настройки, база загрузок, журналы.</summary>
        public string DataDirectory { get; }

        /// <summary>%LOCALAPPDATA%\SoundpadTrackHub — профиль WebView2 (cookies, кэш сайта).</summary>
        public string LocalDataDirectory { get; }

        /// <summary>%LOCALAPPDATA%\Programs\SoundpadTrackHub — папка установки (install.ps1).</summary>
        public string InstalledDirectory { get; }

        /// <summary>%TEMP%\SoundpadTrackHub — временные файлы загрузок.</summary>
        public string TempDirectory { get; }

        /// <summary>Папка с запущенным exe.</summary>
        public string BaseDirectory { get; }

        public string DefaultDownloadFolder { get; }

        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public string HistoryFile => Path.Combine(DataDirectory, "downloads.json");
        public string DownloadsPathMarker => Path.Combine(DataDirectory, "downloads_path.txt");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string WebViewDataDirectory => Path.Combine(LocalDataDirectory, "WebView2");
        public string TempDownloadsDirectory => Path.Combine(TempDirectory, "downloads");
        public string TempRenderDirectory => Path.Combine(TempDirectory, "render");
    }
}
