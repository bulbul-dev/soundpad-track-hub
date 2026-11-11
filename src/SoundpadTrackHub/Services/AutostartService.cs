using System;
using Microsoft.Win32;
using Serilog;

namespace SoundpadTrackHub.Services
{
    /// <summary>Абстракция над HKCU\Software\Microsoft\Windows\CurrentVersion\Run (для тестов).</summary>
    public interface IRunKeyStore
    {
        string? Get(string name);

        void Set(string name, string value);

        void Delete(string name);
    }

    public sealed class RegistryRunKeyStore : IRunKeyStore
    {
        public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string? Get(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }

        public void Set(string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public void Delete(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    public interface IAutostartService
    {
        bool IsEnabled { get; }

        bool SetEnabled(bool enabled);

        void RemoveAll();
    }

    /// <summary>Автозапуск Windows только через HKCU (права администратора не нужны).</summary>
    public sealed class AutostartService : IAutostartService
    {
        public const string ValueName = "SoundpadTrackHub";
        public const string AutostartArgument = "--autostart";

        /// <summary>Значения, которые могла оставить прошлая версия Track Hub.</summary>
        private static readonly string[] AllValueNames = { ValueName, "SoundpadTrackHubWatcher" };

        private readonly IRunKeyStore _store;
        private readonly string _executablePath;
        private readonly ILogger _log = Log.ForContext<AutostartService>();

        public AutostartService(IRunKeyStore store, string executablePath)
        {
            _store = store;
            _executablePath = executablePath;
        }

        public string CommandLine => $"\"{_executablePath}\" {AutostartArgument}";

        public bool IsEnabled
        {
            get
            {
                try
                {
                    var value = _store.Get(ValueName);
                    return !string.IsNullOrWhiteSpace(value)
                           && value.Contains(_executablePath, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to read autostart value");
                    return false;
                }
            }
        }

        public bool SetEnabled(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    _store.Set(ValueName, CommandLine);
                }
                else
                {
                    _store.Delete(ValueName);
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to change autostart to {Enabled}", enabled);
                return false;
            }
        }

        public void RemoveAll()
        {
            foreach (var name in AllValueNames)
            {
                try
                {
                    _store.Delete(name);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to remove autostart value {Name}", name);
                }
            }
        }
    }
}
