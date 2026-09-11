namespace NltvViewer.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\NLTVViewer.Application.Mutex";
    private const string ActivationEventName = @"Local\NLTVViewer.Application.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle _registeredWait;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex)
    {
        _mutex = mutex;
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public event EventHandler? ActivationRequested;

    public static SingleInstanceCoordinator? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceCoordinator(mutex);
    }

    public static void RequestActivation()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first process may still be starting; it remains the owner.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registeredWait.Unregister(null);
        _activationEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
