using System;
using System.Windows;
using System.Windows.Interop;
using Serilog;
using SoundpadTrackHub.Infrastructure;

namespace SoundpadTrackHub.Integration
{
    /// <summary>
    /// Ставит окно Track Hub рядом с Soundpad. Двигается только наше окно —
    /// окно Soundpad не изменяется.
    /// </summary>
    public sealed class WindowDocker
    {
        public const int GapDip = 6;

        private readonly Window _window;
        private readonly SoundpadWatcher _watcher;
        private readonly Func<double> _widthDip;
        private readonly ILogger _log = Log.ForContext<WindowDocker>();

        public WindowDocker(Window window, SoundpadWatcher watcher, Func<double> widthDip)
        {
            _window = window;
            _watcher = watcher;
            _widthDip = widthDip;
        }

        public bool Enabled { get; set; } = true;

        public DockSide? LastSide { get; private set; }

        /// <summary>true, пока мы сами двигаем окно (чтобы не принять это за действие пользователя).</summary>
        public bool IsPositioning { get; private set; }

        public bool Apply()
        {
            if (!Enabled || !_window.IsVisible || _window.WindowState != WindowState.Normal)
            {
                return false;
            }

            if (!_watcher.TryGetWindowBounds(out var sp, out var work, out var scale))
            {
                return false;
            }

            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var width = (int)Math.Round(_widthDip() * scale);
            var minHeight = (int)Math.Round(Models.AppSettings.MinHeight * scale);
            var gap = (int)Math.Round(GapDip * scale);
            var result = DockPlacementCalculator.Calculate(sp, work, width, minHeight, gap);
            LastSide = result.Side;

            try
            {
                IsPositioning = true;
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    result.Bounds.Left, result.Bounds.Top, result.Bounds.Width, result.Bounds.Height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "SetWindowPos failed");
                return false;
            }
            finally
            {
                IsPositioning = false;
            }

            return true;
        }

        /// <summary>Поднимает Track Hub над Soundpad без перехвата фокуса.</summary>
        public void BringAboveSoundpad()
        {
            if (!Enabled || !_window.IsVisible)
            {
                return;
            }

            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }
    }
}
