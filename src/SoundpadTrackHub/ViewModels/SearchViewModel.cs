using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Integration;

namespace SoundpadTrackHub.ViewModels
{
    public sealed class SearchResultViewModel : ObservableObject
    {
        private bool _isPlaying;
        private bool _isBusy;

        public SearchResultViewModel(HitmozTrack track)
        {
            Track = track;
        }

        public HitmozTrack Track { get; }

        public string Title => string.IsNullOrWhiteSpace(Track.Title) ? "Без названия" : Track.Title;

        public string Artist => Track.Artist;

        public string Duration => Track.Duration;

        public bool CanDownload => Track.DownloadUrl.Length > 0;

        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }
    }

    /// <summary>
    /// Вкладка «Поиск»: вводите название — через паузу в набор Track Hub открывает
    /// страницу поиска Hitmoz во встроенном браузере и показывает найденные треки списком.
    /// Прослушивание — плеером самого сайта, скачивание — штатной ссылкой с подтверждением.
    /// </summary>
    public sealed class SearchViewModel : ObservableObject
    {
        public const int MinQueryLength = 2;

        private readonly ILogger _log = Log.ForContext<SearchViewModel>();
        private readonly DispatcherTimer _debounce;
        private IHitmozSearchHost? _host;
        private CancellationTokenSource? _cts;
        private string _query = string.Empty;
        private string _status = "Введите название трека или исполнителя.";
        private bool _isSearching;
        private bool _hasError;
        private SearchResultViewModel? _playing;

        public SearchViewModel()
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _debounce.Tick += async (_, _) =>
            {
                _debounce.Stop();
                await RunSearchAsync();
            };

            SearchNowCommand = new AsyncRelayCommand(async () =>
            {
                _debounce.Stop();
                await RunSearchAsync();
            });
            ClearCommand = new RelayCommand(() => Query = string.Empty);
            PlayCommand = new AsyncRelayCommand(p => TogglePlayAsync(p as SearchResultViewModel));
            DownloadCommand = new RelayCommand(p => Download(p as SearchResultViewModel), p => p is SearchResultViewModel { CanDownload: true });
        }

        /// <summary>Сообщения для статусной строки окна.</summary>
        public event EventHandler<string>? Message;

        public ObservableCollection<SearchResultViewModel> Results { get; } = new();

        public bool HasResults => Results.Count > 0;

        public string Query
        {
            get => _query;
            set
            {
                if (!SetProperty(ref _query, value ?? string.Empty))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasQuery));
                _debounce.Stop();
                if (_query.Trim().Length >= MinQueryLength)
                {
                    _debounce.Start();
                }
                else
                {
                    _cts?.Cancel();
                    Results.Clear();
                    OnPropertyChanged(nameof(HasResults));
                    IsSearching = false;
                    SetStatus("Введите название трека или исполнителя.", false);
                }
            }
        }

        public bool HasQuery => _query.Length > 0;

        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public bool HasError
        {
            get => _hasError;
            private set => SetProperty(ref _hasError, value);
        }

        public bool IsSearching
        {
            get => _isSearching;
            private set => SetProperty(ref _isSearching, value);
        }

        public ICommand SearchNowCommand { get; }

        public ICommand ClearCommand { get; }

        public ICommand PlayCommand { get; }

        public ICommand DownloadCommand { get; }

        public void AttachHost(IHitmozSearchHost host) => _host = host;

        private void SetStatus(string text, bool error)
        {
            Status = text;
            HasError = error;
        }

        private async Task RunSearchAsync()
        {
            var query = _query.Trim();
            if (_host is null || query.Length < MinQueryLength)
            {
                return;
            }

            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            IsSearching = true;
            SetStatus($"Ищем «{query}» на Hitmoz…", false);
            try
            {
                var tracks = await _host.SearchAsync(query, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _playing = null;
                Results.Clear();
                foreach (var t in tracks)
                {
                    Results.Add(new SearchResultViewModel(t));
                }

                OnPropertyChanged(nameof(HasResults));
                SetStatus(tracks.Count == 0 ? $"По запросу «{query}» ничего не найдено." : $"Найдено: {tracks.Count}. Нажмите ▶, чтобы послушать.", false);
            }
            catch (OperationCanceledException)
            {
                // Пользователь продолжил печатать — запустится новый поиск.
            }
            catch (HitmozSearchException ex)
            {
                SetStatus(ex.Message, true);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Search failed");
                SetStatus("Поиск не удался: " + ex.Message, true);
            }
            finally
            {
                if (_cts == cts)
                {
                    IsSearching = false;
                }
            }
        }

        private async Task TogglePlayAsync(SearchResultViewModel? item)
        {
            if (item is null || _host is null)
            {
                return;
            }

            try
            {
                item.IsBusy = true;
                if (_playing is not null && _playing != item)
                {
                    _playing.IsPlaying = false;
                }

                var playing = await _host.TogglePlayAsync(item.Track.Id);
                item.IsPlaying = playing;
                _playing = playing ? item : null;
                if (!playing && !item.IsPlaying)
                {
                    Message?.Invoke(this, "Пауза.");
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Play toggle failed");
                Message?.Invoke(this, "Не удалось включить трек: " + ex.Message);
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        public async Task StopPlaybackAsync()
        {
            if (_host is null || _playing is null)
            {
                return;
            }

            _playing.IsPlaying = false;
            _playing = null;
            try
            {
                await _host.StopPlaybackAsync();
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Stop playback failed");
            }
        }

        private void Download(SearchResultViewModel? item)
        {
            if (item is null || _host is null)
            {
                return;
            }

            if (!_host.StartDownload(item.Track.DownloadUrl))
            {
                Message?.Invoke(this, "У этого трека нет ссылки на MP3 на ru.hitmoz.org.");
                return;
            }

            Message?.Invoke(this, $"Скачивание «{item.Title}» — подтвердите загрузку.");
        }
    }
}
