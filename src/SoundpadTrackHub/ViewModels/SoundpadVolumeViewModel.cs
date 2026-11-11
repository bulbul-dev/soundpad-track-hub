using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Integration;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.ViewModels
{
    /// <summary>
    /// Переключатель [Нормально] [Громко] [Громко 2] [Супер громко] — меняет настройку
    /// Soundpad «Нормализация громкости». Системная громкость Windows не меняется.
    /// </summary>
    public sealed class SoundpadVolumeViewModel : ObservableObject
    {
        public const string SuperLoudWarning = "Усиление может вызвать искажения на некоторых треках";

        private readonly SoundpadVolumeController _controller;
        private SoundpadVolumeProfile? _selected;
        private bool _isApplying;

        public SoundpadVolumeViewModel(SoundpadVolumeController controller)
        {
            _controller = controller;
            _controller.Changed += (_, _) => Refresh();
            Refresh();
        }

        public event EventHandler<string>? Message;

        public SoundpadVolumeProfile? SelectedProfile
        {
            get => _selected;
            set
            {
                if (value is not { } profile || profile == _selected || _isApplying)
                {
                    return;
                }

                _ = ApplyAsync(profile);
            }
        }

        public bool IsApplying
        {
            get => _isApplying;
            private set => SetProperty(ref _isApplying, value);
        }

        public bool IsSuperLoud => _selected == SoundpadVolumeProfile.SuperLoud;

        public string Caption => SuperLoudWarning;

        public string CurrentText
        {
            get
            {
                var current = _controller.ReadCurrent();
                if (current is null)
                {
                    return "Настройки Soundpad не найдены — запустите Soundpad хотя бы один раз.";
                }

                var name = SoundpadVolumeProfileExtensions.Match(current)?.DisplayName() ?? "свои настройки";
                return $"Сейчас в Soundpad: {name} ({current.Describe()}).";
            }
        }

        public string? PendingText => _controller.PendingProfile is { } p
            ? $"Режим «{p.DisplayName()}» применится, когда Soundpad будет закрыт."
            : null;

        public void Refresh()
        {
            _selected = _controller.PendingProfile ?? _controller.CurrentProfile;

            // Откладываем уведомление: если сеттер вызвала сама привязка RadioButton,
            // WPF проигнорирует изменение источника внутри собственного обновления.
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(() =>
            {
                OnPropertyChanged(nameof(SelectedProfile));
                OnPropertyChanged(nameof(IsSuperLoud));
                OnPropertyChanged(nameof(CurrentText));
                OnPropertyChanged(nameof(PendingText));
            }));
        }

        private async Task ApplyAsync(SoundpadVolumeProfile profile)
        {
            IsApplying = true;
            try
            {
                var result = await _controller.ApplyAsync(profile);
                Message?.Invoke(this, result.Message);
            }
            finally
            {
                IsApplying = false;
                Refresh();
            }
        }
    }
}
