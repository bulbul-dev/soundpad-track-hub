using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.Views
{
    public sealed class DialogService : IDialogService
    {
        public int Choose(string title, string message, string? detail, IReadOnlyList<DialogButton> buttons, DialogIcon icon = DialogIcon.Question)
        {
            var dialog = new DialogWindow();
            dialog.Configure(title, message, detail, buttons, icon);

            var owner = Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsVisible && w is not DialogWindow && w.WindowState != WindowState.Minimized);
            if (owner is not null)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Topmost = true;
                dialog.ShowInTaskbar = true;
            }

            dialog.ShowDialog();
            return dialog.Result;
        }

        public bool Confirm(string title, string message, string? detail, string confirmText, string cancelText, bool danger = false)
        {
            // Безопасный вариант — «Отмена» — стоит последним; подтверждение опасного действия Enter не срабатывает.
            var buttons = new[]
            {
                new DialogButton(confirmText, danger ? DialogButtonKind.Danger : DialogButtonKind.Primary),
                new DialogButton(cancelText, DialogButtonKind.Secondary),
            };
            return Choose(title, message, detail, buttons, danger ? DialogIcon.Danger : DialogIcon.Question) == 0;
        }

        public bool ShowAgreement()
        {
            var window = new AgreementWindow();
            var owner = Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsVisible && w is not DialogWindow && w is not AgreementWindow && w.WindowState != WindowState.Minimized);
            if (owner is not null)
            {
                window.Owner = owner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Topmost = true;
                window.ShowInTaskbar = true;
            }

            window.ShowDialog();
            return window.Accepted;
        }

        public void Info(string title, string message, string? detail = null, DialogIcon icon = DialogIcon.Info)
        {
            Choose(title, message, detail, new[] { new DialogButton("OK", DialogButtonKind.Primary) }, icon);
        }
    }
}
