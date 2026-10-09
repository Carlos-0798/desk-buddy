using System.Globalization;

namespace DeskBuddy.Core;

public interface ILineTransport
{
    event Action<string>? LineReceived;
    event Action<Exception?>? Disconnected;
    Task OpenAsync(CancellationToken cancellationToken);
    Task SendLineAsync(string line, CancellationToken cancellationToken);
    Task CloseAsync();
}
public enum HubConnectionState { Disconnected, Connecting, Ready, Legacy, Faulted }

/// <summary>Single-flight protocol session. A line is never an acknowledgement without its request ID.</summary>
public sealed partial class HubClient : IAsyncDisposable
{
    private readonly ILineTransport _transport;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SemaphoreSlim _disconnects = new(1, 1);
    private readonly object _pendingLock = new();
    private Pending? _pending;
    private TaskCompletionSource<DeviceStatus>? _legacyStatus;
    private int _nextId;
    private int _generation;
    private bool _open;
    private volatile HubConnectionState _state;
    private BankSnapshot? _configuration;
    private long _lastStatusTimestamp;
    private bool _disposed;
    private sealed record Pending(int Id, TaskCompletionSource<HubReply> Completion);

    public event Action? Changed;
    public HubConnectionState State => _state;
    public bool IsLegacy => State == HubConnectionState.Legacy;
    public ManagementChannel ManagementChannel => (_transport as IManagementTransport)?.Channel ?? ManagementChannel.Unknown;
    public HelloInfo? Capabilities { get; private set; }
    public DeviceStatus? Status { get; private set; }
    public BankSnapshot? Configuration
    {
        get { var value = _configuration; return value is null ? null : value with { Bank = BankCodec.Clone(value.Bank) }; }
    }
    public string? LastError { get; private set; }
    public TimeSpan StatusStaleAfter { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan? StatusAge => Status is null ? null : _clock.GetElapsedTime(Interlocked.Read(ref _lastStatusTimestamp));
    public bool IsStatusStale => State is not (HubConnectionState.Ready or HubConnectionState.Legacy) ||
        StatusAge is not { } age || age > StatusStaleAfter;

    public HubClient(ILineTransport transport, TimeSpan? requestTimeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport; _timeout = requestTimeout ?? TimeSpan.FromSeconds(2); _clock = timeProvider ?? TimeProvider.System;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _transport.LineReceived += OnLine; _transport.Disconnected += OnDisconnected;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_open) throw new InvalidOperationException("Disconnect the current session before connecting again.");
            ClearDeviceState(); LastError = null; _state = HubConnectionState.Connecting;
            int generation = Interlocked.Increment(ref _generation); Notify();
            try
            {
                await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
                EnsureGeneration(generation); _open = true;
                HubReply? hello = null;
                try { hello = await RequestAsync("HELLO", cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (HubProtocolException ex) when (ex.Code == "UNSUPPORTED") { }
                EnsureGeneration(generation);
                if (hello is null)
                {
                    if (ManagementChannel == ManagementChannel.Ble)
                        throw new HubProtocolException("UPGRADE_REQUIRED", "BLE management requires the dedicated service and a successful protocol handshake. No legacy command was sent.");
                    await LegacyStatusAsync(cancellationToken).ConfigureAwait(false);
                    EnsureGeneration(generation); _state = HubConnectionState.Legacy;
                }
                else
                {
                    Capabilities = HubProtocol.ParseHello(hello);
                    if (ManagementChannel == ManagementChannel.Ble && !Capabilities.SupportsBleManagement)
                        throw new HubProtocolException("UPGRADE_REQUIRED", "This firmware does not advertise BLE Hub management. Upgrade it through the approved USB workflow.");
                    await GetCoreAsync(cancellationToken).ConfigureAwait(false);
                    await StatusCoreAsync(false, cancellationToken).ConfigureAwait(false);
                    EnsureGeneration(generation); _state = HubConnectionState.Ready;
                }
                LastError = null; Notify();
            }
            catch (Exception ex)
            {
                InvalidateConnection(ex);
                // A close deadline must not be reported as if the port had already been released.
                try { await CloseAfterFailedConnectAsync().ConfigureAwait(false); }
                catch (WorkerClosePendingException pending)
                { _state = HubConnectionState.Faulted; LastError = pending.Message; Notify(); throw; }
                _state = HubConnectionState.Faulted; LastError = ex.Message; Notify(); throw;
            }
        }
        finally { _operations.Release(); }
    }
    private async Task CloseAfterFailedConnectAsync()
    {
        try { await _transport.CloseAsync().ConfigureAwait(false); }
        catch (WorkerClosePendingException) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }
    }
    public async Task DisconnectAsync()
    {
        await _disconnects.WaitAsync().ConfigureAwait(false);
        try
        {
            bool revoke = _open && ManagementChannel == ManagementChannel.Ble && Capabilities?.SupportsBleManagement == true;
            bool acknowledged = false;
            var disconnected = new IOException("Device disconnected; pending operations were not replayed.");
            // Stop ordinary and queued operations, retaining only enough stream state for the one self-revoke ACK.
            Interlocked.Increment(ref _generation); ClearDeviceState(); _state = HubConnectionState.Disconnected;
            lock (_pendingLock) { _pending?.Completion.TrySetException(disconnected); _legacyStatus?.TrySetException(disconnected); }
            Notify();
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (revoke && _open)
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var ack = await RequestAsync("CONTROL MANAGE 0", deadline.Token).ConfigureAwait(false);
                        // Self-revoke is management-only: HID may remain armed. USB MANAGE preserves output only on peers that advertise that capability.
                        _ = HubProtocol.RequireBool(ack.Fields, "armed");
                        if (HubProtocol.RequireUInt(ack.Fields, "ble_manage") != 0 || HubProtocol.RequireUInt(ack.Fields, "ble_manage_remaining_ms") != 0)
                            throw new FormatException("Self-revoke acknowledgement did not confirm closed management permission.");
                        acknowledged = true;
                    }
                    catch (HubProtocolException error) when (error.Code == "LOCKED") { acknowledged = true; /* Already revoked. */ }
                    catch (Exception) { /* Never replay a failed revoke. CCCD close is the independent fallback below. */ }
                }
                InvalidateConnection(disconnected);
                try { await _transport.CloseAsync().ConfigureAwait(false); }
                catch (Exception error) when (ManagementChannel == ManagementChannel.Ble && acknowledged)
                { LastError = "Management revocation was acknowledged; Windows cleanup reported: " + error.Message; Notify(); }
            }
            finally { _operations.Release(); }
        }
        finally { _disconnects.Release(); }
    }
    public Task<BankSnapshot> GetAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        EnsureModern(); return await GetCoreAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
    public Task<DeviceStatus> RefreshStatusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => StatusCoreAsync(IsLegacy, cancellationToken), cancellationToken);

    public Task<BankSnapshot> PreviewAsync(HubBank bank, uint expectedRevision, CancellationToken cancellationToken = default) =>
        ChangeBankAsync("PREVIEW", bank, expectedRevision, cancellationToken);
    public Task<BankSnapshot> SaveAsync(HubBank bank, uint expectedRevision, CancellationToken cancellationToken = default) =>
        ChangeBankAsync("SAVE", bank, expectedRevision, cancellationToken);
    private Task<BankSnapshot> ChangeBankAsync(string verb, HubBank bank, uint expectedRevision, CancellationToken ct)
    {
        string hex = BankCodec.ToHex(bank); // Capture caller-owned mutable arrays before the first await.
        return RunAsync(async () =>
        {
            EnsureModern(); RequireRevision(expectedRevision);
            Capabilities!.RequireSupportedBank(BankCodec.FromHex(hex));
            InvalidateConfiguration();
            try
            {
                var ack = await RequestAsync($"{verb} {expectedRevision.ToString(CultureInfo.InvariantCulture)} {hex}", ct).ConfigureAwait(false);
                if (verb == "SAVE" && !HubProtocol.RequireBool(ack.Fields, "saved")) throw new HubProtocolException("SAVE_FAILED");
                return await VerifyReadbackAsync(ack, hex, verb == "PREVIEW", ct).ConfigureAwait(false);
            }
            catch { InvalidateConfiguration(); throw; }
        }, ct);
    }
    public Task<BankSnapshot> RevertAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        EnsureModern(); InvalidateConfiguration();
        try
        {
            var ack = await RequestAsync("REVERT", cancellationToken).ConfigureAwait(false);
            return await VerifyReadbackAsync(ack, null, false, cancellationToken).ConfigureAwait(false);
        }
        catch { InvalidateConfiguration(); throw; }
    }, cancellationToken);
    public Task<BankSnapshot> SelectProfileAsync(byte index, uint expectedRevision, CancellationToken cancellationToken = default)
    {
        if (index > 2) throw new ArgumentOutOfRangeException(nameof(index));
        return RunAsync(async () =>
        {
            EnsureModern(); RequireRevision(expectedRevision);
            var expected = _configuration!.Bank with { ActiveProfile = index };
            string hex = BankCodec.ToHex(expected); InvalidateConfiguration();
            try
            {
                var ack = await RequestAsync($"SELECT {expectedRevision.ToString(CultureInfo.InvariantCulture)} {index}", cancellationToken).ConfigureAwait(false);
                return await VerifyReadbackAsync(ack, hex, true, cancellationToken).ConfigureAwait(false);
            }
            catch { InvalidateConfiguration(); throw; }
        }, cancellationToken);
    }
    public Task<DeviceStatus> ControlAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!HubProtocol.IsControl(command)) throw new FormatException("Unsupported control command.");
        return RunAsync(async () =>
        {
            if (HubProtocol.RequiresPageOutputCapability(command) && (IsLegacy || Capabilities?.PreservesPageOutput != true))
                throw new HubProtocolException("UNSUPPORTED", "This device does not advertise the extended page controls.");
            if (command.StartsWith("MANAGE ", StringComparison.Ordinal))
            {
                if (ManagementChannel != ManagementChannel.Usb) throw new HubProtocolException("FORBIDDEN", "BLE management permission can only be changed locally on the device or through USB.");
                if (IsLegacy || Capabilities?.SupportsBleManagement != true) throw new HubProtocolException("UNSUPPORTED", "Upgrade the device firmware before enabling BLE Hub management.");
            }
            bool legacy = IsLegacy; Status = null; Notify();
            if (legacy) await ExchangeAsync(LegacyCommand(command), Task.FromResult(true), cancellationToken).ConfigureAwait(false);
            else
            {
                var ack = await RequestAsync("CONTROL " + command, cancellationToken).ConfigureAwait(false);
                ValidateControlFields(command, ack.Fields, false);
            }
            var status = await StatusCoreAsync(legacy, cancellationToken).ConfigureAwait(false);
            ValidateControlFields(command, status.Fields, true);
            return status;
        }, cancellationToken);
    }
    public Task<DeviceStatus> SetBleManagementAsync(bool enabled, CancellationToken cancellationToken = default) =>
        ControlAsync(enabled ? "MANAGE 1" : "MANAGE 0", cancellationToken);

    /// <summary>Start a six-second visual override and open HOME without saving a bank. Explicit preserve-output peers retain their current output state; older peers switch OFF.</summary>
    public Task<DeviceStatus> StartAnimationPreviewAsync(AnimationPreviewAction action, CancellationToken cancellationToken = default)
    {
        string token = action switch
        {
            AnimationPreviewAction.Idle => "IDLE", AnimationPreviewAction.Blink => "BLINK", AnimationPreviewAction.Happy => "HAPPY",
            AnimationPreviewAction.Walk => "WALK", AnimationPreviewAction.Tap => "TAP", AnimationPreviewAction.Sleep => "SLEEP",
            AnimationPreviewAction.Wake => "WAKE", _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return AnimateCoreAsync(token, (uint)action, cancellationToken);
    }

    /// <summary>End the override without restoring or arming output. Even STOP requires explicit advertised capability.</summary>
    public Task<DeviceStatus> StopAnimationPreviewAsync(CancellationToken cancellationToken = default) => AnimateCoreAsync("STOP", 0, cancellationToken);

    private Task<DeviceStatus> AnimateCoreAsync(string token, uint action, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        if (IsLegacy || Capabilities?.SupportsAnimationPreview != true)
            throw new HubProtocolException("UNSUPPORTED", "This device does not advertise temporary animation preview support. No command was sent.");
        Status = null; Notify();
        try
        {
            var ack = await RequestAsync("ANIMATE " + token, cancellationToken).ConfigureAwait(false);
            var observed = HubProtocol.RequireAnimationState(ack.Fields);
            bool armed = HubProtocol.RequireBool(ack.Fields, "armed");
            if (observed.Action != action || observed.Remaining != (action == 0 ? 0u : HubProtocol.AnimationPreviewDurationMilliseconds) || (action != 0 && armed && Capabilities?.PreservesOutput != true))
                throw new HubProtocolException("ANIMATION_NOT_CONFIRMED", "Acknowledgement did not confirm the requested temporary animation.");
            var readback = await RequestAsync("STATUS", cancellationToken).ConfigureAwait(false);
            var status = new DeviceStatus(readback.Fields, _clock.GetUtcNow(), Capabilities?.PreservesOutput == true);
            if (!status.HasAnimationPreviewStatus)
                throw new FormatException("Animation preview readback omitted its status fields.");
            if (Capabilities?.SupportsBleManagement == true && !status.HasBleManagementStatus)
                throw new FormatException("Device advertised BLE management but omitted its status fields.");
            RequirePetFeedback(status);
            // Physical input can legitimately cancel the demo between ACK and STATUS. Return that cancellation as observed.
            if (status.DemoAction is { } current && (uint)current != action)
                throw new HubProtocolException("ANIMATION_NOT_CONFIRMED", "Fresh device state reports a different temporary animation.");
            AcceptStatus(status);
            return status;
        }
        catch { Status = null; Notify(); throw; }
    }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int generation = Volatile.Read(ref _generation);
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureGeneration(generation);
            if (!_open || State is not (HubConnectionState.Ready or HubConnectionState.Legacy)) throw new IOException("No ready device session.");
            T result = await operation().ConfigureAwait(false);
            EnsureGeneration(generation); LastError = null; Notify(); return result;
        }
        catch (Exception ex)
        {
            if (ex is IOException and not HubProtocolException) InvalidateConnection(ex);
            if (ex is HubProtocolException { Code: "LOCKED" } && ManagementChannel == ManagementChannel.Ble)
            {
                InvalidateConnection(ex);
                try { await _transport.CloseAsync().ConfigureAwait(false); } catch (IOException) { }
            }
            if (ex is HubProtocolException { Code: "STALE" }) _configuration = null;
            if (ex is TimeoutException or OperationCanceledException) Status = null;
            LastError = ex.Message; Notify(); throw;
        }
        finally { _operations.Release(); }
    }
    private void EnsureModern()
    {
        if (IsLegacy) throw new HubProtocolException("UNSUPPORTED", "Legacy firmware supports status and explicit existing controls; bank editing requires protocol 1.");
    }
    private void RequireRevision(uint expected)
    {
        if (_configuration is null || _configuration.Revision != expected)
            throw new HubProtocolException("STALE", "Read the current device configuration before editing.");
    }
    private async Task<BankSnapshot> GetCoreAsync(CancellationToken ct)
    {
        try
        {
            var reply = await RequestAsync("GET", ct).ConfigureAwait(false);
            if (!reply.Fields.TryGetValue("hex", out string? hex)) throw new FormatException("GET is missing its configuration.");
            var value = new BankSnapshot(BankCodec.FromHex(hex), HubProtocol.RequireUInt(reply.Fields, "revision"), HubProtocol.RequireBool(reply.Fields, "preview"));
            Capabilities?.RequireSupportedBank(value.Bank);
            _configuration = value; Notify(); return value with { Bank = BankCodec.Clone(value.Bank) };
        }
        catch { _configuration = null; Notify(); throw; }
    }
    private async Task<BankSnapshot> VerifyReadbackAsync(HubReply ack, string? expectedHex, bool expectedPreview, CancellationToken ct)
    {
        uint revision = HubProtocol.RequireUInt(ack.Fields, "revision");
        if (HubProtocol.RequireBool(ack.Fields, "preview") != expectedPreview) throw new FormatException("Unexpected preview state in acknowledgement.");
        if (Capabilities?.PreservesOutput == true) _ = HubProtocol.RequireBool(ack.Fields, "armed");
        var readback = await GetCoreAsync(ct).ConfigureAwait(false);
        if (readback.Revision != revision || readback.Preview != expectedPreview ||
            (expectedHex is not null && BankCodec.ToHex(readback.Bank) != expectedHex))
            throw new HubProtocolException("READBACK_MISMATCH", "Acknowledgement and device configuration readback differ.");
        var status = await StatusCoreAsync(false, ct).ConfigureAwait(false);
        // Output can change physically between ACK and readback. Never restore a cached ON state.
        if ((status.Armed && Capabilities?.PreservesOutput != true) || (_configuration is null))
            throw new HubProtocolException("READBACK_MISMATCH", "Configuration changed or output was unexpectedly armed during readback.");
        return readback;
    }
    private async Task<DeviceStatus> StatusCoreAsync(bool legacy, CancellationToken ct)
    {
        DeviceStatus status;
        if (legacy) status = await LegacyStatusAsync(ct).ConfigureAwait(false);
        else
        {
            var reply = await RequestAsync("STATUS", ct).ConfigureAwait(false);
            status = new(reply.Fields, _clock.GetUtcNow(), Capabilities?.PreservesOutput == true);
            if (Capabilities?.SupportsAnimationPreview == true && !status.HasAnimationPreviewStatus)
                throw new FormatException("Device advertised animation preview but omitted its status fields.");
            if (Capabilities?.SupportsBleManagement == true && !status.HasBleManagementStatus)
                throw new FormatException("Device advertised BLE management but omitted its status fields.");
            RequirePetFeedback(status);
            AcceptStatus(status);
        }
        return status;
    }
    private async Task<DeviceStatus> LegacyStatusAsync(CancellationToken ct)
    {
        var pending = new TaskCompletionSource<DeviceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _legacyStatus = pending;
        try
        {
            return await ExchangeAsync("STATUS", pending.Task, ct).ConfigureAwait(false);
        }
        finally { lock (_pendingLock) if (_legacyStatus == pending) _legacyStatus = null; }
    }
    private async Task<HubReply> RequestAsync(string command, CancellationToken ct)
    {
        if (!_open) throw new IOException("Device disconnected.");
        if (_nextId == int.MaxValue) throw new InvalidOperationException("Request ID space exhausted; create a new client.");
        int id = ++_nextId;
        string line = HubProtocol.BuildRequest(id, command);
        var pending = new Pending(id, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_pendingLock) _pending = pending;
        try
        {
            var reply = await ExchangeAsync(line, pending.Completion.Task, ct).ConfigureAwait(false);
            if (!reply.Success) throw new HubProtocolException(reply.ErrorCode!);
            return reply;
        }
        catch (IOException ex) when (ex is not HubProtocolException) { InvalidateConnection(ex); throw; }
        finally { lock (_pendingLock) if (_pending == pending) _pending = null; }
    }
    private async Task<T> ExchangeAsync<T>(string line, Task<T> response, CancellationToken ct)
    {
        // One deadline covers BOTH the transport write and its response, not two sequential budgets.
        using var deadline = new CancellationTokenSource(_timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        bool sent = false;
        try
        {
            await _transport.SendLineAsync(line, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            sent = true;
            return await response.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!sent)
            {
                // A hanging/partially completed transport write may not be safely followed by another request.
                InvalidateConnection(new IOException("Transport write did not complete; reconnect explicitly before continuing."));
                try { await _transport.CloseAsync().WaitAsync(_timeout).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { }
            }
            if (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("Device request timed out; no request was retried. Read back device state before editing again.");
            throw;
        }
    }
    private void OnLine(string line)
    {
        if (!_open) return;
        if (LegacyStatusParser.TryParse(line, out var status, _clock.GetUtcNow(), Capabilities?.PreservesOutput == true))
        {
            if (Capabilities?.SupportsAnimationPreview == true && !status!.HasAnimationPreviewStatus) return;
            if (Capabilities?.SupportsBleManagement == true && !status!.HasBleManagementStatus) return;
            if (Capabilities?.SupportsPetInputFeedback == true && !status!.HasPetInputFeedbackStatus) return;
            if (Capabilities?.SupportsDisplaySleep == true && (status!.Display is null || status.Display.BacklightControlled != Capabilities.BacklightControl)) return;
            AcceptStatus(status!);
            lock (_pendingLock) _legacyStatus?.TrySetResult(status!);
            return;
        }
        if (!line.StartsWith("HUB ", StringComparison.Ordinal)) return;
        try
        {
            var reply = HubProtocol.ParseReply(line);
            lock (_pendingLock) if (_pending?.Id == reply.Id) _pending.Completion.TrySetResult(reply);
        }
        catch (FormatException ex)
        {
            string[] tokens = line.Split(' ', 4);
            if (tokens.Length >= 2 && int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                lock (_pendingLock) if (_pending?.Id == id) _pending.Completion.TrySetException(ex);
        }
    }
    private void AcceptStatus(DeviceStatus status)
    {
        if (Capabilities?.SupportsDisplaySleep == true && (status.Display is null || status.Display.BacklightControlled != Capabilities.BacklightControl))
            throw new FormatException("Advertised display capability and current display status are missing or inconsistent.");
        Interlocked.Exchange(ref _lastStatusTimestamp, _clock.GetTimestamp());
        Status = status;
        if (status.GetUInt32("config_revision") is uint revision && _configuration is { } config && revision != config.Revision)
            _configuration = null;
        Notify();
    }
    private static string LegacyCommand(string command) => command switch
    {
        "OUTPUT 0" => "HID OFF", "OUTPUT 1" => "HID ON", "ROUTE USB" => "USB", "ROUTE BLE" => "BLE",
        "LANGUAGE EN" => "LANG EN", "LANGUAGE ZH" => "LANG ZH", "PAGE HOME" => "HOME", "PAGE DIAG" => "DIAG",
        "PAGE INFO" => "INFO", "PAGE ACTIVITY" => "ACTIVITY", "COUNTERS RESET" => "COUNTERS RESET",
        _ => throw new FormatException("Unsupported legacy command.")
    };
    private void RequirePetFeedback(DeviceStatus status)
    {
        if (Capabilities?.SupportsPetInputFeedback == true && !status.HasPetInputFeedbackStatus)
            throw new FormatException("Device advertised physical pet feedback but omitted its status fields.");
    }
    private void ValidateControlFields(string command, IReadOnlyDictionary<string, string> fields, bool status)
    {
        string[] tokens = command.Split(' ');
        bool preservesOutput = Capabilities?.PreservesOutput == true;
        bool preservesPageOutput = Capabilities?.PreservesPageOutput == true;
        // ACK and STATUS are separate observations, not a request to restore pre-command output.
        if (preservesOutput && tokens[0] is "ROUTE" or "LANGUAGE" or "PAGE") _ = HubProtocol.RequireBool(fields, "armed");
        if (preservesPageOutput && tokens[0] == "PAGE") _ = HubProtocol.RequireBool(fields, "armed");
        bool valid = tokens[0] switch
        {
            "OUTPUT" => fields.GetValueOrDefault("armed") == tokens[1],
            "ROUTE" => fields.GetValueOrDefault("route") == tokens[1] && (preservesOutput || fields.GetValueOrDefault("armed") == "0"),
            "LANGUAGE" => fields.GetValueOrDefault("language") == tokens[1] && fields.GetValueOrDefault(status ? "language_saved" : "saved") == "1",
            "PAGE" => fields.GetValueOrDefault("page") == (tokens[1] switch { "HOME" => "0", "SETTINGS" => "1", "DIAG" => "2", "INFO" => "4", "ACTIVITY" => "5", "MANAGEMENT" => "6", "LANGUAGE" => "7", "TOOLS" => "8", _ => "" }) &&
                ((preservesOutput && tokens[1] == "HOME") || (preservesPageOutput && tokens[1] != "HOME") || fields.GetValueOrDefault("armed") == "0"),
            "COUNTERS" => status ? fields.GetValueOrDefault("key_presses") == "0,0,0,0,0,0" && fields.GetValueOrDefault("key_releases") == "0,0,0,0,0,0" &&
                new[] { "presses", "cw", "ccw", "markers" }.All(k => fields.GetValueOrDefault(k) == "0") : fields.GetValueOrDefault("reset") == "1",
            "MANAGE" => ValidateManageFields(tokens[1] == "1", fields, preservesOutput),
            _ => false
        };
        if (!valid) throw new HubProtocolException("CONTROL_NOT_CONFIRMED", "Fresh device state did not confirm the requested control.");
    }
    private static bool ValidateManageFields(bool enable, IReadOnlyDictionary<string, string> fields, bool preservesOutput)
    {
        uint state = HubProtocol.RequireUInt(fields, "ble_manage"), remaining = HubProtocol.RequireUInt(fields, "ble_manage_remaining_ms");
        bool armed = HubProtocol.RequireBool(fields, "armed");
        return (preservesOutput || !armed) && (enable ? (state == 1 && remaining is > 0 and <= 60000) || (state == 2 && remaining == 0) : state == 0 && remaining == 0);
    }
    private void EnsureGeneration(int generation)
    { if (Volatile.Read(ref _generation) != generation) throw new IOException("Device session changed; operation was not replayed."); }
    private void InvalidateConfiguration() { _configuration = null; Status = null; Notify(); }
    private void ClearDeviceState() { _configuration = null; Status = null; Capabilities = null; }
    private void OnDisconnected(Exception? reason) => InvalidateConnection(reason ?? new IOException("Device disconnected."));
    private void InvalidateConnection(Exception reason)
    {
        Interlocked.Increment(ref _generation); _open = false; ClearDeviceState(); _state = HubConnectionState.Disconnected;
        LastError = reason.Message;
        var error = new IOException("Device disconnected; pending requests were abandoned.", reason);
        lock (_pendingLock) { _pending?.Completion.TrySetException(error); _legacyStatus?.TrySetException(error); }
        Notify();
    }
    private void Notify() => Changed?.Invoke();
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally { _transport.LineReceived -= OnLine; _transport.Disconnected -= OnDisconnected; }
    }
}
