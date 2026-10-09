namespace DeskBuddy.Core;

/// <summary>A close deadline is not proof that a driver released its resource.</summary>
public sealed class WorkerClosePendingException(string resourceName, Exception? inner = null)
    : IOException($"{resourceName} is still closing. The driver has not released it; no new connection will be opened. Wait, or exit Hub before retrying.", inner)
{
    public string ResourceName { get; } = resourceName;
}

/// <summary>Owns a synchronous I/O worker and its process-local resource reservation until the worker really exits.</summary>
public sealed class SerialWorkerLifetime
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, SerialWorkerLifetime> Owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly string resourceName;
    private readonly TimeSpan closeTimeout;
    private CancellationTokenSource? cancellation;
    private Task? worker, closeAttempt;
    private bool closing;

    public SerialWorkerLifetime(string resourceName, TimeSpan? closeTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        this.resourceName = resourceName;
        this.closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(2);
        if (this.closeTimeout <= TimeSpan.Zero || this.closeTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(closeTimeout));
    }

    public bool IsClosing { get { lock (Sync) return closing && worker is { IsCompleted: false }; } }
    public static bool IsReserved(string resourceName) { lock (Sync) return Owners.ContainsKey(resourceName); }

    public void Start(Action<CancellationToken> run, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(run);
        token.ThrowIfCancellationRequested();
        lock (Sync)
        {
            if (worker is not null) throw new InvalidOperationException("A serial worker instance cannot be reused.");
            if (Owners.TryGetValue(resourceName, out var owner))
            {
                if (owner.closing) throw new WorkerClosePendingException(resourceName);
                throw new IOException($"{resourceName} is already owned by another Hub connection.");
            }
            var source = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation = source;
            Owners.Add(resourceName, this);
            try
            {
                worker = Task.Run(() =>
                {
                    try { run(source.Token); }
                    finally
                    {
                        // run must include disposal of the actual port. A cancelled await never releases this lease.
                        lock (Sync)
                        {
                            Owners.Remove(resourceName);
                            cancellation = null;
                        }
                        source.Dispose();
                    }
                }, CancellationToken.None);
            }
            catch { Owners.Remove(resourceName); cancellation = null; source.Dispose(); throw; }
        }
    }

    public async Task CloseAsync()
    {
        CancellationTokenSource? source;
        Task pending;
        lock (Sync)
        {
            if (worker is null) return;
            closing = true;
            source = cancellation;
            // Repeated cleanup shares one deadline. Once the worker really ends, a later cleanup can succeed.
            pending = worker.IsCompleted ? worker : closeAttempt ??= WaitForExitAsync(worker);
        }
        try { source?.Cancel(); } catch (ObjectDisposedException) { /* The worker finished between capture and cancellation. */ }
        await pending.ConfigureAwait(false);
    }

    private async Task WaitForExitAsync(Task task)
    {
        try { await task.WaitAsync(closeTimeout).ConfigureAwait(false); }
        catch (TimeoutException error) { throw new WorkerClosePendingException(resourceName, error); }
    }
}
