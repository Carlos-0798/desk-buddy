using System.Text;
using DeskBuddy.Core;

internal static partial class Program
{
    private const string BleClosed = " ble_manage=0 ble_manage_remaining_ms=0 ble_link=0 ble_secured=0 ble_bonded=0 manage_owner=0 manage_lease_ms=0";
    private static async Task BleManagementProtocol()
    {
        Check(!ParseCapabilities().SupportsBleManagement && !ParseCapabilities(" ble_management=0").SupportsBleManagement, "old BLE capability stays false");
        Check(ParseCapabilities(" ble_management=1").SupportsBleManagement, "explicit BLE management support");
        Check(ParseCapabilities(" ble_management=1").BleManagementIdleMilliseconds is null, "missing idle capability remains unknown");
        Check(ParseCapabilities(" ble_management=1 ble_manage_idle_ms=15000").BleManagementIdleMilliseconds == 15000, "explicit idle lease");
        foreach (string value in new[] { "0", "14999", "15001", "-1", "true", "15000 ble_manage_idle_ms=15000" })
            Throws<FormatException>(() => ParseCapabilities(" ble_management=1 ble_manage_idle_ms=" + value), "strict idle capability");
        Throws<FormatException>(() => ParseCapabilities(" ble_management=0 ble_manage_idle_ms=15000"), "idle requires BLE capability");
        foreach (string value in new[] { "", "2", "01", "+1", "-1", "true", "1.0", "1 ble_management=1" })
            Throws<FormatException>(() => ParseCapabilities(" ble_management=" + value), "strict BLE capability");
        Check(HubProtocol.BuildRequest(1, "CONTROL MANAGE 1") == "HUB 1 CONTROL MANAGE 1", "grant command shape");
        Check(HubProtocol.BuildRequest(1, "CONTROL MANAGE 0") == "HUB 1 CONTROL MANAGE 0", "revoke command shape");
        foreach (string command in new[] { "CONTROL MANAGE", "CONTROL MANAGE 2", "CONTROL MANAGE1", "CONTROL MANAGE true", "CONTROL MANAGE 1 60000" })
            Throws<FormatException>(() => HubProtocol.BuildRequest(1, command), "invalid management command");
        var absent = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields());
        Check(!absent.HasBleManagementStatus && absent.ManagementOwner is null, "no invented management telemetry");
        foreach (string extra in new[] { BleClosed, BleClosed.Replace("ble_manage=0", "ble_manage=1").Replace("ble_manage_remaining_ms=0", "ble_manage_remaining_ms=60000"),
            " ble_manage=2 ble_manage_remaining_ms=0 ble_link=1 ble_secured=1 ble_bonded=1 manage_owner=2 manage_lease_ms=10000" })
            Check(LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields() + extra).HasBleManagementStatus, "coherent grant state");
        foreach (string bad in new[] { " ble_manage=0", BleClosed.Replace("ble_manage=0", "ble_manage=3"), BleClosed.Replace("ble_manage_remaining_ms=0", "ble_manage_remaining_ms=1"),
            BleClosed.Replace("ble_manage=0", "ble_manage=1"), BleClosed.Replace("ble_manage=0", "ble_manage=2"), BleClosed.Replace("manage_owner=0", "manage_owner=3"),
            BleClosed.Replace("manage_owner=0", "manage_owner=1"), BleClosed.Replace("manage_lease_ms=0", "manage_lease_ms=1"), BleClosed.Replace("ble_link=0", "ble_link=2"),
            BleClosed.Replace("ble_manage=0", "ble_manage=1").Replace("ble_manage_remaining_ms=0", "ble_manage_remaining_ms=60001"),
            BleClosed.Replace("manage_owner=0", "manage_owner=2").Replace("manage_lease_ms=0", "manage_lease_ms=10001") })
            Throws<FormatException>(() => LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields() + bad), "invalid grant/lease telemetry");
        var old = new FakeDevice(); await using var oldClient = new HubClient(old); await oldClient.ConnectAsync();
        int oldBefore = old.Sent.Count; await AnimationError("UNSUPPORTED", () => oldClient.SetBleManagementAsync(true));
        Check(old.Sent.Count == oldBefore, "old USB device never receives invented grant command");
        var usb = new FakeDevice { BleManagementCapability = true }; await using var usbClient = new HubClient(usb); await usbClient.ConnectAsync();
        string original = usb.Hex; int before = usb.Sent.Count;
        var granted = await usbClient.SetBleManagementAsync(true);
        Check(granted.BleManagementState == 1 && granted.BleManagementRemainingMilliseconds == 60000 && !granted.Armed, "USB grant ACK and status observed");
        Check(usb.Sent.Skip(before).Select(s => string.Join(' ', s.Split(' ').Skip(2))).SequenceEqual(new[] { "CONTROL MANAGE 1", "STATUS" }), "grant is exactly explicit MANAGE plus status, no SAVE or route change");
        Check((await usbClient.SetBleManagementAsync(false)).BleManagementState == 0 && usb.Hex == original && usb.Revision == 1, "revoke preserves bank");
        usb.Intercept = line => usb.Emit($"HUB {line.Split(' ')[1]} OK ble_manage=0 ble_manage_remaining_ms=0 armed=0");
        await AnimationError("CONTROL_NOT_CONFIRMED", () => usbClient.SetBleManagementAsync(true));
        foreach (ManagementChannel channel in new[] { ManagementChannel.Ble, ManagementChannel.Unknown })
        {
            var peer = new FakeDevice { Channel = channel, BleManagementCapability = true };
            await using var client = new HubClient(peer); await client.ConnectAsync(); int start = peer.Sent.Count;
            await AnimationError("FORBIDDEN", () => client.SetBleManagementAsync(true));
            await AnimationError("FORBIDDEN", () => client.ControlAsync("MANAGE 0"));
            Check(peer.Sent.Count == start, "BLE/unknown transport cannot remotely grant or revoke itself");
        }
        var noCap = new FakeDevice { Channel = ManagementChannel.Ble };
        await using var noCapClient = new HubClient(noCap); await AnimationError("UPGRADE_REQUIRED", () => noCapClient.ConnectAsync());
        Check(noCap.Sent.Count == 1 && noCapClient.State == HubConnectionState.Faulted, "BLE requires advertised capability before GET");
        var noHello = new FakeDevice { Channel = ManagementChannel.Ble, Legacy = true };
        await using var noHelloClient = new HubClient(noHello, TimeSpan.FromMilliseconds(30));
        await AnimationError("UPGRADE_REQUIRED", () => noHelloClient.ConnectAsync());
        Check(noHello.Sent.Count == 1, "BLE never falls back to legacy STATUS");
        var locked = new FakeDevice { Channel = ManagementChannel.Ble, BleManagementCapability = true };
        locked.Intercept = line => locked.Emit($"HUB {line.Split(' ')[1]} ERR code=LOCKED");
        await using var lockedClient = new HubClient(locked); await AnimationError("LOCKED", () => lockedClient.ConnectAsync());
        Check(locked.Sent.Count == 1 && !locked.IsOpen, "LOCKED HELLO is not retried or mistaken for legacy");
    }

    private static async Task BleFraming()
    {
        var connection = new FakeGatt(); var lines = new List<string>();
        var transport = new BleLineTransport(() => connection); transport.LineReceived += lines.Add;
        await transport.OpenAsync(CancellationToken.None);
        foreach (int length in new[] { 1, 19, 20, 21, 39, 40, 510, 511 })
        {
            connection.Writes.Clear(); string line = new('A', length);
            await transport.SendLineAsync(line, CancellationToken.None);
            Check(connection.Writes.All(b => b.Length is > 0 and <= 20) && connection.Writes.Count == (length + 20) / 20, "conservative ATT-MTU fragment sizes");
            Check(connection.Writes.SelectMany(b => b).SequenceEqual(Encoding.ASCII.GetBytes(line + "\n")), "exact request bytes plus one LF");
        }
        int before = connection.Writes.Count;
        foreach (string invalid in new[] { "", new string('A', 512), "A\nB", "A\r", "中文", "A\0B", "A\tB" })
            await ThrowsAsync<ArgumentException>(() => transport.SendLineAsync(invalid, CancellationToken.None), "invalid line before any bytes");
        Check(connection.Writes.Count == before, "no fragment from invalid request");
        string exact = new('X', 4096);
        foreach (byte b in Encoding.ASCII.GetBytes(exact + "\r\n")) connection.Emit(new byte[] { b });
        Check(lines.Single() == exact, "4096 content bytes reassemble across single-byte notifications and CRLF");
        connection.Emit(Encoding.ASCII.GetBytes(new string('X', 4097) + "BAD\nGOOD\n"));
        Check(lines.Count == 2 && lines[^1] == "GOOD", "overflow drops whole line and resynchronizes only at LF");
        connection.Emit(new byte[] { 65, 0xFF, 66, 10, 67, 13, 68, 10, 69, 10 });
        Check(lines.Count == 3 && lines[^1] == "E", "invalid ASCII or interior CR never leaks truncated reply");
        connection.Emit("ONE\nTWO\nPART"u8.ToArray()); connection.Emit("IAL\n"u8.ToArray());
        Check(lines.TakeLast(3).SequenceEqual(new[] { "ONE", "TWO", "PARTIAL" }), "multiple lines and partial line in one notification");
        await transport.CloseAsync(); Check(connection.CloseCount == 1, "close disposes one GATT session");
    }

    private static async Task BleLifecycle()
    {
        var first = new FakeGatt(); var second = new FakeGatt(); int opens = 0, disconnects = 0; var lines = new List<string>();
        var transport = new BleLineTransport(() => ++opens == 1 ? first : second, writeTimeout: TimeSpan.FromMilliseconds(70));
        transport.Disconnected += _ => disconnects++; transport.LineReceived += lines.Add;
        await transport.OpenAsync(CancellationToken.None);
        first.Emit("OLDPART"u8.ToArray()); var oldCallback = first.CaptureNotification();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.WriteBehavior = async (_, ct) => { writeEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using (var cancel = new CancellationTokenSource())
        {
            var partial = transport.SendLineAsync(new string('P', 80), cancel.Token); await writeEntered.Task;
            var queued = transport.SendLineAsync("NEW", CancellationToken.None); cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => partial, "cancel in-flight fragment");
            await ThrowsAsync<OperationCanceledException>(() => queued, "queued line canceled with old connection");
        }
        Check(first.Writes.Count == 1 && first.CloseCount == 1 && disconnects == 1, "partial failure closes once, never emits remainder or queued line");
        await transport.OpenAsync(CancellationToken.None);
        oldCallback?.Invoke("LEAK\n"u8.ToArray()); second.Emit("FRESH\n"u8.ToArray());
        Check(lines.SequenceEqual(new[] { "FRESH" }), "old notifications and old partial line cannot cross reconnect");
        await transport.SendLineAsync("HELLO", CancellationToken.None);
        Check(Encoding.ASCII.GetString(second.Writes.SelectMany(b => b).ToArray()) == "HELLO\n", "new session contains only explicit new line");
        second.Cut(); await transport.CloseAsync();
        Check(disconnects == 2 && second.CloseCount == 1, "remote disconnect notifies and disposes once");
        var timeoutPeer = new FakeGatt { WriteBehavior = async (_, ct) => await Task.Delay(Timeout.Infinite, ct) };
        var timeoutTransport = new BleLineTransport(() => timeoutPeer, writeTimeout: TimeSpan.FromMilliseconds(25)); await timeoutTransport.OpenAsync(CancellationToken.None);
        await ThrowsAsync<TimeoutException>(() => timeoutTransport.SendLineAsync("LONG", CancellationToken.None), "write has total deadline");
        Check(timeoutPeer.CloseCount == 1 && timeoutPeer.Writes.Count == 1, "write deadline poisons connection without retry");
        var openingPeer = new FakeGatt { OpenBehavior = async ct => await Task.Delay(Timeout.Infinite, ct) };
        var openingTransport = new BleLineTransport(() => openingPeer, connectTimeout: TimeSpan.FromMilliseconds(25));
        await ThrowsAsync<TimeoutException>(() => openingTransport.OpenAsync(CancellationToken.None), "discovery/open deadline");
        Check(openingPeer.CloseCount == 1 && openingPeer.Writes.Count == 0, "failed open sends nothing");
        var lateOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new FakeGatt { OpenBehavior = _ => lateOpen.Task };
        var next = new FakeGatt(); int lateOpens = 0;
        var lateTransport = new BleLineTransport(() => lateOpens++ == 0 ? late : next, connectTimeout: TimeSpan.FromMilliseconds(25));
        await ThrowsAsync<TimeoutException>(() => lateTransport.OpenAsync(CancellationToken.None), "late non-cancellable backend opening");
        await lateTransport.OpenAsync(CancellationToken.None); lateOpen.SetResult();
        await lateTransport.SendLineAsync("NEW", CancellationToken.None);
        Check(late.CloseCount == 1 && late.Writes.Count == 0 && next.Requests.SequenceEqual(new[] { "NEW" }), "late opening cannot resurrect or write into replacement session");
        await lateTransport.CloseAsync();
        var unused = new FakeGatt(); var canceledTransport = new BleLineTransport(() => unused);
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => canceledTransport.OpenAsync(alreadyCancelled.Token), "pre-canceled open");
        Check(unused.OpenCount == 0 && unused.Writes.Count == 0, "pre-canceled open never touches backend");
        await canceledTransport.OpenAsync(CancellationToken.None);
        await ThrowsAsync<OperationCanceledException>(() => canceledTransport.SendLineAsync("STATUS", alreadyCancelled.Token), "pre-canceled send");
        Check(unused.Writes.Count == 0 && unused.CloseCount == 0, "pre-canceled line leaves untouched valid session usable");
        await canceledTransport.CloseAsync();
        var lost = new FakeGatt { WriteBehavior = (_, _) => throw new IOException("ATT write rejected") };
        var lostTransport = new BleLineTransport(() => lost); await lostTransport.OpenAsync(CancellationToken.None);
        await ThrowsAsync<IOException>(() => lostTransport.SendLineAsync(new string('S', 60), CancellationToken.None), "ATT failure");
        Check(lost.Writes.Count == 1 && lost.CloseCount == 1, "failed ATT never advances to next fragment");
        var doubleFailure = new FakeGatt { WriteBehavior = (_, _) => Task.FromException(new IOException("write failed")), CloseBehavior = () => Task.FromException(new IOException("CCCD failed")) };
        var doubleTransport = new BleLineTransport(() => doubleFailure); await doubleTransport.OpenAsync(CancellationToken.None);
        await ThrowsAsync<IOException>(() => doubleTransport.SendLineAsync("STATUS", CancellationToken.None), "write failure closes generation");
        await ThrowsAsync<IOException>(doubleTransport.CloseAsync, "later explicit close still reports background unsubscribe failure");
        await doubleTransport.CloseAsync();
        Check(doubleFailure.CloseCount == 1, "background cleanup failure observed once without retrying close or command");
    }

    private static async Task BleProtocolIntegration()
    {
        var peer = new FakeDevice { Channel = ManagementChannel.Ble, BleManagementCapability = true, BleManagementState = 2, AnimationPreviewCapability = true };
        var links = new List<FakeGatt>();
        var transport = new BleLineTransport(() => { var link = new FakeGatt(peer); links.Add(link); return link; });
        await using var client = new HubClient(transport, TimeSpan.FromMilliseconds(200)); await client.ConnectAsync();
        Check(client.ManagementChannel == ManagementChannel.Ble && client.Capabilities!.SupportsBleManagement && client.Status!.BleManagementState == 2, "real Core handshake over byte-fragment mock");
        Check(links[0].Requests.Select(s => s.Split(' ')[2]).SequenceEqual(new[] { "HELLO", "GET", "STATUS" }), "connection only reads, no replay or permission grant");
        var bank = client.Configuration!.Bank; bank.Profiles[0] = bank.Profiles[0] with { Name = "BLE tested" };
        var saved = await client.SaveAsync(bank, 1);
        Check(saved.Bank.Profiles[0].Name == "BLE tested" && !saved.Preview && peer.PersistedHex == BankCodec.ToHex(bank), "long fragmented SAVE requires matching ACK/GET/status");
        Check(links[0].Requests.Count(s => s.Contains(" SAVE ", StringComparison.Ordinal)) == 1 && links[0].Writes.All(b => b.Length <= 20), "one SAVE only, all fragments <=20");
        int before = links[0].Requests.Count; await AnimationError("FORBIDDEN", () => client.SetBleManagementAsync(true));
        Check(links[0].Requests.Count == before, "permission grant cannot be sent through wireless transport");
        await client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle); await client.StopAnimationPreviewAsync();
        Check(!client.Status!.Armed && client.Status.ManagementOwner == 0, "normal animation APIs work over GATT without inventing owner or output");
        peer.Intercept = line => peer.Emit($"HUB {line.Split(' ')[1]} ERR code=BUSY");
        int busyBefore = links[0].Requests.Count;
        await AnimationError("BUSY", () => client.SaveAsync(bank, saved.Revision));
        Check(links[0].Requests.Count == busyBefore + 1 && client.Configuration is null, "lease conflict never retries SAVE and requires fresh config");
        peer.Intercept = line => peer.Emit($"HUB {line.Split(' ')[1]} ERR code=LOCKED");
        await AnimationError("LOCKED", () => client.RefreshStatusAsync());
        Check(client.State == HubConnectionState.Disconnected && client.Status is null && client.Capabilities is null && links[0].CloseCount == 1, "revoked permission closes management and clears caches");
        peer.Intercept = null; await client.ConnectAsync();
        Check(links.Count == 2 && links[1].Requests.Select(s => s.Split(' ')[2]).SequenceEqual(new[] { "HELLO", "GET", "STATUS" }), "new GATT instance reconnect reads only");
        Check(links.SelectMany(l => l.Requests).Select(s => s.Split(' ')[1]).Distinct().Count() == links.Sum(l => l.Requests.Count), "IDs never reused across byte-transport reconnect");
    }

    private static async Task BleDisconnectRevocation()
    {
        foreach (bool revokeAck in new[] { true, false }) foreach (bool cccdSuccess in new[] { true, false })
        {
            var peer = new FakeDevice { Channel = ManagementChannel.Ble, BleManagementCapability = true, BleManagementState = 2 };
            var gatt = new FakeGatt(peer) { CloseBehavior = () => cccdSuccess ? Task.CompletedTask : Task.FromException(new IOException("CCCD None failed")) };
            var transport = new BleLineTransport(() => gatt);
            var client = new HubClient(transport, TimeSpan.FromMilliseconds(50)); await client.ConnectAsync();
            await client.ControlAsync("OUTPUT 1"); int beforeDisconnect = gatt.Requests.Count;
            peer.Intercept = line =>
            {
                if (line.EndsWith("CONTROL MANAGE 0", StringComparison.Ordinal))
                { if (revokeAck) peer.DefaultReply(line); else peer.Emit($"HUB {line.Split(' ')[1]} OK ble_manage=2 ble_manage_remaining_ms=0 armed=0"); }
                else throw new Exception("Disconnect sent a command other than self-revoke.");
            };
            if (!revokeAck && !cccdSuccess)
                await ThrowsAsync<IOException>(client.DisconnectAsync, "failed revoke and CCCD surfaced, not silently accepted");
            else await client.DisconnectAsync();
            Check(gatt.Requests.Skip(beforeDisconnect).Select(s => string.Join(' ', s.Split(' ').Skip(2))).SequenceEqual(new[] { "CONTROL MANAGE 0" }), "exactly one self revoke, no STATUS after permission removed");
            Check(client.State == HubConnectionState.Disconnected && client.Status is null && client.Configuration is null && client.Capabilities is null, "all disconnect branches clear client state");
            Check(gatt.CloseCount == 1 && !gatt.IsOpen, "failed remote confirmation still disposes local session once");
            if (revokeAck) Check(peer.BleManagementState == 0, "acknowledged self revoke closed peer grant");
            Check(peer.Armed, "management self revoke/disconnect never changes previously armed HID");
            await client.DisposeAsync();
            Check(gatt.Requests.Count == beforeDisconnect + 1, "dispose after disconnect never duplicates revoke or replays SAVE");
        }
        var already = new FakeDevice { Channel = ManagementChannel.Ble, BleManagementCapability = true, BleManagementState = 2 };
        var lockedGatt = new FakeGatt(already) { CloseBehavior = () => Task.FromException(new IOException("unsubscribe unavailable")) };
        var lockedClient = new HubClient(new BleLineTransport(() => lockedGatt)); await lockedClient.ConnectAsync();
        already.Intercept = line => already.Emit($"HUB {line.Split(' ')[1]} ERR code=LOCKED");
        await lockedClient.DisconnectAsync();
        Check(lockedClient.State == HubConnectionState.Disconnected && lockedGatt.CloseCount == 1, "LOCKED proves permission already unavailable even if CCCD close fails");
        await lockedClient.DisposeAsync();
    }
}

