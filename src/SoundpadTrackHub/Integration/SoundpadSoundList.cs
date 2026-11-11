using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SoundpadTrackHub.Integration
{
    /// <summary>Звук из списка Soundpad (soundlist.spl). Index — номер с 1, как в Soundpad.</summary>
    public sealed record SoundpadSound(int Index, string Url, string Artist, string Title, string Duration, IReadOnlyList<string> Categories)
    {
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Title)) return $"{Artist} - {Title}";
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                return Path.GetFileNameWithoutExtension(Url);
            }
        }

        public string CategoryText => Categories.Count == 0 ? "Без категории" : string.Join(", ", Categories);
    }

    /// <summary>Исполнитель, название и длительность нового трека для записи в Soundpad.</summary>
    public sealed record SoundpadTrackInfo(string FilePath, string Artist, string Title, string Duration)
    {
        private static readonly Regex TrailingId = new(@"\s+\d{5,}$", RegexOptions.Compiled);

        /// <summary>
        /// «Artist - Title 79416996.mp3» → Artist / Title (числовой id Hitmoz в конце убирается).
        /// Если дефиса нет — всё имя файла считается названием.
        /// </summary>
        public static SoundpadTrackInfo FromFileName(string filePath, TimeSpan? length)
        {
            var name = TrailingId.Replace(Path.GetFileNameWithoutExtension(filePath).Trim(), string.Empty).Trim();
            string artist = string.Empty, title = name;
            var dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                artist = name[..dash].Trim();
                title = name[(dash + 3)..].Trim();
            }

            return new SoundpadTrackInfo(filePath, artist, title, FormatDuration(length));
        }

        public static string FormatDuration(TimeSpan? length)
        {
            if (length is not { } l || l <= TimeSpan.Zero) return string.Empty;
            return l.TotalHours >= 1 ? $"{(int)l.TotalHours}:{l.Minutes:00}:{l.Seconds:00}" : $"{(int)l.TotalMinutes}:{l.Seconds:00}";
        }
    }

    /// <summary>
    /// Чтение и замена звуков в списке Soundpad (%APPDATA%\Leppsoft\soundlist.spl).
    /// Работает и в бесплатной (демо) версии: не нужен канал удалённого управления.
    /// Файл можно менять только когда Soundpad закрыт — при выходе Soundpad перезаписывает его.
    /// Перед каждой заменой делается резервная копия.
    /// </summary>
    public sealed class SoundpadSoundList
    {
        public const int BackupsToKeep = 15;

        public SoundpadSoundList(string listPath, string backupDirectory)
        {
            ListPath = listPath;
            BackupDirectory = backupDirectory;
        }

        public static string DefaultListPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leppsoft", "soundlist.spl");

        public string ListPath { get; }

        public string BackupDirectory { get; }

        public bool Exists => File.Exists(ListPath);

        public IReadOnlyList<SoundpadSound> Load()
        {
            var doc = XDocument.Load(ListPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root ?? throw new InvalidDataException("Пустой список Soundpad.");
            var categories = new Dictionary<int, List<string>>();
            foreach (var category in root.Descendants("Category"))
            {
                var name = (string?)category.Attribute("name") ?? string.Empty;
                foreach (var s in category.Elements("Sound"))
                {
                    // В категориях Soundpad звуки указаны по номеру с нуля (id="0" — первый звук списка).
                    if (int.TryParse((string?)s.Attribute("id"), out var zeroBased))
                    {
                        var id = zeroBased + 1;
                        if (!categories.TryGetValue(id, out var list)) categories[id] = list = new List<string>();
                        if (!list.Contains(name)) list.Add(name);
                    }
                }
            }

            var result = new List<SoundpadSound>();
            var index = 0;
            foreach (var e in root.Elements("Sound"))
            {
                index++;
                result.Add(new SoundpadSound(index,
                    (string?)e.Attribute("url") ?? string.Empty,
                    (string?)e.Attribute("artist") ?? string.Empty,
                    (string?)e.Attribute("title") ?? string.Empty,
                    (string?)e.Attribute("duration") ?? string.Empty,
                    categories.TryGetValue(index, out var c) ? c : (IReadOnlyList<string>)Array.Empty<string>()));
            }

            return result;
        }

        /// <summary>
        /// Меняет файл звука №index на новый трек. Номер, категории и горячие клавиши звука сохраняются.
        /// Возвращает путь резервной копии.
        /// </summary>
        public string Replace(int index, SoundpadTrackInfo track, string? expectedOldUrl = null)
        {
            if (!File.Exists(track.FilePath)) throw new FileNotFoundException("Файл трека не найден.", track.FilePath);

            var doc = XDocument.Load(ListPath, LoadOptions.PreserveWhitespace);
            var sounds = doc.Root?.Elements("Sound").ToList() ?? new List<XElement>();
            if (index < 1 || index > sounds.Count) throw new ArgumentOutOfRangeException(nameof(index), "Такого звука в Soundpad нет.");

            var e = sounds[index - 1];
            if (expectedOldUrl is not null && !string.Equals((string?)e.Attribute("url"), expectedOldUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Список Soundpad изменился. Обновите список и выберите звук снова.");
            }

            var backup = MakeBackup();
            e.SetAttributeValue("url", track.FilePath);
            e.SetAttributeValue("artist", string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist);
            e.SetAttributeValue("title", track.Title);
            e.SetAttributeValue("duration", string.IsNullOrWhiteSpace(track.Duration) ? null : track.Duration);
            // hash — отпечаток старого файла; без него Soundpad просто посчитает его заново.
            e.SetAttributeValue("hash", null);

            var tmp = ListPath + ".trackhub.tmp";
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None };
            using (var w = XmlWriter.Create(tmp, settings))
            {
                doc.Save(w);
            }

            File.Copy(tmp, ListPath, overwrite: true);
            File.Delete(tmp);
            return backup;
        }

        private string MakeBackup()
        {
            Directory.CreateDirectory(BackupDirectory);
            var path = Path.Combine(BackupDirectory, $"soundlist-{DateTime.Now:yyyyMMdd-HHmmss-fff}.spl");
            File.Copy(ListPath, path, overwrite: false);
            foreach (var old in new DirectoryInfo(BackupDirectory).GetFiles("soundlist-*.spl")
                         .OrderByDescending(f => f.Name).Skip(BackupsToKeep))
            {
                try { old.Delete(); } catch (IOException) { }
            }

            return path;
        }
    }
}
