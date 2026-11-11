using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;
using SoundpadTrackHub.Services.Audio;

namespace SoundpadTrackHub.ViewModels
{
    public sealed class DownloadItemViewModel : ObservableObject
    {
        private bool _isPlaying;
        private bool _isBusy;

        public DownloadItemViewModel(DownloadRecord record)
        {
            Record = record;
        }

        public DownloadRecord Record { get; }

        public string FilePath => Record.FilePath;

        public string FileName => Record.FileName;

        public bool Exists => File.Exists(Record.FilePath);

        public string Meta
        {
            get
            {
                var size = Record.SizeBytes > 0 ? $"{Record.SizeBytes / 1024.0 / 1024.0:F1} МБ" : "—";
                var date = Record.DownloadedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                return Exists ? $"{size} · {date}" : $"Файл перемещён или удалён · {date}";
            }
        }

        public string? Badge => Record.IsBoostedCopy && Record.BoostMode is { } m ? $"+{m.GainDb():0} dB" : null;

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
    /// Список загрузок, предпрослушивание и усиленные копии.
    /// Свойство PositionSeconds имеет сеттер (перемотка), поэтому TwoWay-привязка
    /// слайдера не падает, как было с read-only Position в прошлой версии.
    /// </summary>
    public sealed class DownloadsViewModel : ObservableObject
    {
        private readonly ISettingsService _settings;
        private readonly DownloadHistoryService _history;
        private readonly AudioPreviewService _preview;
        private readonly BoostedCopyService _boost;
        private readonly ShellService _shell;
        private readonly DispatcherTimer _positionTimer;
        private readonly DispatcherTimer _saveVolumeTimer;
        private readonly ILogger _log = Log.ForContext<DownloadsViewModel>();

        private DownloadItemViewModel? _nowPlaying;
        private double _positionSeconds;
        private double _durationSeconds;
        private double _previewVolume;
        private string? _notice;
        private bool _noticeIsWarning;

        public DownloadsViewModel(ISettingsService settings, DownloadHistoryService history, AudioPreviewService preview, BoostedCopyService boost, ShellService shell)
        {
            _settings = settings;
            _history = history;
            _preview = preview;
            _boost = boost;
            _shell = shell;
            _previewVolume = settings.Current.PreviewVolume;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += (_, _) => UpdatePosition();

            _saveVolumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveVolumeTimer.Tick += (_, _) =>
            {
                _saveVolumeTimer.Stop();
                var v = _previewVolume;
                _settings.Update(s => s.PreviewVolume = v);
            };

            _preview.Stopped += (_, _) => OnPreviewStopped();
            _history.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(Refresh));

            PlayCommand = new AsyncRelayCommand(p => TogglePlayAsync(p as DownloadItemViewModel), p => p is DownloadItemViewModel { Exists: true });
            StopCommand = new RelayCommand(() => _preview.Stop());
            CreateCopyCommand = new AsyncRelayCommand(p => CreateCopyAsync(p as DownloadItemViewModel), p => p is DownloadItemViewModel { Exists: true, IsBusy: false });
            RevealCommand = new RelayCommand(p =>
            {
                if (p is DownloadItemViewModel item) _shell.RevealFile(item.FilePath);
            });
            RemoveCommand = new RelayCommand(p =>
            {
                if (p is DownloadItemViewModel item)
                {
                    if (_nowPlaying == item) _preview.Stop();
                    _history.Remove(item.Record.Id);
                }
            });
            OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(_settings.Current.DownloadFolder));
            DismissNoticeCommand = new RelayCommand(() => Notice = null);

            Refresh();
        }

        public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

        public bool HasItems => Items.Count > 0;

        public double PreviewVolume
        {
            get => _previewVolume;
            set
            {
                value = Math.Clamp(value, 0, 1);
                if (SetProperty(ref _previewVolume, value))
                {
                    _preview.SetVolume(value);
                    _saveVolumeTimer.Stop();
                    _saveVolumeTimer.Start();
                }
            }
        }

        // ---------- Воспроизведение ----------

        public DownloadItemViewModel? NowPlaying
        {
            get => _nowPlaying;
            private set
            {
                if (_nowPlaying is not null) _nowPlaying.IsPlaying = false;
                if (SetProperty(ref _nowPlaying, value))
                {
                    if (value is not null) value.IsPlaying = true;
                    OnPropertyChanged(nameof(IsPlaying));
                }
            }
        }

        public bool IsPlaying => NowPlaying is not null;

