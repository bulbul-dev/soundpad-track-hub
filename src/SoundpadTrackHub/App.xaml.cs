using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Integration;
using SoundpadTrackHub.Services;
using SoundpadTrackHub.Services.Audio;
using SoundpadTrackHub.ViewModels;
using SoundpadTrackHub.Views;

namespace SoundpadTrackHub
{
    /// <summary>Точка сборки приложения (composition root) и управление жизненным циклом.</summary>
    public partial class App : Application
    {
        public const string UninstallArgument = "--uninstall";

        private SingleInstanceGuard? _guard;
        private AppPaths? _paths;
        private JsonSettingsService? _settings;
        private TempFileManager? _temp;
        private IDialogService? _dialogs;
        private AudioPreviewService? _preview;
        private SoundpadWatcher? _watcher;
        private HitmozWebViewController? _browser;
        private CompanionWindowController? _companion;
        private TrayIcon? _tray;
        private MainWindow? _window;
        private MainViewModel? _mainVm;
        private UninstallService? _uninstall;
        private ShellService? _shell;
        private bool _shuttingDown;
        private bool _showingCrashDialog;
        private bool _trayHintShown;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Только один экземпляр: вторая копия просит первую показать окно и выходит.
            var uninstallMode = e.Args.Any(a => string.Equals(a, UninstallArgument, StringComparison.OrdinalIgnoreCase));

