using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using SoundpadTrackHub.Models;
using SoundpadTrackHub.Services;

namespace SoundpadTrackHub.Integration
{
    public sealed record HitmozTrack(string Id, string Artist, string Title, string Duration, string DownloadUrl);

    public sealed class HitmozSearchException : Exception
    {
        public HitmozSearchException(string message) : base(message)
        {
        }
    }

    /// <summary>Поиск по сайту Hitmoz через встроенный браузер (вкладка «Поиск»).</summary>
    public interface IHitmozSearchHost
    {
        Task<IReadOnlyList<HitmozTrack>> SearchAsync(string query, CancellationToken ct);

        /// <summary>Нажимает трек в плеере сайта. Возвращает true, если трек играет.</summary>
        Task<bool> TogglePlayAsync(string trackId);

        Task StopPlaybackAsync();

        /// <summary>Открывает штатную ссылку скачивания (дальше — обычное подтверждение).</summary>
        bool StartDownload(string downloadUrl);
    }

    public interface IBrowserHost
    {
        void GoHome();

        void GoBack();

        void Reload();

        Task RetryAsync();

        bool HasActiveDownloads { get; }

        void CancelDownloads();
    }

    /// <summary>
    /// Встроенный браузер Hitmoz. Пользователь сам ищет треки и нажимает штатную кнопку
    /// скачивания на сайте; Track Hub только перехватывает обычную загрузку MP3,
    /// проверяет её по правилам и спрашивает подтверждение. Сайт никак не обходится:
    /// если он отвечает 403/недоступен — показывается честный экран ошибки.
    /// </summary>
    public sealed class HitmozWebViewController : IBrowserHost, IHitmozSearchHost, IDisposable
    {
        private sealed class PendingSearch
        {
            public PendingSearch(string url) => Url = url;

            public string Url { get; }

            public ulong? NavigationId { get; set; }

            public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Читает список треков со страницы поиска, которую загрузил браузер: data-musmeta
        /// (исполнитель, название), длительность и штатную ссылку скачивания.
        /// </summary>
        internal const string ExtractTracksScript = @"(() => {
  const out = [];
  document.querySelectorAll('li.tracks__item').forEach(li => {
    if (out.length >= 60) return;
    let meta = {};
    try { meta = JSON.parse(li.getAttribute('data-musmeta') || '{}'); } catch (e) {}
    const dl = li.querySelector('a.track__download-btn[href]');
    const time = li.querySelector('.track__fulltime');
    const title = li.querySelector('.track__title');
    const desc = li.querySelector('.track__desc');
    out.push({
      id: li.getAttribute('data-musid') || meta.id || '',
      artist: meta.artist || (desc ? desc.textContent.trim() : ''),
      title: meta.title || (title ? title.textContent.trim() : ''),
      duration: time ? time.textContent.trim() : '',
      download: dl ? dl.href : ''
    });
  });
  return out;
})()";

        private PendingSearch? _search;
        private sealed class ActiveDownload
        {
            public required CoreWebView2DownloadOperation Operation { get; init; }
            public required string Id { get; init; }
            public required string FileName { get; init; }
            public required string Folder { get; init; }
            public required ExistingFileDecision Decision { get; init; }
            public required string TempPath { get; init; }
            public required string SourceUrl { get; init; }
            public bool Finished { get; set; }
        }

        private readonly WebView2 _webView;
        private readonly HitmozDownloadPolicy _policy;
        private readonly IDialogService _dialogs;
        private readonly TempFileManager _temp;
        private readonly DownloadHistoryService _history;
        private readonly ISettingsService _settings;
        private readonly string _userDataFolder;
        private readonly ILogger _log = Log.ForContext<HitmozWebViewController>();
        private readonly List<ActiveDownload> _downloads = new();
        private readonly HashSet<ulong> _ignoredNavigationIds = new();

        private CoreWebView2? _core;
        private string? _chainStartUri;
        private DateTime? _chainStartUtc;
        private bool _initializing;
        private bool _disposed;

