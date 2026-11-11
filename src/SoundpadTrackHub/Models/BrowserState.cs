namespace SoundpadTrackHub.Models
{
    public enum BrowserState
    {
        /// <summary>WebView2 ещё не создавался (окно не показывалось).</summary>
        NotStarted,

        Initializing,

        /// <summary>Страница Hitmoz загружается.</summary>
        Loading,

        /// <summary>Страница Hitmoz открыта.</summary>
        Ready,

        /// <summary>Microsoft Edge WebView2 Runtime не установлен.</summary>
        RuntimeMissing,

        /// <summary>Сайт недоступен (сеть, DNS, сертификат, сбой WebView2).</summary>
        Unavailable,

        /// <summary>Сайт ответил HTTP 403.</summary>
        Forbidden,
    }

    public enum AppPage
    {
        Search,
        Browser,
        Downloads,
        Settings,
        Security,
        Replace,
    }

    public sealed record DownloadProgressInfo(string Id, string FileName, long Received, long? Total, bool IsFinished);
}