internal sealed class FakeGatt : IGattByteConnection
{
    public event Action<ReadOnlyMemory<byte>>? NotificationReceived;
    public event Action<Exception?>? Disconnected;
    public List<byte[]> Writes { get; } = [];
    public List<string> Requests { get; } = [];
    public int OpenCount { get; private set; }
    public int CloseCount { get; private set; }
    public bool IsOpen { get; private set; }
    public Func<CancellationToken, Task>? OpenBehavior { get; init; }
    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? WriteBehavior { get; set; }
    public Func<Task>? CloseBehavior { get; init; }
    private readonly LineFramer incoming = new(HubProtocol.MaxRequestLength);
    private readonly FakeDevice? peer;
    private bool closed;
    public FakeGatt(FakeDevice? peer = null)
    {
        this.peer = peer;
        if (peer is not null) peer.LineReceived += Reply;
    }
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); OpenCount++;
        if (OpenBehavior is not null) await OpenBehavior(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); if (closed) throw new IOException("Closed GATT generation"); IsOpen = true;
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!IsOpen) throw new IOException("GATT disconnected"); Writes.Add(bytes.ToArray());
        if (WriteBehavior is not null) await WriteBehavior(bytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); if (!IsOpen) throw new IOException("GATT disconnected");
        foreach (string line in incoming.Feed(bytes.Span)) { Requests.Add(line); if (peer?.Intercept is { } intercept) intercept(line); else peer?.DefaultReply(line); }
    }
    public void Emit(ReadOnlyMemory<byte> bytes) => NotificationReceived?.Invoke(bytes);
    public Action<ReadOnlyMemory<byte>>? CaptureNotification() => NotificationReceived;
    public void Cut() { IsOpen = false; Disconnected?.Invoke(new IOException("Radio disconnected")); }
    private void Reply(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        for (int offset = 0; offset < bytes.Length; offset += 7) Emit(bytes.AsMemory(offset, Math.Min(7, bytes.Length - offset)));
    }
    public Task CloseAsync()
    {
        if (!closed) { closed = true; IsOpen = false; CloseCount++; if (peer is not null) peer.LineReceived -= Reply; }
        return CloseBehavior?.Invoke() ?? Task.CompletedTask;
    }
}
