using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace SoundpadTrackHub.Services
{
    /// <summary>Открытие папок и ссылок через оболочку Windows.</summary>
    public sealed class ShellService
    {
        public const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

        private readonly ILogger _log = Log.ForContext<ShellService>();

        /// <summary>Открывает только https-ссылки во внешнем браузере пользователя.</summary>
        public bool OpenInBrowser(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                _log.Warning("Refused to open non-https url {Url}", url);
                return false;
            }

            return Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }

        public bool OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "Cannot create folder {Folder}", folder);
                return false;
            }

            return Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }

        public bool RevealFile(string path)
        {
            if (!File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                return dir is not null && OpenFolder(dir);
            }

            return Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = false });
        }

        private bool Start(ProcessStartInfo psi)
        {
            try
            {
                using var _ = Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start {File}", psi.FileName);
                return false;
            }
        }
    }
}