        public HitmozWebViewController(
            WebView2 webView,
            HitmozDownloadPolicy policy,
            IDialogService dialogs,
            TempFileManager temp,
            DownloadHistoryService history,
            ISettingsService settings,
            string userDataFolder)
        {
            _webView = webView;
            _policy = policy;
            _dialogs = dialogs;
            _temp = temp;
            _history = history;
            _settings = settings;
            _userDataFolder = userDataFolder;
        }

        public event EventHandler<(BrowserState State, string? Detail)>? StateChanged;

        /// <summary>Короткие сообщения для статусной строки.</summary>
        public event EventHandler<string>? Message;

        public event EventHandler<DownloadProgressInfo>? DownloadProgress;

        public event EventHandler<DownloadRecord>? DownloadCompleted;

        public BrowserState State { get; private set; } = BrowserState.NotStarted;

        public bool HasActiveDownloads => _downloads.Any(d => !d.Finished);

        /// <summary>Проверка наличия WebView2 Runtime без создания браузера.</summary>
        public static string? GetRuntimeVersion()
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebView2 runtime probe failed");
                return null;
            }
        }

        public async Task InitializeAsync()
        {
            if (_core is not null || _initializing || _disposed)
            {
                return;
            }

            var version = GetRuntimeVersion();
            if (version is null)
            {
                SetState(BrowserState.RuntimeMissing, null);
                return;
            }

            _initializing = true;
            SetState(BrowserState.Initializing, null);
            try
            {
                Directory.CreateDirectory(_userDataFolder);
                var options = new CoreWebView2EnvironmentOptions { Language = "ru-RU" };
                var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options);
                await _webView.EnsureCoreWebView2Async(env);
                _core = _webView.CoreWebView2;
                _log.Information("WebView2 {Version} initialized", version);

                var s = _core.Settings;
                s.IsStatusBarEnabled = false;
                s.AreHostObjectsAllowed = false;
                s.IsWebMessageEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsGeneralAutofillEnabled = false;
                s.IsZoomControlEnabled = true;
#if DEBUG
                s.AreDevToolsEnabled = true;
#else
                s.AreDevToolsEnabled = false;
#endif

                _core.NavigationStarting += OnNavigationStarting;
                _core.NavigationCompleted += OnNavigationCompleted;
                _core.NewWindowRequested += OnNewWindowRequested;
                _core.DownloadStarting += OnDownloadStarting;
                _core.ProcessFailed += OnProcessFailed;

                SetState(BrowserState.Loading, null);
                _core.Navigate(Hitmoz.HomeUrl);
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                _log.Warning(ex, "WebView2 runtime not found");
                SetState(BrowserState.RuntimeMissing, null);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                _log.Error(ex, "WebView2 initialization failed");
                SetState(BrowserState.Unavailable, "Не удалось запустить встроенный браузер WebView2: " + ex.Message);
            }
            finally
            {
                _initializing = false;
            }
        }

        public void GoHome()
        {
            if (_core is null)
            {
                _ = InitializeAsync();
                return;
            }

            SetState(BrowserState.Loading, null);
            _core.Navigate(Hitmoz.HomeUrl);
        }

        public void GoBack()
        {
            if (_core?.CanGoBack == true)
            {
                _core.GoBack();
            }
        }

        public void Reload()
        {
            if (_core is null)
            {
                _ = InitializeAsync();
                return;
            }

            if (State is BrowserState.Ready or BrowserState.Loading)
            {
                _core.Reload();
            }
            else
            {
                GoHome();
            }
        }

        public async Task RetryAsync()
        {
            if (_core is null)
            {
                await InitializeAsync();
            }
            else
            {
                GoHome();
            }
        }

        public void CancelDownloads()
        {
            foreach (var d in _downloads.Where(d => !d.Finished).ToList())
            {
                try
                {
                    d.Operation.Cancel();
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Cancel download failed");
                }

                d.Finished = true;
                _temp.Delete(d.TempPath);
                DownloadProgress?.Invoke(this, new DownloadProgressInfo(d.Id, d.FileName, 0, null, true));
            }
        }

        // ---------------- Поиск ----------------

        public async Task<IReadOnlyList<HitmozTrack>> SearchAsync(string query, CancellationToken ct)
        {
            if (_core is null)
            {
                await InitializeAsync();
            }

            if (_core is null)
            {
                throw new HitmozSearchException(State == BrowserState.RuntimeMissing
                    ? "Не установлен WebView2 Runtime — поиск недоступен."
                    : "Встроенный браузер не запущен.");
            }

            var url = Hitmoz.HomeUrl + "search?q=" + Uri.EscapeDataString(query.Trim());
            _search?.Completion.TrySetCanceled();
            var pending = new PendingSearch(url);
            _search = pending;

            using (ct.Register(() => pending.Completion.TrySetCanceled()))
            {
                _core.Navigate(url);
                var finished = await Task.WhenAny(pending.Completion.Task, Task.Delay(TimeSpan.FromSeconds(25), ct));
                ct.ThrowIfCancellationRequested();
                if (finished != pending.Completion.Task)
                {
                    throw new HitmozSearchException("Hitmoz не ответил вовремя. Попробуйте ещё раз.");
                }

                if (!await pending.Completion.Task)
                {
                    throw new HitmozSearchException(State == BrowserState.Forbidden
                        ? "Hitmoz ответил HTTP 403. Откройте вкладку «Сайт» — возможно, там нужна проверка, которую можно пройти вручную."
                        : "Сайт Hitmoz недоступен. Проверьте интернет и попробуйте ещё раз.");
                }
            }

            var json = await _core.ExecuteScriptAsync(ExtractTracksScript);
            ct.ThrowIfCancellationRequested();
            return ParseTracks(json);
        }

        internal static IReadOnlyList<HitmozTrack> ParseTracks(string json)
        {
            var result = new List<HitmozTrack>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string Get(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;
                    var id = Get("id");
                    var download = Get("download");
                    if (!IsSafeTrackId(id))
                    {
                        continue;
                    }

                    // Скачивать можно только штатные MP3-ссылки ru.hitmoz.org.
                    if (!IsDownloadLink(download))
                    {
                        download = string.Empty;
                    }

                    result.Add(new HitmozTrack(id, Get("artist"), Get("title"), Get("duration"), download));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                throw new HitmozSearchException("Не удалось прочитать результаты поиска — возможно, сайт изменил разметку.");
            }

            return result;
        }

        public async Task<bool> TogglePlayAsync(string trackId)
        {
            if (_core is null || !IsSafeTrackId(trackId))
            {
                return false;
            }

            var find = "document.querySelector('li.tracks__item[data-musid=\"" + trackId + "\"]')";
            var clicked = await _core.ExecuteScriptAsync("(() => { const li = " + find + "; if (!li) return false; li.click(); return true; })()");
            if (clicked != "true")
            {
                return false;
            }

            await Task.Delay(450);
            var state = await _core.ExecuteScriptAsync("(() => { const li = " + find + "; if (!li) return false; const c = li.className; return c.includes('mustoggler_playing') && !c.includes('mustoggler_paused'); })()");
            return state == "true";
        }

        public async Task StopPlaybackAsync()
        {
            if (_core is not null)
            {
                await _core.ExecuteScriptAsync("document.querySelectorAll('audio,video').forEach(m => m.pause()); true");
            }
        }

        public bool StartDownload(string downloadUrl)
        {
            if (_core is null || !IsDownloadLink(downloadUrl))
            {
                return false;
            }

            _core.Navigate(downloadUrl);
            return true;
        }

        internal static bool IsSafeTrackId(string id) =>
            id.Length is > 0 and < 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

        private static bool IsSameUrl(string a, string b) =>
            Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub)
            && string.Equals(ua.AbsoluteUri, ub.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

        // ---------------- Навигация ----------------

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!e.IsRedirected)
            {
                _chainStartUri = e.Uri;
                _chainStartUtc = DateTime.UtcNow;

                if (_search is { NavigationId: null } search && IsSameUrl(e.Uri, search.Url))
                {
                    search.NavigationId = e.NavigationId;
                }
            }

            var verdict = HitmozNavigationPolicy.Evaluate(e.Uri, e.IsRedirected, _chainStartUri);
            switch (verdict)
            {
                case NavigationVerdict.UpgradeToHttps:
                    e.Cancel = true;
                    _ignoredNavigationIds.Add(e.NavigationId);
                    var https = new UriBuilder(e.Uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
                    _core?.Navigate(https);
                    return;
                case NavigationVerdict.BlockExternal:
                    e.Cancel = true;
                    _ignoredNavigationIds.Add(e.NavigationId);
                    var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Host : e.Uri;
                    _log.Information("Blocked external navigation to {Uri}", e.Uri);
                    Message?.Invoke(this, $"Переход на внешний сайт заблокирован: {host}");
                    return;
            }

            if (IsDownloadLink(_chainStartUri))
            {
                // Клик по кнопке скачивания: страница не меняется, ошибки этой навигации не показываем.
                _ignoredNavigationIds.Add(e.NavigationId);
                return;
            }

            SetState(BrowserState.Loading, null);
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_search is { } search && search.NavigationId == e.NavigationId)
            {
                _search = null;
                search.Completion.TrySetResult(e.IsSuccess && e.HttpStatusCode is 0 or (>= 200 and < 400));
            }
            if (_ignoredNavigationIds.Remove(e.NavigationId))
            {
                if (State == BrowserState.Loading)
                {
                    SetState(BrowserState.Ready, null);
                }

                return;
            }

            var status = e.HttpStatusCode;
            if (status == 403)
            {
                _log.Warning("Hitmoz responded HTTP 403");
                SetState(BrowserState.Forbidden, null);
                return;
            }

            if (e.IsSuccess)
            {
                SetState(BrowserState.Ready, null);
                return;
            }

            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                // Навигацию отменили (новый переход, наша блокировка) — это не ошибка сайта.
                if (State == BrowserState.Loading)
                {
                    SetState(BrowserState.Ready, null);
                }

                return;
            }

            if (status >= 400)
            {
                SetState(BrowserState.Unavailable, $"Hitmoz ответил HTTP {status}.");
                return;
            }

            _log.Warning("Navigation failed: {Status}", e.WebErrorStatus);
            SetState(BrowserState.Unavailable, DescribeError(e.WebErrorStatus));
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (Hitmoz.IsHitmozHttps(e.Uri))
            {
                _core?.Navigate(e.Uri);
                return;
            }

            var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Host : "неизвестный адрес";
            _log.Information("Blocked popup {Uri}", e.Uri);
            Message?.Invoke(this, $"Всплывающее окно заблокировано: {host}");
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _log.Error("WebView2 process failed: {Kind} {Reason}", e.ProcessFailedKind, e.Reason);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                SetState(BrowserState.Unavailable, "Процесс встроенного браузера WebView2 завершился. Перезапустите Track Hub.");
            }
            else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                Message?.Invoke(this, "Страница перестала отвечать и была перезагружена.");
                _core?.Reload();
            }
        }

        // ---------------- Загрузки ----------------

        private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // Диалоги показываем вне обработчика события, удерживая deferral.
            var deferral = e.GetDeferral();
            _webView.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    HandleDownloadStarting(e);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Download handling failed");
                    e.Cancel = true;
                    Message?.Invoke(this, "Ошибка при обработке загрузки — загрузка отменена.");
                }
                finally
                {
                    deferral.Complete();
                }
            }));
        }

        private void HandleDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
        {
            e.Handled = true; // скрываем стандартную панель загрузок Edge
            var op = e.DownloadOperation;
            var request = new DownloadRequest
            {
                DownloadUri = op.Uri,
                SuggestedFileName = Path.GetFileName(e.ResultFilePath),
                MimeType = op.MimeType,
                SourceUri = _chainStartUri,
                SourceStartedUtc = _chainStartUtc,
                NowUtc = DateTime.UtcNow,
            };

            var verdict = _policy.Evaluate(request);
            _log.Information("Download {Uri} (source {Source}, mime {Mime}) → {Allowed}: {Reason}",
                op.Uri, request.SourceUri, op.MimeType, verdict.IsAllowed, verdict.Reason);

            if (!verdict.IsAllowed)
            {
                e.Cancel = true;
                Message?.Invoke(this, verdict.Reason);
                if (HitmozDownloadPolicy.FindDangerousExtension(verdict.SafeFileName) is not null
                    || HitmozDownloadPolicy.FindDangerousExtension(Path.GetFileName(e.ResultFilePath)) is not null)
                {
                    _dialogs.Info("Загрузка заблокирована", verdict.Reason, $"Адрес: {op.Uri}", DialogIcon.Danger);
                }

                return;
            }

            var folder = _settings.Current.DownloadFolder;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                e.Cancel = true;
                _dialogs.Info("Папка недоступна", "Не удалось открыть папку загрузок. Выберите другую папку в настройках.", $"{folder}\n{ex.Message}", DialogIcon.Warning);
                return;
            }

            var source = request.SourceUri ?? op.Uri;
            var detail =
                $"Файл: {verdict.SafeFileName}\n" +
                $"Ссылка: {source}\n" +
                $"Сервер файла: {verdict.DownloadHost}\n" +
                $"Сохранить в: {folder}";

            var confirmed = _dialogs.Confirm(
                "Загрузка с Hitmoz",
                "Скачать этот MP3 с Hitmoz? Проверьте права использования трека.",
                detail,
                "Скачать",
                "Отмена");

            if (!confirmed)
            {
                e.Cancel = true;
                Message?.Invoke(this, "Загрузка отменена.");
                return;
            }

            var decision = ExistingFileDecision.KeepBoth;
            if (File.Exists(Path.Combine(folder, verdict.SafeFileName)))
            {
                var choice = _dialogs.Choose(
                    "Файл уже существует",
                    $"Файл «{verdict.SafeFileName}» уже есть в папке загрузок. Заменить его?",
                    "«Сохранить копию» создаст файл с номером, существующий файл не изменится.",
                    new[]
                    {
                        new DialogButton("Сохранить копию", DialogButtonKind.Primary),
                        new DialogButton("Заменить", DialogButtonKind.Danger),
                        new DialogButton("Отмена"),
                    },
                    DialogIcon.Warning);

                decision = choice switch
                {
                    0 => ExistingFileDecision.KeepBoth,
                    1 => ExistingFileDecision.Overwrite,
                    _ => ExistingFileDecision.Cancel,
                };

                if (decision == ExistingFileDecision.Cancel)
                {
                    e.Cancel = true;
                    Message?.Invoke(this, "Загрузка отменена: файл уже существует.");
                    return;
                }
            }

            var tempPath = _temp.CreateDownloadPath();
            e.ResultFilePath = tempPath;

            var active = new ActiveDownload
            {
                Operation = op,
                Id = Guid.NewGuid().ToString("N"),
                FileName = verdict.SafeFileName,
                Folder = folder,
                Decision = decision,
                TempPath = tempPath,
                SourceUrl = source,
            };
            _downloads.Add(active);

            op.BytesReceivedChanged += (_, _) => ReportProgress(active);
            op.StateChanged += (_, _) => OnDownloadStateChanged(active);

            Message?.Invoke(this, $"Загрузка: {active.FileName}");
            ReportProgress(active);
        }

        private void ReportProgress(ActiveDownload d)
        {
            if (d.Finished)
            {
                return;
            }

            long? total = d.Operation.TotalBytesToReceive is { } t && t > 0 ? (long)t : null;
            DownloadProgress?.Invoke(this, new DownloadProgressInfo(d.Id, d.FileName, d.Operation.BytesReceived, total, false));
        }

        private void OnDownloadStateChanged(ActiveDownload d)
        {
            if (d.Finished)
            {
                return;
            }

            switch (d.Operation.State)
            {
                case CoreWebView2DownloadState.Completed:
                    d.Finished = true;
                    FinishDownload(d);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    d.Finished = true;
                    _temp.Delete(d.TempPath);
                    _temp.Delete(SafeResultPath(d));
                    var reason = d.Operation.InterruptReason;
                    _log.Information("Download interrupted {File}: {Reason}", d.FileName, reason);
                    Message?.Invoke(this, reason == CoreWebView2DownloadInterruptReason.UserCanceled
                        ? "Загрузка отменена, временный файл удалён."
                        : $"Загрузка прервана ({reason}). Временный файл удалён.");
                    DownloadProgress?.Invoke(this, new DownloadProgressInfo(d.Id, d.FileName, 0, null, true));
                    break;
            }

            _downloads.RemoveAll(x => x.Finished);
        }

        private void FinishDownload(ActiveDownload d)
        {
            var downloaded = SafeResultPath(d) ?? d.TempPath;
            try
            {
                if (!Mp3Validator.LooksLikeMp3(downloaded, out var reason))
                {
                    _temp.Delete(downloaded);
                    _log.Warning("Downloaded file rejected {File}: {Reason}", d.FileName, reason);
                    _dialogs.Info("Файл отклонён", "Скачанный файл не является MP3 и был удалён.", reason, DialogIcon.Warning);
                    Message?.Invoke(this, "Файл отклонён: " + reason);
                    return;
                }

                var target = TargetPathResolver.Resolve(d.Folder, d.FileName, d.Decision)
                             ?? TargetPathResolver.GetUniquePath(d.Folder, d.FileName);
                if (!TargetPathResolver.IsInsideFolder(target, d.Folder))
                {
                    throw new InvalidOperationException("Недопустимый путь сохранения.");
                }

                File.Move(downloaded, target, overwrite: d.Decision == ExistingFileDecision.Overwrite);

                var record = new DownloadRecord
                {
                    FilePath = target,
                    FileName = Path.GetFileName(target),
                    SourceUrl = d.SourceUrl,
                    DownloadedAtUtc = DateTime.UtcNow,
                    SizeBytes = new FileInfo(target).Length,
                };
                _history.Add(record);
                _log.Information("Download saved to {Target}", target);
                Message?.Invoke(this, $"Сохранено: {target}");
                DownloadCompleted?.Invoke(this, record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _temp.Delete(downloaded);
                _log.Error(ex, "Failed to move download {File}", d.FileName);
                _dialogs.Info("Не удалось сохранить файл", "Файл скачан, но не перемещён в папку загрузок. Временный файл удалён.", ex.Message, DialogIcon.Warning);
            }
            finally
            {
                DownloadProgress?.Invoke(this, new DownloadProgressInfo(d.Id, d.FileName, 0, null, true));
            }
        }

        private static string? SafeResultPath(ActiveDownload d)
        {
            try
            {
                return d.Operation.ResultFilePath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool IsDownloadLink(string? uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var u)
            && Hitmoz.IsHitmozHttps(u)
            && u.AbsolutePath.EndsWith(HitmozDownloadPolicy.AllowedExtension, StringComparison.OrdinalIgnoreCase);

        private static string DescribeError(CoreWebView2WebErrorStatus status) => status switch
        {
            CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.ConnectionAborted
                or CoreWebView2WebErrorStatus.ConnectionReset => "Соединение с сайтом прервано. Проверьте подключение к интернету.",
            CoreWebView2WebErrorStatus.HostNameNotResolved => "Не удалось найти сайт ru.hitmoz.org (ошибка DNS). Проверьте интернет или попробуйте позже.",
            CoreWebView2WebErrorStatus.CannotConnect => "Не удалось подключиться к ru.hitmoz.org.",
            CoreWebView2WebErrorStatus.Timeout => "Сайт не ответил вовремя.",
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
                or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked
                or CoreWebView2WebErrorStatus.CertificateIsInvalid => "Сертификат сайта не прошёл проверку — соединение небезопасно, страница не открыта.",
            CoreWebView2WebErrorStatus.ServerUnreachable => "Сервер Hitmoz недоступен.",
            _ => $"Сайт Hitmoz недоступен ({status}).",
        };

        private void SetState(BrowserState state, string? detail)
        {
            if (State == state && detail is null)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(this, (state, detail));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelDownloads();
            if (_core is not null)
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core.NewWindowRequested -= OnNewWindowRequested;
                _core.DownloadStarting -= OnDownloadStarting;
                _core.ProcessFailed -= OnProcessFailed;
            }

            try
            {
                _webView.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "WebView2 dispose failed");
            }
        }
    }
}
