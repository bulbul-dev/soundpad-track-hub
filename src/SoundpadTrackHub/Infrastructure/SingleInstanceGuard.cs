using System;
using System.Threading;

namespace SoundpadTrackHub.Infrastructure
{
    /// <summary>
    /// Single-instance через именованный Mutex. Вторая копия не запускается,
    /// а просит первую показать окно (именованное событие).
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        public const string DefaultName = "SoundpadTrackHub";

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _activateEvent;
        private RegisteredWaitHandle? _registration;
        private bool _ownsMutex;

        private SingleInstanceGuard(Mutex mutex, EventWaitHandle activateEvent, bool ownsMutex)
        {
            _mutex = mutex;
            _activateEvent = activateEvent;
            _ownsMutex = ownsMutex;
        }

        public bool IsFirstInstance => _ownsMutex;

        public static SingleInstanceGuard Acquire(string name = DefaultName)
        {
            var mutex = new Mutex(initiallyOwned: false, @"Local\" + name + ".SingleInstance");
            bool owns;
            try
            {
                owns = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // Предыдущий экземпляр аварийно завершился — мьютекс теперь наш.
                owns = true;
            }

            var evt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Activate");
            return new SingleInstanceGuard(mutex, evt, owns);
        }

        /// <summary>Вызывается во второй копии: просит первую показать окно.</summary>
        public void SignalFirstInstance()
        {
            try
            {
                _activateEvent.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Вызывается в первой копии: callback срабатывает на пуле потоков.</summary>
        public void ListenForActivation(Action onActivate)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (_, _) => onActivate(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        public void Dispose()
        {
            _registration?.Unregister(null);
            _registration = null;

            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Мьютекс освобождается не из потока-владельца — Windows освободит его при выходе.
                }

                _ownsMutex = false;
            }

            _mutex.Dispose();
            _activateEvent.Dispose();
        }
    }
}
