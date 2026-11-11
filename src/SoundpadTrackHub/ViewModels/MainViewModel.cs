using System;
using System.IO;
using System.Windows.Input;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Integration;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly ISettingsService _settings;
        private readonly ShellService _shell;
        private IBrowserHost? _browser;

        private AppPage _currentPage = AppPage.Search;
        private string _lastCompletedHint = DragHint;
        private SoundpadConnectionState _soundpadState = SoundpadConnectionState.Waiting;
        private BrowserState _browserState = BrowserState.NotStarted;
        private string? _browserErrorDetail;
        private bool _showSiteAnyway;
        private string _statusText = "Готово";
        private string? _activeDownloadName;
        private double _activeDownloadProgress;
        private bool _activeDownloadIndeterminate;
        private DownloadRecord? _lastCompleted;

        public const string DragHint = "Перетащите скачанный файл в список звуков Soundpad";

        public MainViewModel(ISettingsService settings, ShellService shell, DownloadsViewModel downloads, SettingsViewModel settingsPage, SearchViewModel search, SoundpadVolumeViewModel volume, ReplaceViewModel replace)
        {
            _settings = settings;
            _shell = shell;
            Downloads = downloads;
            Replace = replace;
            SettingsPage = settingsPage;
            Search = search;
            Volume = volume;
            Search.Message += (_, m) => StatusText = m;
            Volume.Message += (_, m) => StatusText = m;

            _settings.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(DownloadFolder));
                OnPropertyChanged(nameof(DownloadFolderShort));
                OnPropertyChanged(nameof(IsDocked));
            };

            ShowPageCommand = new RelayCommand(p =>
            {
                if (p is AppPage page) CurrentPage = page;
                else if (p is string s && Enum.TryParse<AppPage>(s, out var parsed)) CurrentPage = parsed;
            });
            OpenHitmozCommand = new RelayCommand(OpenHitmoz);
            BackCommand = new RelayCommand(() => _browser?.GoBack(), () => _browser is not null && IsBrowserUsable);
            ReloadCommand = new RelayCommand(() => _browser?.Reload(), () => _browser is not null);
            RetryCommand = new AsyncRelayCommand(async () =>
            {
                ShowSiteAnyway = false;
                if (_browser is not null) await _browser.RetryAsync();
            });
            OpenExternalCommand = new RelayCommand(() =>
            {
                if (_shell.OpenInBrowser(Hitmoz.HomeUrl)) StatusText = "Hitmoz открыт во внешнем браузере.";
            });
            GetWebView2Command = new RelayCommand(() => _shell.OpenInBrowser(ShellService.WebView2DownloadUrl));
            ShowSiteAnywayCommand = new RelayCommand(() => ShowSiteAnyway = true);
            OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(DownloadFolder));
            CancelDownloadCommand = new RelayCommand(() => _browser?.CancelDownloads(), () => IsDownloading);
            DismissCompletedCommand = new RelayCommand(() => LastCompleted = null);
            RevealCompletedCommand = new RelayCommand(() =>
            {
                if (LastCompleted is not null) _shell.RevealFile(LastCompleted.FilePath);
            });
        }

        public DownloadsViewModel Downloads { get; }

        public SettingsViewModel SettingsPage { get; }

        public SearchViewModel Search { get; }

        public SoundpadVolumeViewModel Volume { get; }

        public ReplaceViewModel Replace { get; }

        public string Title => "Hitmoz • Soundpad";

        // ---------- Навигация ----------

        public AppPage CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value))
                {
                    OnPropertyChanged(nameof(IsSearchPage));
                    OnPropertyChanged(nameof(IsBrowserPage));
                    OnPropertyChanged(nameof(IsDownloadsPage));
                    OnPropertyChanged(nameof(IsSettingsPage));
                    OnPropertyChanged(nameof(IsSecurityPage));
                    OnPropertyChanged(nameof(IsReplacePage));
                    OnPropertyChanged(nameof(IsWebViewVisible));
                    OnPropertyChanged(nameof(IsBrowserErrorVisible));
                    if (value == AppPage.Downloads) Downloads.Refresh();
                    if (value == AppPage.Replace) Replace.Refresh();
                }
            }
        }

        public bool IsSearchPage => CurrentPage == AppPage.Search;

        public bool IsBrowserPage => CurrentPage == AppPage.Browser;

        public bool IsDownloadsPage => CurrentPage == AppPage.Downloads;

        public bool IsSettingsPage => CurrentPage == AppPage.Settings;

        public bool IsSecurityPage => CurrentPage == AppPage.Security;

        public bool IsReplacePage => CurrentPage == AppPage.Replace;

        // ---------- Soundpad ----------

        public SoundpadConnectionState SoundpadState
        {
            get => _soundpadState;
            set
            {
                if (SetProperty(ref _soundpadState, value))
                {
                    OnPropertyChanged(nameof(SoundpadStateText));
                    OnPropertyChanged(nameof(IsSoundpadConnected));
                    OnPropertyChanged(nameof(IsSoundpadClosed));
                }
            }
        }

        public string SoundpadStateText => SoundpadState.DisplayText();

        public bool IsSoundpadConnected => SoundpadState == SoundpadConnectionState.Connected;

        public bool IsSoundpadClosed => SoundpadState == SoundpadConnectionState.Closed;

        public bool IsDocked
        {
            get => _settings.Current.DockToSoundpad;
            set
            {
                if (_settings.Current.DockToSoundpad != value)
                {
                    _settings.Update(s => s.DockToSoundpad = value);
                }
            }
        }

        // ---------- Браузер ----------

        public BrowserState BrowserState
        {
            get => _browserState;
            private set
            {
                if (SetProperty(ref _browserState, value))
                {
                    OnPropertyChanged(nameof(IsBrowserLoading));
                    OnPropertyChanged(nameof(IsBrowserUsable));
                    OnPropertyChanged(nameof(IsWebViewVisible));
                    OnPropertyChanged(nameof(IsBrowserErrorVisible));
                    OnPropertyChanged(nameof(IsRuntimeMissing));
                    OnPropertyChanged(nameof(IsForbidden));
                    OnPropertyChanged(nameof(BrowserErrorTitle));
                    OnPropertyChanged(nameof(BrowserErrorText));
                    OnPropertyChanged(nameof(BrowserErrorGlyph));
                    OnPropertyChanged(nameof(ConnectionText));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsBrowserLoading => BrowserState is BrowserState.Loading or BrowserState.Initializing;

        public bool IsBrowserUsable => BrowserState is BrowserState.Ready or BrowserState.Loading
                                       || (BrowserState == BrowserState.Forbidden && ShowSiteAnyway);

        public bool IsRuntimeMissing => BrowserState == BrowserState.RuntimeMissing;

        public bool IsForbidden => BrowserState == BrowserState.Forbidden;

        /// <summary>
        /// WebView2 — отдельное HWND-окно, поверх него WPF рисовать не может,
        /// поэтому на других страницах и при ошибке оно скрывается.
        /// </summary>
        public bool IsWebViewVisible => IsBrowserPage && (IsBrowserUsable || BrowserState is BrowserState.Initializing or BrowserState.NotStarted);

        public bool IsBrowserErrorVisible => IsBrowserPage && !IsWebViewVisible;

        public bool ShowSiteAnyway
        {
            get => _showSiteAnyway;
            set
            {
                if (SetProperty(ref _showSiteAnyway, value))
                {
                    OnPropertyChanged(nameof(IsBrowserUsable));
                    OnPropertyChanged(nameof(IsWebViewVisible));
                    OnPropertyChanged(nameof(IsBrowserErrorVisible));
                }
            }
        }

        public string BrowserErrorGlyph => BrowserState switch
        {
            BrowserState.RuntimeMissing => "",
            BrowserState.Forbidden => "",
            _ => "",
        };

        public string BrowserErrorTitle => BrowserState switch
        {
            BrowserState.RuntimeMissing => "Не установлен WebView2 Runtime",
            BrowserState.Forbidden => "Hitmoz ответил HTTP 403",
            _ => "Hitmoz недоступен",
        };

        public string BrowserErrorText => BrowserState switch
        {
            BrowserState.RuntimeMissing =>
                "Для встроенного сайта нужен компонент Microsoft Edge WebView2 Runtime. Установите его с сайта Microsoft и нажмите «Повторить» — или откройте Hitmoz во внешнем браузере.",
            BrowserState.Forbidden =>
                "Сайт отказал в доступе (HTTP 403). Track Hub не обходит защиту сайтов, CAPTCHA и ограничения доступа. " +
                "Попробуйте позже или откройте Hitmoz во внешнем браузере. Если на странице есть проверка, которую можно пройти вручную, нажмите «Показать страницу».",
            _ => _browserErrorDetail ?? "Не удалось открыть ru.hitmoz.org. Проверьте подключение к интернету.",
        };

        public string ConnectionText => BrowserState switch
        {
            BrowserState.Ready => "Hitmoz открыт",
            BrowserState.Loading => "Загрузка страницы…",
            BrowserState.Initializing => "Запуск WebView2…",
            BrowserState.RuntimeMissing => "WebView2 не найден",
            BrowserState.Forbidden => "HTTP 403",
            BrowserState.Unavailable => "Сайт недоступен",
            _ => "Не запущено",
        };

        public void AttachBrowser(IBrowserHost browser)
        {
            _browser = browser;
            CommandManager.InvalidateRequerySuggested();
        }

        public void OnBrowserStateChanged(BrowserState state, string? detail)
        {
            _browserErrorDetail = detail;
            if (state is BrowserState.Ready)
            {
                ShowSiteAnyway = false;
            }

            BrowserState = state;
            OnPropertyChanged(nameof(BrowserErrorText));
        }

        private void OpenHitmoz()
        {
            _ = Search.StopPlaybackAsync();
            CurrentPage = AppPage.Browser;
            ShowSiteAnyway = false;
            _browser?.GoHome();
        }

        // ---------- Загрузка ----------

        public string? ActiveDownloadName
        {
            get => _activeDownloadName;
            private set
            {
                if (SetProperty(ref _activeDownloadName, value))
                {
                    OnPropertyChanged(nameof(IsDownloading));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsDownloading => ActiveDownloadName is not null;

        public double ActiveDownloadProgress
        {
            get => _activeDownloadProgress;
            private set => SetProperty(ref _activeDownloadProgress, value);
        }

        public bool ActiveDownloadIndeterminate
        {
            get => _activeDownloadIndeterminate;
            private set => SetProperty(ref _activeDownloadIndeterminate, value);
        }

        public void OnDownloadProgress(DownloadProgressInfo info)
        {
            if (info.IsFinished)
            {
                ActiveDownloadName = null;
                ActiveDownloadProgress = 0;
                return;
            }

            ActiveDownloadName = info.FileName;
            ActiveDownloadIndeterminate = info.Total is null;
            ActiveDownloadProgress = info.Total is { } t && t > 0 ? Math.Clamp((double)info.Received / t, 0, 1) : 0;
        }

        public DownloadRecord? LastCompleted
        {
            get => _lastCompleted;
            private set
            {
                if (SetProperty(ref _lastCompleted, value))
                {
                    OnPropertyChanged(nameof(HasLastCompleted));
                    OnPropertyChanged(nameof(LastCompletedPath));
                    OnPropertyChanged(nameof(LastCompletedName));
                }
            }
        }

        public bool HasLastCompleted => LastCompleted is not null;

        public string LastCompletedPath => LastCompleted?.FilePath ?? string.Empty;

        public string LastCompletedName => LastCompleted?.FileName ?? string.Empty;

        /// <summary>Подсказка под скачанным треком: перетащить вручную или «уже в Soundpad».</summary>
        public string LastCompletedHint
        {
            get => _lastCompletedHint;
            set => SetProperty(ref _lastCompletedHint, value);
        }

        public void OnDownloadCompleted(DownloadRecord record)
        {
            LastCompletedHint = DragHint;
            LastCompleted = record;
            Downloads.Refresh();
        }

        // ---------- Статус ----------

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string DownloadFolder => _settings.Current.DownloadFolder;

        public string DownloadFolderShort
        {
            get
            {
                var f = DownloadFolder;
                var name = Path.GetFileName(f.TrimEnd('\\'));
                return string.IsNullOrEmpty(name) ? f : name;
            }
        }

        public ICommand ShowPageCommand { get; }

        public ICommand OpenHitmozCommand { get; }

        public ICommand BackCommand { get; }

        public ICommand ReloadCommand { get; }

        public ICommand RetryCommand { get; }

        public ICommand OpenExternalCommand { get; }

        public ICommand GetWebView2Command { get; }

        public ICommand ShowSiteAnywayCommand { get; }

        public ICommand OpenFolderCommand { get; }

        public ICommand CancelDownloadCommand { get; }

        public ICommand DismissCompletedCommand { get; }

        public ICommand RevealCompletedCommand { get; }
    }
}
