using System.Collections.Generic;

namespace SoundpadTrackHub.Services
{
    public enum DialogButtonKind
    {
        Primary,
        Secondary,
        Danger,
    }

    public sealed record DialogButton(string Text, DialogButtonKind Kind = DialogButtonKind.Secondary);

    public enum DialogIcon
    {
        Info,
        Question,
        Warning,
        Danger,
        Success,
    }

    /// <summary>Модальные диалоги в стиле приложения (тёмная тема).</summary>
    public interface IDialogService
    {
        /// <summary>Возвращает индекс нажатой кнопки или −1, если окно закрыто.</summary>
        int Choose(string title, string message, string? detail, IReadOnlyList<DialogButton> buttons, DialogIcon icon = DialogIcon.Question);

        bool Confirm(string title, string message, string? detail, string confirmText, string cancelText, bool danger = false);

        void Info(string title, string message, string? detail = null, DialogIcon icon = DialogIcon.Info);
    }
}