        public double PositionSeconds
        {
            get => _positionSeconds;
            set
            {
                if (Math.Abs(_positionSeconds - value) < 0.01)
                {
                    return;
                }

                _positionSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PositionText));
                _preview.Seek(TimeSpan.FromSeconds(value));
            }
        }

        public double DurationSeconds
        {
            get => _durationSeconds;
            private set => SetProperty(ref _durationSeconds, value);
        }

        public string PositionText => $"{TimeSpan.FromSeconds(_positionSeconds):m\\:ss} / {TimeSpan.FromSeconds(_durationSeconds):m\\:ss}";

        // ---------- Уведомления ----------

        public string? Notice
        {
            get => _notice;
            private set
            {
                if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
            }
        }

        public bool HasNotice => Notice is not null;

        public bool NoticeIsWarning
        {
            get => _noticeIsWarning;
            private set => SetProperty(ref _noticeIsWarning, value);
        }

        public ICommand PlayCommand { get; }

        public ICommand StopCommand { get; }

        public ICommand CreateCopyCommand { get; }

        public ICommand RevealCommand { get; }

        public ICommand RemoveCommand { get; }

        public ICommand OpenFolderCommand { get; }

        public ICommand DismissNoticeCommand { get; }

        public void Refresh()
        {
            var playingPath = _nowPlaying?.FilePath;
            Items.Clear();
            foreach (var r in _history.GetAll())
            {
                var vm = new DownloadItemViewModel(r);
                if (playingPath is not null && string.Equals(r.FilePath, playingPath, StringComparison.OrdinalIgnoreCase))
                {
                    vm.IsPlaying = true;
                    _nowPlaying = vm;
                }

                Items.Add(vm);
            }

            OnPropertyChanged(nameof(HasItems));
        }

        public void StopPreview() => _preview.Stop();

        private void ShowNotice(string text, bool warning)
        {
            NoticeIsWarning = warning;
            Notice = text;
        }

        private async Task TogglePlayAsync(DownloadItemViewModel? item)
        {
            if (item is null)
            {
                return;
            }

            if (_nowPlaying == item)
            {
                _preview.Stop();
                return;
            }

            await RestartPreviewAsync(item, TimeSpan.Zero);
        }

        private async Task RestartPreviewAsync(DownloadItemViewModel item, TimeSpan startAt)
        {
            try
            {
                item.IsBusy = true;
                var plan = await _preview.PlayAsync(item.FilePath, VolumeMode.Normal, PreviewVolume);
                if (startAt > TimeSpan.Zero) _preview.Seek(startAt);
                NowPlaying = item;
                DurationSeconds = _preview.Duration.TotalSeconds;
                UpdatePosition();
                _positionTimer.Start();

                if (plan.WasDowngraded && plan.Warning is not null)
                {
                    ShowNotice(plan.Warning, warning: true);
                }
                else if (Notice is not null && NoticeIsWarning)
                {
                    Notice = null;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Preview failed");
                NowPlaying = null;
                ShowNotice("Не удалось воспроизвести файл: " + ex.Message, warning: true);
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        private void OnPreviewStopped()
        {
            _positionTimer.Stop();
            NowPlaying = null;
            _positionSeconds = 0;
            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(PositionText));
        }

        private void UpdatePosition()
        {
            _positionSeconds = _preview.Position.TotalSeconds;
            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(PositionText));
        }

        private async Task CreateCopyAsync(DownloadItemViewModel? item)
        {
            if (item is null)
            {
                return;
            }

            // Отдельная копия +6 dB с limiter'ом; оригинал не изменяется.
            var mode = VolumeMode.Loud;
            item.IsBusy = true;
            ShowNotice("Создаётся громкая копия (+6 dB)…", warning: false);
            try
            {
                var result = await _boost.CreateCopyAsync(item.FilePath, mode);
                if (result.Created && result.OutputPath is not null)
                {
                    _history.Add(new DownloadRecord
                    {
                        FilePath = result.OutputPath,
                        FileName = Path.GetFileName(result.OutputPath),
                        SourceUrl = item.Record.SourceUrl,
                        DownloadedAtUtc = DateTime.UtcNow,
                        SizeBytes = new FileInfo(result.OutputPath).Length,
                        IsBoostedCopy = true,
                        BoostMode = result.Plan.AppliedMode,
                        OriginalPath = item.FilePath,
                    });
                    ShowNotice(result.Message, warning: false);
                }
                else
                {
                    ShowNotice(result.Message, warning: true);
                }
            }
            finally
            {
                item.IsBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
