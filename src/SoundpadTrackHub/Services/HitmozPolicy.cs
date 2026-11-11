using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services
{
    /// <summary>
    /// Константы сайта. Track Hub работает с Hitmoz только как обычный браузер (WebView2):
    /// никаких API, скрытых запросов и обхода защиты сайта.
    /// </summary>
    public static class Hitmoz
    {
        public const string Host = "ru.hitmoz.org";
        public const string HomeUrl = "https://ru.hitmoz.org/";

        public static bool IsHitmozHttps(Uri? uri) =>
            uri is not null
            && uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            && (uri.IsDefaultPort || uri.Port == 443);

        public static bool IsHitmozHttps(string? uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsHitmozHttps(parsed);
    }

    /// <summary>
    /// Правила загрузки: только HTTPS, только ru.hitmoz.org, только .mp3.
    /// Исполняемые и скриптовые файлы блокируются с отдельным сообщением.
    /// </summary>
    public sealed class HitmozDownloadPolicy
    {
        public const string AllowedExtension = ".mp3";

        /// <summary>Сколько времени после клика по ссылке Hitmoz допускается перенаправление на файл.</summary>
        public static readonly TimeSpan MaxRedirectAge = TimeSpan.FromSeconds(90);

        public static readonly IReadOnlySet<string> BlockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".bat", ".cmd", ".com", ".scr", ".dll", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe",
            ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".msix", ".appx", ".lnk", ".reg", ".hta",
            ".cpl", ".jar", ".pif", ".sys", ".inf", ".scf", ".url", ".application", ".gadget",
            ".iso", ".img", ".vhd", ".vhdx", ".zip", ".rar", ".7z", ".cab", ".sh", ".py", ".ocx", ".drv",
        };

        private static readonly string[] AllowedMimePrefixes = { "audio/" };

        private static readonly HashSet<string> AllowedMimeExact = new(StringComparer.OrdinalIgnoreCase)
        {
            "", "application/octet-stream", "binary/octet-stream", "application/force-download",
            "application/download", "application/x-download",
        };

        public DownloadPolicyResult Evaluate(DownloadRequest request)
        {
            if (!Uri.TryCreate(request.DownloadUri, UriKind.Absolute, out var downloadUri))
            {
                return DownloadPolicyResult.Block("Некорректный адрес загрузки.");
            }

            var host = downloadUri.Host;

            if (downloadUri.Scheme != Uri.UriSchemeHttps)
            {
                return DownloadPolicyResult.Block("Разрешены только HTTPS-загрузки. Загрузка заблокирована.", host: host);
            }

            // 1. Имя файла: предложенное браузером, иначе последний сегмент URL.
            var rawName = !string.IsNullOrWhiteSpace(request.SuggestedFileName)
                ? request.SuggestedFileName
                : downloadUri.Segments.LastOrDefault();
            var safeName = FileNameSanitizer.Sanitize(rawName);

            // 2. Опасные расширения в имени файла или в пути URL — отдельное сообщение.
            var dangerous = FindDangerousExtension(safeName)
                            ?? FindDangerousExtension(FileNameSanitizer.Sanitize(downloadUri.Segments.LastOrDefault()));
            if (dangerous is not null)
            {
                return DownloadPolicyResult.Block(
                    $"Заблокирован потенциально опасный файл ({dangerous}). Track Hub скачивает только MP3.",
                    safeName, host);
            }

            if (string.IsNullOrEmpty(safeName))
            {
                return DownloadPolicyResult.Block("Не удалось определить имя файла.", host: host);
            }

            if (!string.Equals(Path.GetExtension(safeName), AllowedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadPolicyResult.Block("Разрешены только файлы .mp3. Загрузка заблокирована.", safeName, host);
            }

            // 3. MIME: HTML/скрипты/исполняемые — это не MP3.
            if (!IsAllowedMime(request.MimeType))
            {
                return DownloadPolicyResult.Block(
                    $"Сервер прислал не аудиофайл (тип «{request.MimeType}»). Загрузка заблокирована.",
                    safeName, host);
            }

            // 4. Источник: прямой файл с ru.hitmoz.org...
            if (Hitmoz.IsHitmozHttps(downloadUri))
            {
                return new DownloadPolicyResult(true, "Файл с ru.hitmoz.org (HTTPS, MP3).", safeName, host);
            }

            // ...или файл, на который ru.hitmoz.org перенаправил с его собственной ссылки
            // /get/...mp3, по которой пользователь только что кликнул на сайте.
            if (IsValidHitmozSource(request))
            {
                return new DownloadPolicyResult(
                    true,
                    $"Ссылка ru.hitmoz.org, файл отдаёт сервер {host} (HTTPS, MP3).",
                    safeName,
                    host);
            }

            return DownloadPolicyResult.Block(
                $"Загрузка с «{host}» заблокирована: разрешены только файлы, скачанные штатной кнопкой на ru.hitmoz.org.",
                safeName, host);
        }

        public static string? FindDangerousExtension(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            return FileNameSanitizer.AllExtensions(fileName).FirstOrDefault(BlockedExtensions.Contains);
        }

        private static bool IsAllowedMime(string? mime)
        {
            var m = (mime ?? string.Empty).Split(';')[0].Trim();
            return AllowedMimeExact.Contains(m)
                   || AllowedMimePrefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsValidHitmozSource(DownloadRequest request)
        {
            if (!Uri.TryCreate(request.SourceUri, UriKind.Absolute, out var source) || !Hitmoz.IsHitmozHttps(source))
            {
                return false;
            }

            if (!source.AbsolutePath.EndsWith(AllowedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (FindDangerousExtension(FileNameSanitizer.Sanitize(source.Segments.LastOrDefault())) is not null)
            {
                return false;
            }

            if (request.SourceStartedUtc is not { } started)
            {
                return false;
            }

            var age = request.NowUtc - started;
            return age >= TimeSpan.Zero && age <= MaxRedirectAge;
        }
    }

    public enum NavigationVerdict
    {
        Allow,
        UpgradeToHttps,
        BlockExternal,
    }

    /// <summary>
    /// Ограничивает основной фрейм сайтом ru.hitmoz.org. Внешние переходы (реклама)
    /// блокируются; перенаправления, начатые с собственной ссылки Hitmoz на MP3, разрешены,
    /// иначе штатное скачивание не сработало бы.
    /// </summary>
    public static class HitmozNavigationPolicy
    {
        public static NavigationVerdict Evaluate(string uri, bool isRedirected, string? chainStartUri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return NavigationVerdict.BlockExternal;
            }

            if (parsed.Scheme == "about" || parsed.Scheme == "data")
            {
                return parsed.AbsoluteUri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)
                    ? NavigationVerdict.Allow
                    : NavigationVerdict.BlockExternal;
            }

            if (string.Equals(parsed.Host, Hitmoz.Host, StringComparison.OrdinalIgnoreCase))
            {
                return parsed.Scheme == Uri.UriSchemeHttps ? NavigationVerdict.Allow : NavigationVerdict.UpgradeToHttps;
            }

            if (isRedirected
                && parsed.Scheme == Uri.UriSchemeHttps
                && Uri.TryCreate(chainStartUri, UriKind.Absolute, out var start)
                && Hitmoz.IsHitmozHttps(start))
            {
                // Перенаправление с ru.hitmoz.org (например, /get/music/...mp3 → файловый сервер).
                // Если это окажется не MP3, загрузку остановит HitmozDownloadPolicy.
                return NavigationVerdict.Allow;
            }

            return NavigationVerdict.BlockExternal;
        }
    }
}
