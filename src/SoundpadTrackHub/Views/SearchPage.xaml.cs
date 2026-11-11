using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoundpadTrackHub.ViewModels;

namespace SoundpadTrackHub.Views
{
    public partial class SearchPage : UserControl
    {
        public SearchPage()
        {
            InitializeComponent();
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                {
                    Dispatcher.BeginInvoke(new System.Action(() => QueryBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
                }
            };
        }

        private void OnQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is SearchViewModel vm && vm.SearchNowCommand.CanExecute(null))
            {
                vm.SearchNowCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
