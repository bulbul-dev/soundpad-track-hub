using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Serilog;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Integration;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.ViewModels
{
    /// <summary>Скачанный трек, который можно поставить вместо звука в Soundpad.</summary>
    public sealed class ReplaceSourceItem
    {
        public ReplaceSourceItem(string filePath, string meta)
        {
            FilePath = filePath;
            Meta = meta;
        }

        public string FilePath { get; }

        public string FileName => Path.GetFileNameWithoutExtension(FilePath);

        public string Meta { get; }
    }

    /// <summary>Звук Soundpad в списке выбора «на какой поменять».</summary>
    public sealed class ReplaceTargetItem
    {
        public ReplaceTargetItem(SoundpadSound sound) => Sound = sound;

        public SoundpadSound Sound { get; }

        public string Number => Sound.Index.ToString();

        public string Name => Sound.DisplayName;

        public string Meta => string.IsNullOrEmpty(Sound.Duration) ? Sound.CategoryText : $"{Sound.Duration} · {Sound.CategoryText}";
    }

    /// <summary>
    /// Вкладка «Замена»: для бесплатной (демо) версии Soundpad, где нельзя добавлять новые звуки
    /// через удалённое управление. Выбираете скачанный трек → выбираете звук Soundpad → он заменяется.
    /// </summary>
    public sealed class ReplaceViewModel : ObservableObject
    {
        private readonly DownloadHistoryService _history;
        private readonly SoundpadTrackReplacer _replacer;
        private readonly ILogger _log = Log.ForContext<ReplaceViewModel>();
        private List<ReplaceTargetItem> _allTargets = new();
        private ReplaceSourceItem? _selectedSource;
        private string _filter = string.Empty;
        private string _status = string.Empty;
        private bool _isBusy;

        public ReplaceViewModel(DownloadHistoryService history, SoundpadTrackReplacer replacer)
        {
            _history = history;
            _replacer = replacer;
            ChooseCommand = new RelayCommand(p => { if (p is ReplaceSourceItem s) BeginPick(s); });
            BackCommand = new RelayCommand(_ => SelectedSource = null);
            ReplaceCommand = new AsyncRelayCommand(p => p is ReplaceTargetItem t ? ReplaceAsync(t) : Task.CompletedTask, _ => !IsBusy);
            RefreshCommand = new RelayCommand(_ => Refresh());
            _history.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshSources));
        }

        public ObservableCollection<ReplaceSourceItem> Sources { get; } = new();

        public ObservableCollection<ReplaceTargetItem> Targets { get; } = new();

        public ICommand ChooseCommand { get; }

        public ICommand BackCommand { get; }

        public ICommand ReplaceCommand { get; }

        public ICommand RefreshCommand { get; }

        public bool HasSources => Sources.Count > 0;

        public ReplaceSourceItem? SelectedSource
        {
            get => _selectedSource;
            private set
            {
                if (SetProperty(ref _selectedSource, value))
                {
                    OnPropertyChanged(nameof(IsPicking));
                    OnPropertyChanged(nameof(IsChoosingSource));
                    if (value is null) Filter = string.Empty;
                }
            }
        }

        public bool IsPicking => SelectedSource is not null;

        public bool IsChoosingSource => SelectedSource is null;

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value ?? string.Empty)) ApplyFilter();
            }
        }

        public string Status
        {
            get => _status;
            private set
            {
                if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus));
            }
        }

        public bool HasStatus => !string.IsNullOrEmpty(Status);

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
            }
        }

        public void Refresh()
        {
            RefreshSources();
            if (IsPicking) LoadTargets();
        }

        private void RefreshSources()
        {
            Sources.Clear();
            foreach (var r in _history.GetAll().Where(r => File.Exists(r.FilePath)).OrderByDescending(r => r.DownloadedAtUtc))
            {
                var size = r.SizeBytes > 0 ? $"{r.SizeBytes / 1024.0 / 1024.0:F1} МБ · " : string.Empty;
                Sources.Add(new ReplaceSourceItem(r.FilePath, size + r.DownloadedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")));
            }

            OnPropertyChanged(nameof(HasSources));
        }

        private void BeginPick(ReplaceSourceItem source)
        {
            SelectedSource = source;
            Status = string.Empty;
            LoadTargets();
        }

        private void LoadTargets()
        {
            try
            {
                _allTargets = _replacer.List.Exists
                    ? _replacer.List.Load().Select(s => new ReplaceTargetItem(s)).ToList()
                    : new List<ReplaceTargetItem>();
                if (!_replacer.List.Exists) Status = "Список звуков Soundpad не найден. Запустите Soundpad хотя бы один раз.";
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Cannot read Soundpad sound list");
                _allTargets = new List<ReplaceTargetItem>();
                Status = "Не удалось прочитать список Soundpad: " + ex.Message;
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            Targets.Clear();
            var f = Filter.Trim();
            foreach (var t in _allTargets)
            {
                if (f.Length == 0 || t.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || t.Sound.CategoryText.Contains(f, StringComparison.OrdinalIgnoreCase))
                {
                    Targets.Add(t);
                }
            }
        }

        private async Task ReplaceAsync(ReplaceTargetItem target)
        {
            if (SelectedSource is not { } source || IsBusy) return;
            IsBusy = true;
            try
            {
                var result = await _replacer.ReplaceAsync(target.Sound, source.FilePath);
                Status = result.Message;
                if (result.Success)
                {
                    SelectedSource = null;
                    Status = result.Message;
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
