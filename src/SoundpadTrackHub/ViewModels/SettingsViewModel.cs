using System;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using Microsoft.Win32;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.ViewModels
{
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly ISettingsService _settings;
        private readonly IAutostartService _autostart;
        private readonly ShellService _shell;
        private readonly AppPaths _paths;
        private readonly IDialogService _dialogs;
        private bool _autostartEnabled;

        public SettingsViewModel(ISettingsService settings, IAutostartService autostart, ShellService shell, AppPaths paths, IDialogService dialogs)
        {
            _settings = settings;
            _autostart = autostart;
            _shell = shell;
            _paths = paths;
            _dialogs = dialogs;
            _autostartEnabled = autostart.IsEnabled;

            _settings.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(DownloadFolder));
                OnPropertyChanged(nameof(DockToSoundpad));
                OnPropertyChanged(nameof(HideWhenSoundpadClosed));
                OnPropertyChanged(nameof(AddToSoundpadCategory));
                OnPropertyChanged(nameof(CategoryHint));
            };
            SyncSoundpadCommand = new RelayCommand(() => SyncSoundpadRequested?.Invoke(this, EventArgs.Empty));

            ChooseFolderCommand = new RelayCommand(ChooseFolder);
            OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(DownloadFolder));
            ResetFolderCommand = new RelayCommand(() => _settings.Update(s => s.DownloadFolder = _paths.DefaultDownloadFolder),
                () => !string.Equals(DownloadFolder, _paths.DefaultDownloadFolder, StringComparison.OrdinalIgnoreCase));
            OpenLogsCommand = new RelayCommand(() => _shell.OpenFolder(_paths.LogsDirectory));
            UninstallCommand = new RelayCommand(() => UninstallRequested?.Invoke(this, EventArgs.Empty));
        }

        /// <summary>Обрабатывается App: удаление требует закрытия приложения.</summary>
        public event EventHandler? UninstallRequested;

        public event EventHandler? SyncSoundpadRequested;

        public bool AddToSoundpadCategory
        {
            get => _settings.Current.AddToSoundpadCategory;
            set => _settings.Update(s => s.AddToSoundpadCategory = value);
        }

        public string CategoryHint => $"Скачанные треки попадают в категорию «{_settings.Current.SoundpadCategoryName}» в списке звуков Soundpad (через официальный интерфейс удалённого управления).";

        public ICommand SyncSoundpadCommand { get; }

        public string DownloadFolder => _settings.Current.DownloadFolder;

        public bool AutostartEnabled
        {
            get => _autostartEnabled;
            set
            {
                if (_autostartEnabled == value)
                {
                    return;
                }

                if (_autostart.SetEnabled(value))
                {
                    _autostartEnabled = value;
                }
                else
                {
                    _dialogs.Info("Автозапуск", "Не удалось изменить автозапуск Windows.", "Подробности записаны в журнал.", DialogIcon.Warning);
                }

                OnPropertyChanged();
            }
        }

        public bool DockToSoundpad
        {
            get => _settings.Current.DockToSoundpad;
            set => _settings.Update(s => s.DockToSoundpad = value);
        }

        public bool HideWhenSoundpadClosed
        {
            get => _settings.Current.HideWhenSoundpadClosed;
            set => _settings.Update(s => s.HideWhenSoundpadClosed = value);
        }

        public string Version =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? "2.0.0";

        public string DataFolder => _paths.DataDirectory;

        public ICommand ChooseFolderCommand { get; }

        public ICommand OpenFolderCommand { get; }

        public ICommand ResetFolderCommand { get; }

        public ICommand OpenLogsCommand { get; }

        public ICommand UninstallCommand { get; }

        private void ChooseFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Папка для скачанных MP3",
                InitialDirectory = Directory.Exists(DownloadFolder) ? DownloadFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var folder = dialog.FolderName;
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".trackhub-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.Info("Папка недоступна", "В эту папку нельзя записывать файлы. Выберите другую.", ex.Message, DialogIcon.Warning);
                return;
            }

            _settings.Update(s => s.DownloadFolder = folder);
        }
    }
}
