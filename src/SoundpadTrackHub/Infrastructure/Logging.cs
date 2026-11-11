using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace SoundpadTrackHub.Infrastructure
{
    public static class Logging
    {
        public static void Configure(AppPaths paths)
        {
            try
            {
                Directory.CreateDirectory(paths.LogsDirectory);
            }
            catch (Exception)
            {
                // Без журнала приложение всё равно должно запускаться.
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.WithProperty("App", "SoundpadTrackHub")
                .WriteTo.File(
                    Path.Combine(paths.LogsDirectory, "trackhub-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
    }
}
