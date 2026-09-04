using System;
using System.Threading;

namespace HDRCalibrate.Services
{
    public sealed class SingleInstanceCoordinator : IDisposable
    {
        private const string DefaultIdentity = "HDRCalibrate.7E92C581-3D0F-48DA-B86C-9A9502014D39";

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _activationEvent;
        private readonly EventWaitHandle _exitEvent;
        private RegisteredWaitHandle _activationWait;
        private RegisteredWaitHandle _exitWait;
        private bool _ownsMutex;
        private bool _disposed;

        public SingleInstanceCoordinator(string identity = DefaultIdentity)
        {
            if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("Eine Instanzkennung ist erforderlich.", nameof(identity));
            _mutex = new Mutex(false, @"Local\" + identity + ".Mutex");
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + identity + ".Activate");
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + identity + ".Exit");
        }

        public bool TryAcquire()
        {
            ThrowIfDisposed();
            if (_ownsMutex) return true;
            try
            {
                _ownsMutex = _mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
            return _ownsMutex;
        }

        public void StartListening(Action activationCallback, Action exitCallback)
        {
            ThrowIfDisposed();
            if (!_ownsMutex) throw new InvalidOperationException("Nur die primäre Instanz darf Signale empfangen.");
            if (activationCallback == null) throw new ArgumentNullException(nameof(activationCallback));
            if (exitCallback == null) throw new ArgumentNullException(nameof(exitCallback));
            if (_activationWait != null || _exitWait != null)
                throw new InvalidOperationException("Die Signalempfänger wurden bereits gestartet.");

            _activationWait = Register(_activationEvent, activationCallback);
            _exitWait = Register(_exitEvent, exitCallback);
        }

        public void SignalActivation()
        {
            ThrowIfDisposed();
            _activationEvent.Set();
        }

        public void SignalExit()
        {
            ThrowIfDisposed();
            _exitEvent.Set();
        }

        public bool WaitForPrimaryExit(TimeSpan timeout)
        {
            ThrowIfDisposed();
            if (_ownsMutex) return true;
            if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            try
            {
                _ownsMutex = _mutex.WaitOne(timeout, false);
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
            return _ownsMutex;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activationWait?.Unregister(null);
            _exitWait?.Unregister(null);
            _activationWait = null;
            _exitWait = null;
            _activationEvent.Dispose();
            _exitEvent.Dispose();
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }
            _mutex.Dispose();
        }

        private RegisteredWaitHandle Register(EventWaitHandle waitHandle, Action callback)
        {
            return ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                (state, timedOut) =>
                {
                    if (!_disposed && !timedOut) callback();
                },
                null,
                Timeout.Infinite,
                false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceCoordinator));
        }
    }
}
