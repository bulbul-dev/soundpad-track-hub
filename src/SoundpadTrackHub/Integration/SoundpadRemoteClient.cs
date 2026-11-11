using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SoundpadTrackHub.Integration
{
    public sealed record SoundpadCategory(int Index, string Name);

    public sealed class SoundpadRemoteException : Exception
    {
        public SoundpadRemoteException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Официальный интерфейс удалённого управления Soundpad (именованный канал
    /// \\.\pipe\sp_remote_control). Это штатный API Soundpad, не внедрение кода.
    /// Особенность, выясненная на Soundpad 4.0.35: команды Soundpad читает в ANSI-кодировке
    /// локали пользователя (на ru-RU — windows-1251, при системной en-US/1252), а кириллический
    /// путь в UTF-8 даёт «R-204: File does not exist». Ответы приходят в UTF-8.
    /// Поэтому для путей перебираются кодировки, пока Soundpad не найдёт файл.
    /// </summary>
    public sealed class SoundpadRemoteClient
    {
        public const string PipeName = "sp_remote_control";

        static SoundpadRemoteClient()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        private static Encoding? _workingEncoding;

        /// <summary>Кодировки-кандидаты: сработавшая ранее, ANSI локали пользователя, системная ANSI, UTF-8.</summary>
        public static IReadOnlyList<Encoding> CandidateEncodings()
        {
            var pages = new List<int>();
            if (_workingEncoding is not null) pages.Add(_workingEncoding.CodePage);
            pages.Add(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            pages.Add((int)GetACP());
            pages.Add(65001);
            return pages.Distinct().Select(Strict).ToList();
        }

        /// <summary>Основная кодировка запросов (первая из кандидатов).</summary>
        public static Encoding RequestEncoding() => CandidateEncodings()[0];

        private static Encoding Strict(int codePage) => codePage == 65001
            ? new UTF8Encoding(false, throwOnInvalidBytes: true)
            : Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);

        public static bool CanEncode(Encoding encoding, string text)
        {
            try
            {
                encoding.GetBytes(text);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        public Task<string> SendAsync(string command, int timeoutMs = 3000, CancellationToken ct = default) =>
            SendAsync(command, RequestEncoding(), timeoutMs, ct);

        public async Task<string> SendAsync(string command, Encoding encoding, int timeoutMs = 3000, CancellationToken ct = default)
        {
            byte[] request;
            try
            {
                request = encoding.GetBytes(command);
            }
            catch (EncoderFallbackException)
            {
                throw new SoundpadRemoteException("Путь содержит символы, которые Soundpad не может принять. Перетащите файл в Soundpad вручную.");
            }

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                pipe.ReadMode = PipeTransmissionMode.Message;
                await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

                using var ms = new MemoryStream();
                var buffer = new byte[65536];
                do
                {
                    var n = await pipe.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    ms.Write(buffer, 0, n);
                }
                while (!pipe.IsMessageComplete);

                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SoundpadRemoteException("Soundpad не отвечает на команды удалённого управления.");
            }
            catch (IOException ex)
            {
                throw new SoundpadRemoteException("Нет связи с Soundpad: " + ex.Message);
            }
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                var version = await SendAsync("GetVersion()", 1500, ct).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(version) && !version.StartsWith("R-", StringComparison.Ordinal);
            }
            catch (SoundpadRemoteException)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<SoundpadCategory>> GetCategoriesAsync(CancellationToken ct = default)
        {
            var xml = await SendAsync("GetCategories(false, false)", ct: ct).ConfigureAwait(false);
            return ParseCategories(xml);
        }

        public async Task<IReadOnlyList<string>> GetCategorySoundPathsAsync(int categoryIndex, CancellationToken ct = default)
        {
            var xml = await SendAsync($"GetCategory({categoryIndex}, true, false)", ct: ct).ConfigureAwait(false);
            return ParseSoundUrls(xml);
        }

        /// <summary>Находит категорию по имени или создаёт её в корне списка звуков.</summary>
        public async Task<int> EnsureCategoryAsync(string name, CancellationToken ct = default)
        {
            var existing = FindCategory(await GetCategoriesAsync(ct).ConfigureAwait(false), name);
            if (existing is not null)
            {
                return existing.Index;
            }

            EnsureOk(await SendAsync($"DoAddCategory({Quote(name)}, -1)", ct: ct).ConfigureAwait(false));
            var created = FindCategory(await GetCategoriesAsync(ct).ConfigureAwait(false), name)
                          ?? throw new SoundpadRemoteException("Soundpad не создал категорию.");
            return created.Index;
        }

        /// <summary>Добавляет звук в конец категории. Возвращает false, если он там уже есть.</summary>
        public async Task<bool> AddSoundToCategoryAsync(string path, int categoryIndex, CancellationToken ct = default)
        {
            var current = await GetCategorySoundPathsAsync(categoryIndex, ct).ConfigureAwait(false);
            if (current.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var command = $"DoAddSound({Quote(path)}, {categoryIndex}, {current.Count + 1})";
            string? lastResponse = null;
            foreach (var encoding in CandidateEncodings().Where(e => CanEncode(e, command)))
            {
                lastResponse = await SendAsync(command, encoding, ct: ct).ConfigureAwait(false);
                if (lastResponse.StartsWith("R-200", StringComparison.Ordinal))
                {
                    _workingEncoding = encoding;
                    return true;
                }

                if (!lastResponse.StartsWith("R-204", StringComparison.Ordinal))
                {
                    break; // другая ошибка — перебор кодировок не поможет
                }
            }

            throw new SoundpadRemoteException(lastResponse is null
                ? "Путь содержит символы, которые Soundpad не может принять. Перетащите файл в Soundpad вручную."
                : "Soundpad не принял файл: " + lastResponse.Trim());
        }

        public static string Quote(string value)
        {
            if (value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                throw new SoundpadRemoteException("Недопустимые символы в имени.");
            }

            return "\"" + value + "\"";
        }

        public static void EnsureOk(string response)
        {
            if (!response.StartsWith("R-200", StringComparison.Ordinal))
            {
                throw new SoundpadRemoteException("Soundpad ответил: " + response.Trim());
            }
        }

        public static SoundpadCategory? FindCategory(IEnumerable<SoundpadCategory> categories, string name) =>
            categories.FirstOrDefault(c => string.Equals(c.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

        public static IReadOnlyList<SoundpadCategory> ParseCategories(string xml)
        {
            try
            {
                return XDocument.Parse(xml).Descendants("Category")
                    .Where(c => (string?)c.Attribute("type") != "1") // служебная «Все звуки»
                    .Select(c => new SoundpadCategory((int?)c.Attribute("index") ?? -1, (string?)c.Attribute("name") ?? string.Empty))
                    .Where(c => c.Index >= 0)
                    .ToList();
            }
            catch (System.Xml.XmlException)
            {
                throw new SoundpadRemoteException("Не удалось прочитать список категорий Soundpad.");
            }
        }

        public static IReadOnlyList<string> ParseSoundUrls(string xml)
        {
            try
            {
                return XDocument.Parse(xml).Descendants("Sound")
                    .Select(s => (string?)s.Attribute("url"))
                    .Where(u => !string.IsNullOrEmpty(u))
                    .Select(u => u!)
                    .ToList();
            }
            catch (System.Xml.XmlException)
            {
                throw new SoundpadRemoteException("Не удалось прочитать категорию Soundpad.");
            }
        }
    }
}
