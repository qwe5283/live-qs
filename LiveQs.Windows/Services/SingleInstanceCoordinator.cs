using System.Windows.Threading;

namespace LiveQs.Windows.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string SingleInstanceMutexName = @"Local\LiveQs.Windows.SingleInstance";
    private const string ActivationEventName = @"Local\LiveQs.Windows.Activate";

    private readonly Dispatcher _dispatcher;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private Mutex? _singleInstance;
    private Action? _activationHandler;
    private bool _activationPending;
    private bool _ownsSingleInstanceMutex;
    private bool _disposed;

    public SingleInstanceCoordinator(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_singleInstance is not null) return _ownsSingleInstanceMutex;

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _singleInstance = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            _activationEvent.Set();
            return false;
        }

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) => ((SingleInstanceCoordinator)state!).QueueActivationRequest(),
            this,
            Timeout.Infinite,
            false);
        return true;
    }

    public void AttachActivationHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activationHandler = handler;
        if (_activationPending)
        {
            _activationPending = false;
            _ = _dispatcher.BeginInvoke(new Action(handler));
        }
    }

    private void QueueActivationRequest()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _ = _dispatcher.BeginInvoke(new Action(DispatchActivationRequest));
    }

    private void DispatchActivationRequest()
    {
        if (_activationHandler is { } handler)
        {
            handler();
            return;
        }

        _activationPending = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstance?.ReleaseMutex(); }
            catch (ApplicationException) { }
            _ownsSingleInstanceMutex = false;
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
