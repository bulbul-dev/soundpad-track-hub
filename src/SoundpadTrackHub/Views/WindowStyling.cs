using System;
using System.Windows;
using System.Windows.Interop;
using SoundpadTrackHub.Infrastructure;

namespace SoundpadTrackHub.Views
{
    internal static class WindowStyling
    {
        /// <summary>Скруглённые углы и тёмная рамка Windows 11 (на Windows 10 тихо игнорируется).</summary>
        public static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var dark = 1;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                var corner = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                var border = 0x00453A34; // COLORREF 0x00BBGGRR → #343A45
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }
}
