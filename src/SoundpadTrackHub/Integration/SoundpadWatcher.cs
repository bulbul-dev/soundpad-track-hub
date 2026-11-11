using System;
using System.Windows.Threading;
using Serilog;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Integration
{
    /// <summary>
    /// Следит за Soundpad: появление/закрытие процесса (опрос раз в секунду) и
    /// перемещение/сворачивание окна (SetWinEventHook, WINEVENT_OUTOFCONTEXT —
    /// события доставляются в наш процесс, в Soundpad ничего не внедряется).
    /// Должен создаваться и использоваться в UI-потоке.
    /// </summary>
    public sealed class SoundpadWatcher : IDisposable
    {
        private readonly SoundpadLocator _locator;
        private readonly DispatcherTimer _timer;
        private readonly ILogger _log = Log.ForContext<SoundpadWatcher>();

        // Делегат хранится в поле, иначе GC соберёт его, пока хук активен.
        private readonly NativeMethods.WinEventDelegate _winEventProc;
        private IntPtr _hookLocation;
        private IntPtr _hookMinimize;
        private IntPtr _hookForeground;

        private int _processId;
        private IntPtr _window;
        private bool _wasMinimized;
        private bool _disposed;

        public SoundpadWatcher(SoundpadLocator locator)
        {
            _locator = locator;
            _winEventProc = OnWinEvent;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Poll();
        }

        public SoundpadConnectionState State { get; private set; } = SoundpadConnectionState.Waiting;

        public IntPtr WindowHandle => _window;

        public event EventHandler<SoundpadConnectionState>? StateChanged;

        /// <summary>Окно Soundpad переместилось или изменило размер.</summary>
        public event EventHandler? WindowMoved;

        public event EventHandler? WindowMinimized;

        public event EventHandler? WindowRestored;

        /// <summary>Soundpad стал активным окном.</summary>
        public event EventHandler? WindowActivated;

        public void Start()
        {
            Poll();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            Unhook();
        }

        public bool TryGetWindowBounds(out PixelRect bounds, out PixelRect workArea, out double dpiScale)
        {
            bounds = default;
            workArea = default;
            dpiScale = 1.0;
            if (_window == IntPtr.Zero || !NativeMethods.IsWindow(_window) || NativeMethods.IsIconic(_window))
            {
                return false;
            }

            // Видимые границы без невидимых рамок изменения размера Windows 10/11.
            NativeMethods.RECT r;
            if (NativeMethods.DwmGetWindowAttribute(_window, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out r, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0
                && !NativeMethods.GetWindowRect(_window, out r))
            {
                return false;
            }

            bounds = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
            if (bounds.IsEmpty)
            {
                return false;
            }

            var monitor = NativeMethods.MonitorFromWindow(_window, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            workArea = new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            {
                dpiScale = dpiX / 96.0;
            }

            return true;
        }

        private void Poll()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_processId != 0 && !SoundpadLocator.IsAlive(_processId))
                {
                    _log.Information("Soundpad process {Pid} exited", _processId);
                    _processId = 0;
                    _window = IntPtr.Zero;
                    Unhook();
                    SetState(SoundpadConnectionState.Closed);
                }

                if (_processId == 0)
                {
                    using var process = _locator.FindProcess();
                    if (process is null)
                    {
                        return;
                    }

                    _processId = process.Id;
                    _log.Information("Soundpad process found: pid {Pid}", _processId);
                }

                // Окно может появиться позже процесса (заставка, сворачивание в трей).
                if (_window == IntPtr.Zero || !NativeMethods.IsWindow(_window) || !NativeMethods.IsWindowVisible(_window))
                {
                    var hwnd = _locator.FindMainWindow(_processId);
                    if (hwnd != _window)
                    {
                        _window = hwnd;
                        Unhook();
                        if (hwnd != IntPtr.Zero)
                        {
                            Hook((uint)_processId);
                            WindowMoved?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }

                SetState(SoundpadConnectionState.Connected);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Soundpad poll failed");
            }
        }

        private void Hook(uint pid)
        {
            const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
            _hookLocation = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventProc, pid, 0, flags);
            _hookMinimize = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _winEventProc, pid, 0, flags);
            _hookForeground = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, pid, 0, flags);
            _wasMinimized = NativeMethods.IsIconic(_window);
        }

        private void Unhook()
        {
            foreach (var h in new[] { _hookLocation, _hookMinimize, _hookForeground })
            {
                if (h != IntPtr.Zero)
                {
                    NativeMethods.UnhookWinEvent(h);
                }
            }

            _hookLocation = _hookMinimize = _hookForeground = IntPtr.Zero;
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd != _window || idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF)
            {
                return;
            }

            switch (eventType)
            {
                case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                    var minimized = NativeMethods.IsIconic(_window);
                    if (minimized != _wasMinimized)
                    {
                        _wasMinimized = minimized;
                        (minimized ? WindowMinimized : WindowRestored)?.Invoke(this, EventArgs.Empty);
                    }

                    if (!minimized)
                    {
                        WindowMoved?.Invoke(this, EventArgs.Empty);
                    }

                    break;
                case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                    _wasMinimized = true;
                    WindowMinimized?.Invoke(this, EventArgs.Empty);
                    break;
                case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                    _wasMinimized = false;
                    WindowRestored?.Invoke(this, EventArgs.Empty);
                    WindowMoved?.Invoke(this, EventArgs.Empty);
                    break;
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    WindowActivated?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private void SetState(SoundpadConnectionState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(this, state);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }
    }
}
