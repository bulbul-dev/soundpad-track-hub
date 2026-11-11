using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundpadTrackHub.Views
{
    /// <summary>
    /// Перетаскивание файла из Track Hub в Soundpad (обычный FileDrop, как из Проводника).
    /// </summary>
    internal static class FileDrag
    {
        private static Point _start;
        private static bool _armed;

        public static void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Клики по кнопкам внутри карточки — не начало перетаскивания.
            _armed = !IsInsideButton(e.OriginalSource as DependencyObject);
            _start = e.GetPosition(null);
        }

        public static void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_armed || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement element)
            {
                return;
            }

            var pos = e.GetPosition(null);
            if (System.Math.Abs(pos.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance
                && System.Math.Abs(pos.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _armed = false;
            if (element.Tag is not string path || !File.Exists(path))
            {
                return;
            }

            var data = new DataObject();
            data.SetFileDropList(new StringCollection { path });
            DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);
        }

        private static bool IsInsideButton(DependencyObject? d)
        {
            while (d is not null)
            {
                if (d is ButtonBase)
                {
                    return true;
                }

                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }

            return false;
        }
    }
}