            _guard = SingleInstanceGuard.Acquire();
            if (!_guard.IsFirstInstance)
            {
                if (uninstallMode)
                {
                    MessageBox.Show("Soundpad Track Hub сейчас запущен. Закройте его (значок в области уведомлений → «Выход») и повторите удаление, либо удалите через «Настройки → Удалить приложение».",
                        "Удаление Soundpad Track Hub", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _guard.SignalFirstInstance();
                _guard.Dispose();
                _guard = null;
                Shutdown(0);
                return;
            }

            _paths = AppPaths.CreateDefault();
            Logging.Configure(_paths);
            Log.Information("Soundpad Track Hub starting, args: {Args}", string.Join(' ', e.Args));

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", args.IsTerminating);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _temp?.CleanupAll();
            SessionEnding += (_, _) => ShutdownApplication();

            var startedByAutostart = e.Args.Any(a => string.Equals(a, AutostartService.AutostartArgument, StringComparison.OrdinalIgnoreCase));

            // 2. Сервисы.
            _settings = new JsonSettingsService(_paths.SettingsFile, _paths.DefaultDownloadFolder, _paths.DownloadsPathMarker);
            _settings.Save();
            _temp = new TempFileManager(_paths.TempDownloadsDirectory, _paths.TempRenderDirectory);
            var cleaned = _temp.CleanupAll();
            if (cleaned > 0) Log.Information("Removed {Count} stale temp files", cleaned);

            _dialogs = new DialogService();
            _shell = new ShellService();
            var history = new DownloadHistoryService(_paths.HistoryFile);
            var exePath = Environment.ProcessPath ?? System.IO.Path.Combine(_paths.BaseDirectory, AppPaths.ExecutableName);
            var autostart = new AutostartService(new RegistryRunKeyStore(), exePath);
            var analyzer = new AudioAnalyzer();
            _preview = new AudioPreviewService(analyzer);
            var boost = new BoostedCopyService(analyzer, _temp);
            var locator = new SoundpadLocator();
            _uninstall = new UninstallService(_dialogs, autostart,
                new UninstallPlanner(_paths, UninstallService.DefaultShortcutFolders(), locator.GetSoundpadDirectories()),
                history);

            // Удаление из «Установка и удаление программ» Windows: окно не открывается.
            if (uninstallMode)
            {
                Log.Information("Started in uninstall mode");
                RunUninstall();
                if (!_shuttingDown)
                {
                    ShutdownApplication();
                }

                return;
            }

            // Soundpad: наблюдение за окном, категория в списке звуков, громкость.
            _watcher = new SoundpadWatcher(locator);
            var librarySync = new SoundpadLibrarySync(new SoundpadRemoteClient(), history, _settings);
            var volumeController = new SoundpadVolumeController(new SoundpadRegistryStore(), locator, _watcher, _settings, _dialogs);

            // 3. ViewModels.
            var downloadsVm = new DownloadsViewModel(_settings, history, _preview, boost, _shell);
            var settingsVm = new SettingsViewModel(_settings, autostart, _shell, _paths, _dialogs);
            settingsVm.UninstallRequested += (_, _) => RunUninstall();
            var searchVm = new SearchViewModel();
            var volumeVm = new SoundpadVolumeViewModel(volumeController);
            var replacer = new SoundpadTrackReplacer(
                new SoundpadSoundList(SoundpadSoundList.DefaultListPath, System.IO.Path.Combine(_paths.DataDirectory, "soundpad-backups")),
                locator, _dialogs);
            var replaceVm = new ReplaceViewModel(history, replacer);
            _mainVm = new MainViewModel(_settings, _shell, downloadsVm, settingsVm, searchVm, volumeVm, replaceVm);
            var mainVm = _mainVm;

            async Task SyncSoundpadAsync(bool report)
            {
                var result = await librarySync.SyncPendingAsync();
                if (report || result.Added > 0 || !result.Success)
                {
                    mainVm.StatusText = result.Message;
                }
            }

            settingsVm.SyncSoundpadRequested += async (_, _) => await SyncSoundpadAsync(report: true);
            history.Changed += async (_, _) => await SyncSoundpadAsync(report: false);

            // 4. Окно, браузер, Soundpad.
            _window = new MainWindow(_mainVm);
            _window.Closing += OnWindowClosing;

            _browser = new HitmozWebViewController(_window.HitmozWebView, new HitmozDownloadPolicy(), _dialogs, _temp, history, _settings, _paths.WebViewDataDirectory);
            _browser.StateChanged += (_, s) => _mainVm.OnBrowserStateChanged(s.State, s.Detail);
            _browser.Message += (_, m) => _mainVm.StatusText = m;
            _browser.DownloadProgress += (_, p) => _mainVm.OnDownloadProgress(p);
            _browser.DownloadCompleted += async (_, r) =>
            {
                mainVm.OnDownloadCompleted(r);
                if (!_settings.Current.AddToSoundpadCategory)
                {
                    return;
                }

                var result = await librarySync.SyncPendingAsync();
                mainVm.StatusText = result.Message;
                if (result.Success)
                {
                    mainVm.LastCompletedHint = $"Трек уже в Soundpad — категория «{librarySync.CategoryName}».";
                }
                else
                {
                    mainVm.LastCompletedHint = result.Message + " " + MainViewModel.DragHint + ".";
                }
            };
            _mainVm.AttachBrowser(_browser);
            searchVm.AttachHost(_browser);
            _window.Loaded += async (_, _) => await _browser.InitializeAsync();

            _watcher.StateChanged += async (_, s) =>
            {
                mainVm.SoundpadState = s;
                volumeVm.Refresh();
                if (s == Models.SoundpadConnectionState.Connected)
                {
                    // Soundpad принимает команды не сразу после запуска.
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await SyncSoundpadAsync(report: false);
                }
            };
            _companion = new CompanionWindowController(_window, _watcher, _settings, startHiddenUntilSoundpad: startedByAutostart)
            {
                SuppressHide = () => volumeController.IsRestartingSoundpad || replacer.IsRestartingSoundpad,
            };

            _tray = new TrayIcon(
                show: () => _companion.ShowFromUser(),
                openHitmoz: () =>
                {
                    _companion.ShowFromUser();
                    _mainVm.OpenHitmozCommand.Execute(null);
                },
                openFolder: () => _shell.OpenFolder(_settings.Current.DownloadFolder),
                exit: RequestExit);

            _companion.Start();
            _watcher.Start();
            _guard.ListenForActivation(() => Dispatcher.BeginInvoke(new Action(() => _companion?.ShowFromUser())));

            if (startedByAutostart)
            {
                Log.Information("Started by Windows autostart, waiting for Soundpad");
            }
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (_shuttingDown)
            {
                return;
            }

            // Крестик прячет окно в трей, а не завершает процесс: иначе Track Hub
            // не сможет снова появиться при следующем запуске Soundpad.
            // Полный выход — «Выход» в меню значка в области уведомлений.
            e.Cancel = true;
            if (_companion is null)
            {
                Dispatcher.BeginInvoke(new Action(RequestExit));
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _companion.HideToTray();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray?.ShowHint("Soundpad Track Hub работает в фоне",
                        "Окно появится при следующем запуске Soundpad. Выход — правый клик по значку → «Выход».");
                }
            }));
        }

        private void RequestExit()
        {
            if (_shuttingDown)
            {
                return;
            }

            if (_browser?.HasActiveDownloads == true && _dialogs is not null)
            {
                var ok = _dialogs.Confirm("Soundpad Track Hub", "Идёт загрузка. Закрыть Track Hub и отменить её?",
                    "Временный файл будет удалён.", "Закрыть", "Продолжить загрузку");
                if (!ok)
                {
                    return;
                }
            }

            ShutdownApplication();
        }

        private void RunUninstall()
        {
            if (_uninstall is null || _shuttingDown)
            {
                return;
            }

            var options = _uninstall.AskOptions();
            if (options is null)
            {
                return;
            }

            _browser?.CancelDownloads();
            _preview?.Stop();
            _watcher?.Stop();

            var report = _uninstall.Execute(options);
            _uninstall.ShowResult(report);
            ShutdownApplication(saveSettings: !options.DeleteUserData);
        }

        private void ShutdownApplication() => ShutdownApplication(saveSettings: true);

        private void ShutdownApplication(bool saveSettings)
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            Log.Information("Shutting down");

            Try(() => _browser?.CancelDownloads());
            Try(() => _preview?.Dispose());
            if (saveSettings)
            {
                Try(() => _companion?.SaveGeometry());
            }

            Try(() => _companion?.Dispose());
            Try(() => _watcher?.Dispose());
            Try(() => _tray?.Dispose());
            Try(() => _browser?.Dispose());
            Try(() => _window?.Close());
            Try(() => _temp?.CleanupAll());

            Shutdown(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Try(() => _temp?.CleanupAll());
            _guard?.Dispose();
            _guard = null;
            Log.Information("Exited with code {Code}", e.ApplicationExitCode);
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;

            if (_showingCrashDialog || _dialogs is null || _shuttingDown)
            {
                return;
            }

            try
            {
                _showingCrashDialog = true;
                _dialogs.Info("Soundpad Track Hub", "Произошла непредвиденная ошибка. Приложение продолжит работу.",
                    e.Exception.Message + "\nПодробности записаны в журнал.", DialogIcon.Warning);
            }
            finally
            {
                _showingCrashDialog = false;
            }
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Shutdown step failed");
            }
        }
    }
}
