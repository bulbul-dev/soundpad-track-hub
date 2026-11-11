using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Services
{
    public static class FileNameSanitizer
    {
        private const int MaxBaseNameLength = 150;

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

        /// <summary>
        /// Делает безопасное имя файла: убирает пути, недопустимые символы,
        /// управляющие символы, зарезервированные имена Windows. Подчёркивания Hitmoz
        /// («ENZRO_-_Ty_ne_bojjsya») заменяются пробелами для читаемости в Soundpad.
        /// </summary>
        public static string Sanitize(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return string.Empty;
            }

            var name = rawName.Trim().Trim('"');

            try
            {
                name = Uri.UnescapeDataString(name);
            }
            catch (UriFormatException)
            {
            }

            // Только последний сегмент: защита от "..\..\file" и "C:\x\file".
            name = name.Replace('/', '\\');
            var slash = name.LastIndexOf('\\');
            if (slash >= 0)
            {
                name = name[(slash + 1)..];
            }

            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsControl(ch) || invalid.Contains(ch))
                {
                    sb.Append(' ');
                }
                else if (ch == '_')
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
            }

            name = MultiSpace.Replace(sb.ToString(), " ").Trim().TrimEnd('.', ' ');

            var ext = Path.GetExtension(name);
            var baseName = Path.GetFileNameWithoutExtension(name).Trim().TrimEnd('.', ' ');
            if (baseName.Length > MaxBaseNameLength)
            {
                baseName = baseName[..MaxBaseNameLength].TrimEnd('.', ' ');
            }

            if (baseName.Length == 0)
            {
                return string.Empty;
            }

            if (ReservedNames.Contains(baseName.Split('.')[0]))
            {
                baseName = "_" + baseName;
            }

            return baseName + ext.ToLowerInvariant();
        }

        /// <summary>Все «расширения» в имени: "a.exe.mp3" → [".exe", ".mp3"].</summary>
        public static IReadOnlyList<string> AllExtensions(string fileName)
        {
            var parts = fileName.Split('.');
            return parts.Length <= 1
                ? Array.Empty<string>()
                : parts.Skip(1).Select(p => "." + p.Trim().ToLowerInvariant()).ToArray();
        }
    }

    public static class TargetPathResolver
    {
        /// <summary>Возвращает свободный путь: "name.mp3" → "name (2).mp3" → "name (3).mp3".</summary>
        public static string GetUniquePath(string folder, string fileName, Func<string, bool>? exists = null)
        {
            exists ??= File.Exists;
            var candidate = Path.Combine(folder, fileName);
            if (!exists(candidate))
            {
                return candidate;
            }

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (var i = 2; i < 10_000; i++)
            {
                candidate = Path.Combine(folder, $"{baseName} ({i}){ext}");
                if (!exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(folder, $"{baseName} ({Guid.NewGuid():N}){ext}");
        }

        /// <summary>
        /// Итоговый путь с учётом решения пользователя. null — сохранять не нужно.
        /// </summary>
        public static string? Resolve(string folder, string fileName, ExistingFileDecision decisionIfExists, Func<string, bool>? exists = null)
        {
            exists ??= File.Exists;
            var direct = Path.Combine(folder, fileName);
            if (!exists(direct))
            {
                return direct;
            }

            return decisionIfExists switch
            {
                ExistingFileDecision.Overwrite => direct,
                ExistingFileDecision.KeepBoth => GetUniquePath(folder, fileName, exists),
                _ => null,
            };
        }

        /// <summary>true, если путь лежит внутри папки (без обхода через "..").</summary>
        public static bool IsInsideFolder(string path, string folder)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
