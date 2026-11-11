using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
using SoundpadTrackHub.Infrastructure;
using SoundpadTrackHub.ViewModels;

namespace SoundpadTrackHub.Views
{
    public partial class MainWindow : Window
    {
        private const int WM_EXITSIZEMOVE = 0x0232;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            SourceInitialized += OnSourceInitialized;
        }

        /// <summary>Пользователь закончил перемещение/изменение размера окна.</summary>
        public event EventHandler? ExitSizeMove;

        public WebView2 HitmozWebView => WebView;

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            WindowStyling.Apply(this);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                // Компактная панель не разворачивается на весь экран (в т.ч. двойным кликом по заголовку).
                case NativeMethods.WM_SYSCOMMAND when (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_MAXIMIZE:
                case NativeMethods.WM_NCLBUTTONDBLCLK:
                    handled = true;
                    break;
                case WM_EXITSIZEMOVE:
                    ExitSizeMove?.Invoke(this, EventArgs.Empty);
                    break;
            }

            return IntPtr.Zero;
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void OnFileDragStart(object sender, MouseButtonEventArgs e) => FileDrag.OnMouseDown(sender, e);

        private void OnFileDragMove(object sender, MouseEventArgs e) => FileDrag.OnMouseMove(sender, e);
    }
}
