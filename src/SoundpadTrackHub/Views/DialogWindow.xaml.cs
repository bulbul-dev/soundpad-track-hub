using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.Views
{
    public partial class DialogWindow : Window
    {
        public DialogWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => WindowStyling.Apply(this);
        }

        /// <summary>Индекс нажатой кнопки; −1 — окно закрыто крестиком/Esc.</summary>
        public int Result { get; private set; } = -1;

        public void Configure(string title, string message, string? detail, IReadOnlyList<DialogButton> buttons, DialogIcon icon)
        {
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                DetailText.Text = detail;
                DetailText.Visibility = Visibility.Visible;
            }

            var (glyph, fg, bg) = icon switch
            {
                DialogIcon.Warning => ("", "WarningBrush", "WarningSoftBrush"),
                DialogIcon.Danger => ("", "DangerBrush", "DangerSoftBrush"),
                DialogIcon.Success => ("", "SuccessBrush", "SuccessSoftBrush"),
                DialogIcon.Question => ("", "AccentHoverBrush", "AccentSoftBrush"),
                _ => ("", "AccentHoverBrush", "AccentSoftBrush"),
            };
            IconGlyph.Text = glyph;
            IconGlyph.Foreground = (Brush)FindResource(fg);
            IconHost.Background = (Brush)FindResource(bg);

            ButtonsPanel.Children.Clear();
            for (var i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                var index = i;
                var button = new Button
                {
                    Content = b.Text,
                    MinWidth = 96,
                    Margin = new Thickness(6, 0, 0, 0),
                    Style = (Style)FindResource(b.Kind switch
                    {
                        DialogButtonKind.Primary => (object)"PrimaryButton",
                        DialogButtonKind.Danger => "DangerButton",
                        _ => typeof(Button),
                    }),
                };

                // По умолчанию (Enter) — первая не-опасная основная кнопка; опасное действие требует клика.
                if (b.Kind == DialogButtonKind.Primary)
                {
                    button.IsDefault = true;
                }

                button.Click += (_, _) =>
                {
                    Result = index;
                    DialogResult = true;
                };
                ButtonsPanel.Children.Add(button);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Result = -1;
            DialogResult = false;
        }
    }
}
