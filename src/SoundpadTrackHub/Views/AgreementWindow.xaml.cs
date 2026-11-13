using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SoundpadTrackHub.Views
{
    /// <summary>Пользовательское соглашение: показывается перед первой загрузкой трека.</summary>
    public partial class AgreementWindow : Window
    {
        public AgreementWindow()
        {
            InitializeComponent();
            AgreementText.Text = LoadText();
            Loaded += (_, _) => WindowStyling.Apply(this);
        }

        public bool Accepted { get; private set; }

        /// <summary>Текст соглашения встроен в программу (тот же файл, что показывает установщик).</summary>
        public static string LoadText()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundpadTrackHub.Agreement.txt");
            if (stream is null)
            {
                return "Текст соглашения не найден.";
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().Trim();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
        }

        private void OnAcceptChanged(object sender, RoutedEventArgs e) => AcceptButton.IsEnabled = AcceptBox.IsChecked == true;

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            Accepted = AcceptBox.IsChecked == true;
            DialogResult = Accepted;
        }

        private void OnDecline(object sender, RoutedEventArgs e)
        {
            Accepted = false;
            DialogResult = false;
        }
    }
}
