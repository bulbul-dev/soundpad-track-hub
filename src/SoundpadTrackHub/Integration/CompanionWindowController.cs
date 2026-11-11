using System;
using System.Windows;
using Serilog;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;
using SoundpadTrackHub.Views;

namespace SoundpadTrackHub.Integration
{
    /// <summary>
    /// Поведение companion-панели: стоит рядом с Soundpad, двигается вместе с ним,
    /// прячется при сворачивании/закрытии Soundpad и появляется снова при его запуске.
    /// </summary>
    public sealed class CompanionWindowController : IDisposable
    {
        private readonly MainWindow _window;
        private readonly SoundpadWatcher _watcher;
        private readonly ISettingsService _settings;
        private readonly WindowDocker _docker;
        private readonly ILogger _log = Log.ForContext<CompanionWindowController>();

        private bool _hiddenBySoundpadClosed;

        /// <summary>Если true — закрытие Soundpad не прячет окно (Track Hub сам перезапускает Soundpad).</summary>
        public Func<bool>? SuppressHide { get; set; }
        private bool _hiddenBySoundpadMinimized;

        public CompanionWindowController(MainWindow window, SoundpadWatcher watcher, ISettingsService settings, bool startHiddenUntilSoundpad)
        {
            _window = window;
            _watcher = watcher;
            _settings = settings;
            _docker = new WindowDocker(window, watcher, () => _settings.Current.WindowWidth)
            {
                Enabled = settings.Current.DockToSoundpad,
            };
            _hiddenBySoundpadClosed = startHiddenUntilSoundpad;

            ApplySavedGeometry();

            _watcher.StateChanged += OnSoundpadStateChanged;
            _watcher.WindowMoved += OnSoundpadMoved;
            _watcher.WindowMinimized += OnSoundpadMinimized;
            _watcher.WindowRestored += OnSoundpadRestored;
            _watcher.WindowActivated += OnSoundpadActivated;
            _settings.Changed += OnSettingsChanged;
            _window.ExitSizeMove += OnExitSizeMove;
            _window.IsVisibleChanged += (_, _) =>
            {
                if (_window.IsVisible) Dock();
            };
        }

        /// <summary>true, если окно скрыто, потому что Soundpad закрыт или свёрнут.</summary>
        public bool IsHiddenBySoundpad => _hiddenBySoundpadClosed || _hiddenBySoundpadMinimized;

        public void Start()
        {
            if (!_hiddenBySoundpadClosed)
            {
                _window.Show();
            }

            Dock();
        }

        /// <summary>Пользователь явно попросил показать окно (трей, повторный запуск exe).</summary>
        public void ShowFromUser()
        {
            _hiddenBySoundpadClosed = false;
            _hiddenBySoundpadMinimized = false;
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
            Dock();
        }

        /// <summary>
        /// Пользователь закрыл окно крестиком: прячемся в трей и ждём следующего запуска Soundpad.
        /// </summary>
        public void HideToTray()
        {
            SaveGeometry();
            _hiddenBySoundpadMinimized = false;
            _hiddenBySoundpadClosed = true;
            _window.Hide();
        }

        public void SaveGeometry()
        {
            if (_window.WindowState != WindowState.Normal || _window.ActualWidth <= 0)
            {
                return;
            }

            var width = _window.ActualWidth;
            var height = _window.ActualHeight;
            var left = _window.Left;
            var top = _window.Top;
            var docked = _docker.Enabled && _watcher.State == SoundpadConnectionState.Connected;
            _settings.Update(s =>
            {
                s.WindowWidth = width;
                if (!docked)
                {
                    s.WindowHeight = height;
                    s.WindowLeft = left;
                    s.WindowTop = top;
                }
            });
        }

        private void ApplySavedGeometry()
        {
            var s = _settings.Current;
            _window.Width = s.WindowWidth;
            _window.Height = s.WindowHeight;

            if (s.WindowLeft is { } l && s.WindowTop is { } t && IsOnScreen(l, t, s.WindowWidth))
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Left = l;
                _window.Top = t;
            }
            else
            {
                _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private static bool IsOnScreen(double left, double top, double width) =>
            left + width > SystemParameters.VirtualScreenLeft + 40
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && top >= SystemParameters.VirtualScreenTop - 10
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

        private void Dock()
        {
            if (_docker.Enabled && _window.IsVisible)
            {
                _docker.Apply();
            }
        }

        private void ShowWithoutActivation()
        {
            if (_window.IsVisible)
            {
                return;
            }

            var previous = _window.ShowActivated;
            _window.ShowActivated = false;
            _window.Show();
            _window.ShowActivated = previous;
        }

        private void OnSoundpadStateChanged(object? sender, SoundpadConnectionState state)
        {
            _log.Information("Soundpad state: {State}", state);
            switch (state)
            {
                case SoundpadConnectionState.Connected:
                    if (_hiddenBySoundpadClosed)
                    {
                        _hiddenBySoundpadClosed = false;
                        ShowWithoutActivation();
                    }

                    Dock();
                    break;
                case SoundpadConnectionState.Closed:
                    _hiddenBySoundpadMinimized = false;
                    if (_settings.Current.HideWhenSoundpadClosed && _window.IsVisible && SuppressHide?.Invoke() != true)
                    {
                        SaveGeometry();
                        _hiddenBySoundpadClosed = true;
                        _window.Hide();
                    }

                    break;
            }
        }

        private void OnSoundpadMoved(object? sender, EventArgs e) => Dock();

        private void OnSoundpadMinimized(object? sender, EventArgs e)
        {
            if (_docker.Enabled && _window.IsVisible && _window.WindowState == WindowState.Normal)
            {
                _hiddenBySoundpadMinimized = true;
                _window.Hide();
            }
        }

        private void OnSoundpadRestored(object? sender, EventArgs e)
        {
            if (_hiddenBySoundpadMinimized)
            {
                _hiddenBySoundpadMinimized = false;
                ShowWithoutActivation();
            }

            Dock();
        }

        private void OnSoundpadActivated(object? sender, EventArgs e) => _docker.BringAboveSoundpad();

        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            var enabled = _settings.Current.DockToSoundpad;
            if (_docker.Enabled != enabled)
            {
                _docker.Enabled = enabled;
                if (enabled)
                {
                    Dock();
                }
            }
        }

        private void OnExitSizeMove(object? sender, EventArgs e)
        {
            SaveGeometry();
            Dock();
        }

        public void Dispose()
        {
            _watcher.StateChanged -= OnSoundpadStateChanged;
            _watcher.WindowMoved -= OnSoundpadMoved;
            _watcher.WindowMinimized -= OnSoundpadMinimized;
            _watcher.WindowRestored -= OnSoundpadRestored;
            _watcher.WindowActivated -= OnSoundpadActivated;
            _settings.Changed -= OnSettingsChanged;
            _window.ExitSizeMove -= OnExitSizeMove;
        }
    }
}
