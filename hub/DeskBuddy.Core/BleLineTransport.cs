using System.Text;

namespace DeskBuddy.Core;

public enum ManagementChannel { Unknown, Usb, Ble }
public interface IManagementTransport : ILineTransport { ManagementChannel Channel { get; } }

/// <summary>One GATT connection, with notifications enabled before OpenAsync completes.
/// WriteAsync must use write-with-response and never submit bytes after cancellation.
/// CloseAsync revokes the notification session and disposes its OS references.</summary>
public interface IGattByteConnection
{
    event Action<ReadOnlyMemory<byte>>? NotificationReceived;
    event Action<Exception?>? Disconnected;
    Task OpenAsync(CancellationToken cancellationToken);
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task CloseAsync();
}

/// <summary>Platform-independent, bounded ASCII/LF stream over a fresh GATT connection per open.
/// A failed or partially cancelled write poisons the whole connection, never just the current line.</summary>
public sealed class BleLineTransport : IManagementTransport
{
    public const int FragmentBytes = 20; // Safe even at ATT MTU 23; deliberately do not assume negotiated MTU.
    private readonly Func<IGattByteConnection> _factory;
    private readonly TimeSpan _connectTimeout, _writeTimeout;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _gate = new();
    private Context? _current;
    private Exception? _unreportedCloseError;
    private sealed class Context(IGattByteConnection connection)
    {
        public readonly IGattByteConnection Connection = connection;
        public readonly CancellationTokenSource Lifetime = new();
        public readonly LineFramer Framer = new(HubProtocol.MaxReplyLength);
        public Action<ReadOnlyMemory<byte>>? Notify;
        public Action<Exception?>? Disconnect;
        public bool Ready;
        public bool Closed;
        public Task? Closing;
    }
    public ManagementChannel Channel => ManagementChannel.Ble;
    public event Action<string>? LineReceived;
    public event Action<Exception?>? Disconnected;

    public BleLineTransport(Func<IGattByteConnection> connectionFactory, TimeSpan? connectTimeout = null, TimeSpan? writeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory); _factory = connectionFactory;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(20);
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(8);
        if (_connectTimeout <= TimeSpan.Zero || _connectTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        if (_writeTimeout <= TimeSpan.Zero || _writeTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(writeTimeout));
    }
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Context context;
        lock (_gate)
        {
            if (_current is not null) throw new InvalidOperationException("Close the previous BLE session before opening another.");
            if (_unreportedCloseError is not null) throw new IOException("Complete the previous BLE close before opening another session.");
            context = new(_factory() ?? throw new InvalidOperationException("Missing GATT connection."));
            _current = context;
            context.Notify = bytes => Receive(context, bytes);
            context.Disconnect = error => { _ = EndAsync(context, error ?? new IOException("BLE connection lost.")); };
            context.Connection.NotificationReceived += context.Notify;
            context.Connection.Disconnected += context.Disconnect;
        }
        using var deadline = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, context.Lifetime.Token);
        try
        {
            await context.Connection.OpenAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            lock (_gate) { EnsureCurrent(context, false); context.Ready = true; }
        }
        catch (Exception error)
        {
            await EndAsync(context, error).ConfigureAwait(false);
            if (error is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("BLE connection or service discovery timed out. Nothing was replayed.", error);
            throw;
        }
    }
    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(line) || line.Length > HubProtocol.MaxRequestLength || line.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("Expected 1..511 printable ASCII characters, without CR/LF.", nameof(line));
        Context context;
        lock (_gate) { context = _current ?? throw new IOException("BLE management is disconnected."); EnsureCurrent(context); }
        using var queueCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Lifetime.Token);
        await _writes.WaitAsync(queueCancellation.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) EnsureCurrent(context);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
            using var deadline = new CancellationTokenSource(_writeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, context.Lifetime.Token);
            try
            {
                for (int offset = 0; offset < bytes.Length; offset += FragmentBytes)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    lock (_gate) EnsureCurrent(context);
                    await context.Connection.WriteAsync(bytes.AsMemory(offset, Math.Min(FragmentBytes, bytes.Length - offset)), linked.Token)
                        .WaitAsync(linked.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                await EndAsync(context, error).ConfigureAwait(false);
                if (error is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("BLE line write timed out; the partial session was closed and will not be replayed.", error);
                throw;
            }
        }
        finally { _writes.Release(); }
    }
    private void EnsureCurrent(Context context, bool ready = true)
    {
        if (!ReferenceEquals(_current, context) || context.Closed || (ready && !context.Ready))
            throw new IOException("BLE session changed; queued data was abandoned.");
    }
    private void Receive(Context context, ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, context) || context.Closed) return;
            // Callback processing is bounded by the notification size; the framer never retains more than 4096 bytes.
            foreach (string line in context.Framer.Feed(bytes.Span))
            {
                if (!ReferenceEquals(_current, context) || context.Closed) break;
                LineReceived?.Invoke(line);
            }
        }
    }
    public async Task CloseAsync()
    {
        Context? context;
        lock (_gate) context = _current;
        if (context is not null) await EndAsync(context, null).ConfigureAwait(false);
        Exception? error;
        lock (_gate) { error = _unreportedCloseError; _unreportedCloseError = null; }
        if (error is not null)
            throw new IOException("BLE notification unsubscribe was not confirmed. Revoke management on the device or allow the firmware idle timeout to expire.", error);
    }
    private Task EndAsync(Context context, Exception? reason)
    {
        lock (_gate)
        {
            if (context.Closing is not null) return context.Closing;
            context.Closed = true; context.Ready = false; context.Lifetime.Cancel(); context.Framer.Reset();
            context.Connection.NotificationReceived -= context.Notify;
            context.Connection.Disconnected -= context.Disconnect;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Closing = completion.Task;
            if (ReferenceEquals(_current, context)) Disconnected?.Invoke(reason);
            _ = CloseConnectionAsync(context, completion);
            return completion.Task;
        }
    }
    private async Task CloseConnectionAsync(Context context, TaskCompletionSource completion)
    {
        try { await context.Connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) _unreportedCloseError = error; /* Preserve failure even if a write already closed this generation. */ }
        finally
        {
            lock (_gate) if (ReferenceEquals(_current, context)) _current = null;
            // Keep the cancelled token source alive for already queued callers; never reuse this context.
            completion.TrySetResult();
        }
    }
}
