using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SoundpadTrackHub.Infrastructure;

namespace SoundpadTrackHub.Integration
{
    /// <summary>
    /// Поиск процесса Soundpad.exe и его главного окна. Только чтение: процесс
    /// Soundpad не открывается на запись, код в него не внедряется, файлы не меняются.
    /// </summary>
    public sealed class SoundpadLocator
    {
        public const string ProcessName = "Soundpad";

        public Process? FindProcess()
        {
            Process? found = null;
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                if (found is null && !SafeHasExited(p))
                {
                    found = p;
                }
                else
                {
                    p.Dispose();
                }
            }

            return found;
        }

        /// <summary>Самое большое видимое окно верхнего уровня без владельца, принадлежащее процессу.</summary>
        public IntPtr FindMainWindow(int processId)
        {
            var best = IntPtr.Zero;
            long bestArea = 0;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != processId || !NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(hwnd, out var r))
                {
                    return true;
                }

                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }

                return true;
            }, IntPtr.Zero);

            return best;
        }

        /// <summary>Папки, где может лежать Soundpad, — чтобы удаление Track Hub их гарантированно не тронуло.</summary>
        public IReadOnlyList<string> GetSoundpadDirectories()
        {
            var dirs = new List<string>();
            using (var p = FindProcess())
            {
                try
                {
                    var exe = p?.MainModule?.FileName;
                    var dir = exe is null ? null : Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        dirs.Add(dir);
                    }
                }
                catch (Exception)
                {
                    // Нет доступа к модулю процесса (например, Soundpad запущен от администратора).
                }
            }

            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                dirs.Add(Path.Combine(root, "Soundpad"));
                dirs.Add(Path.Combine(root, "Steam", "steamapps", "common", "Soundpad"));
            }

            return dirs;
        }

        public static bool SafeHasExited(Process p)
        {
            try
            {
                return p.HasExited;
            }
            catch (Exception)
            {
                // Нет прав на опрос — считаем живым, пока процесс виден в списке.
                return false;
            }
        }

        public static bool IsAlive(int processId)
        {
            try
            {
                using var p = Process.GetProcessById(processId);
                return !SafeHasExited(p) && string.Equals(p.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
