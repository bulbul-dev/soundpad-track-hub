using System.Windows.Controls;
using System.Windows.Input;

namespace SoundpadTrackHub.Views
{
    public partial class DownloadsPage : UserControl
    {
        public DownloadsPage()
        {
            InitializeComponent();
        }

        private void OnItemMouseDown(object sender, MouseButtonEventArgs e) => FileDrag.OnMouseDown(sender, e);

        private void OnItemMouseMove(object sender, MouseEventArgs e) => FileDrag.OnMouseMove(sender, e);
    }
}
