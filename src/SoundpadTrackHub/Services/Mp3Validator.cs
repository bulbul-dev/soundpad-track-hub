using System;
using System.IO;

namespace SoundpadTrackHub.Services
{
    /// <summary>
    /// Проверка, что скачанный файл действительно MP3 (а не HTML-страница с ошибкой
    /// или переименованный исполняемый файл).
    /// </summary>
    public static class Mp3Validator
    {
        public const long MinimumSizeBytes = 4 * 1024;

        public static bool LooksLikeMp3(string path, out string reason)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    reason = "Файл не найден.";
                    return false;
                }

                if (info.Length < MinimumSizeBytes)
                {
                    reason = "Файл слишком маленький для MP3.";
                    return false;
                }

                Span<byte> head = stackalloc byte[16];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var read = fs.Read(head);
                    if (read < 4)
                    {
                        reason = "Файл повреждён.";
                        return false;
                    }
                }

                return LooksLikeMp3Header(head, out reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = "Не удалось прочитать файл: " + ex.Message;
                return false;
            }
        }

        public static bool LooksLikeMp3Header(ReadOnlySpan<byte> head, out string reason)
        {
            // "MZ" — исполняемый файл Windows.
            if (head.Length >= 2 && head[0] == 'M' && head[1] == 'Z')
            {
                reason = "Это исполняемый файл, а не MP3.";
                return false;
            }

            // ID3v2-тег.
            if (head.Length >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                reason = string.Empty;
                return true;
            }

            // MPEG audio frame sync: 11 единичных бит, layer != 00.
            if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0)
            {
                reason = string.Empty;
                return true;
            }

            if (head.Length >= 1 && (head[0] == '<' || head[0] == '{'))
            {
                reason = "Сервер вернул веб-страницу вместо MP3.";
                return false;
            }

            reason = "Содержимое файла не похоже на MP3.";
            return false;
        }
    }
}
