using DeskBuddy.Core;
using System.IO;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace DeskBuddy.App;

public sealed record BleCandidate(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class BleDiscovery
{
    // Paired-device enumeration does not pair, open a GATT connection or probe unrelated services.
    public static async Task<BleCandidate[]> ScanAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                Array.Empty<string>(), DeviceInformationKind.AssociationEndpoint).AsTask(linked.Token).WaitAsync(linked.Token);
            return devices.Where(d => d.Pairing.IsPaired).Select(d => new BleCandidate(d.Id,
                string.IsNullOrWhiteSpace(d.Name) ? "Unnamed paired BLE device" : d.Name)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Paired BLE device discovery timed out. Check Windows Bluetooth and try again explicitly.", error); }
    }
}

/// <summary>Windows GATT adapter. Only the selected, already-paired device is opened.
/// The portable BleLineTransport owns deadlines, ASCII/LF framing and session generation.</summary>
public sealed class WindowsBleGattConnection(string deviceId, Dispatcher uiDispatcher) : IGattByteConnection
{
    public static readonly Guid ServiceUuid = new("8e7b0001-6f9a-4c4d-9f32-2f3c1d0b5a60");
    public static readonly Guid RxUuid = new("8e7b0002-6f9a-4c4d-9f32-2f3c1d0b5a60");
    public static readonly Guid TxUuid = new("8e7b0003-6f9a-4c4d-9f32-2f3c1d0b5a60");
    private readonly object gate = new();
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? rx, tx;
    private bool closed, ready;
    public event Action<ReadOnlyMemory<byte>>? NotificationReceived;
    public event Action<Exception?>? Disconnected;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        lock (gate) if (device is not null || closed) throw new InvalidOperationException("Create a fresh BLE adapter for each session.");
        BluetoothLEDevice created = await uiDispatcher.InvokeAsync(() =>
            Await(BluetoothLEDevice.FromIdAsync(deviceId), cancellationToken, item => item?.Dispose())).Task.Unwrap();
        if (created is null) throw new IOException("Windows could not open this paired BLE device. Check Bluetooth and device availability.");
        lock (gate)
        {
            if (closed || cancellationToken.IsCancellationRequested) { created.Dispose(); cancellationToken.ThrowIfCancellationRequested(); throw new IOException("BLE opening was cancelled."); }
            device = created;
        }
        if (!created.DeviceInformation.Pairing.IsPaired) throw new HubProtocolException("NOT_PAIRED", "Pair Desk Buddy in Windows Bluetooth Settings first. Hub never pairs or removes bonds automatically.");
        DeviceAccessStatus access = await uiDispatcher.InvokeAsync(() => Await(created.RequestAccessAsync(), cancellationToken)).Task.Unwrap();
        if (access != DeviceAccessStatus.Allowed) throw new HubProtocolException("BLE_ACCESS_DENIED", "Windows denied access to this Bluetooth device. No settings were changed.");
        async Task<IReadOnlyList<GattDeviceService>> Discover(bool cachedFullTable, CancellationToken token)
        {
            IAsyncOperation<GattDeviceServicesResult> operation;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (closed) throw new IOException("BLE session closed during service discovery.");
                operation = cachedFullTable ? created.GetGattServicesAsync(BluetoothCacheMode.Cached) :
                    created.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            }
            var result = await Await(operation, token, late => { foreach (var candidate in late.Services) candidate.Dispose(); });
            if (result.Status != GattCommunicationStatus.Success)
            {
                foreach (var candidate in result.Services) candidate.Dispose();
                throw GattError(cachedFullTable ? "read cached service table" : "discover management service", result.Status, result.ProtocolError);
            }
            return result.Services;
        }
        // Some Windows bonded-service databases return Success/0 for the filtered query while the full cached table is current.
        // One read-only fallback shares the same token/deadline; characteristic and HELLO verification remain mandatory.
        GattDeviceService selected = await GattServiceDiscovery.SelectAsync(ServiceUuid,
            token => Discover(false, token), token => Discover(true, token), candidate => candidate.Uuid, cancellationToken);
        lock (gate)
        {
            if (closed || cancellationToken.IsCancellationRequested) { selected.Dispose(); cancellationToken.ThrowIfCancellationRequested(); throw new IOException("BLE discovery was cancelled."); }
            service = selected;
        }
        var characteristics = await Await(selected.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), cancellationToken);
        if (characteristics.Status != GattCommunicationStatus.Success) throw GattError("discover management characteristics", characteristics.Status, characteristics.ProtocolError);
        var receive = characteristics.Characteristics.SingleOrDefault(c => c.Uuid == RxUuid);
        var notify = characteristics.Characteristics.SingleOrDefault(c => c.Uuid == TxUuid);
        if (receive is null || notify is null || !receive.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
            !notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            throw new HubProtocolException("UPGRADE_REQUIRED", "BLE management requires RX Write-with-response and TX Notify. The device service is incompatible.");
        // Pairing alone is not permission. Windows must encrypt the link, and firmware checks its own local grant.
        receive.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
        notify.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
        IAsyncOperation<GattWriteResult> subscription;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (closed) throw new IOException("BLE session was closed during discovery.");
            rx = receive; tx = notify;
            created.ConnectionStatusChanged += ConnectionChanged;
            notify.ValueChanged += ValueChanged;
            // Submit while holding the lifecycle lock. Close can then only enqueue None after this Notify submission.
            subscription = notify.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }
        var subscribe = await Await(subscription, cancellationToken);
        if (subscribe.Status != GattCommunicationStatus.Success) throw GattError("subscribe to management replies", subscribe.Status, subscribe.ProtocolError);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (closed) throw new IOException("BLE session was closed during subscription.");
            ready = true;
        }
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length is < 1 or > BleLineTransport.FragmentBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        using var writer = new DataWriter(); writer.WriteBytes(bytes.ToArray());
        var buffer = writer.DetachBuffer();
        IAsyncOperation<GattWriteResult> operation;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (closed || !ready || rx is null) throw new IOException("BLE management is not connected.");
            // Submit atomically with the closed/ready check. Once Close acquires this lock no old fragment can be submitted.
            operation = rx.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse);
        }
        var result = await Await(operation, cancellationToken);
        if (result.Status != GattCommunicationStatus.Success) throw GattError("write management fragment", result.Status, result.ProtocolError);
    }
    private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[]? bytes = null; bool oversized = false;
        lock (gate)
        {
            if (closed || !ReferenceEquals(sender, tx)) return;
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            oversized = reader.UnconsumedBufferLength > 512;
            if (!oversized) { bytes = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(bytes); }
        }
        // Never invoke the outer transport while holding the adapter lock: Close travels in the opposite direction.
        if (oversized) Disconnected?.Invoke(new IOException("Unexpectedly large BLE notification."));
        else if (bytes is not null) NotificationReceived?.Invoke(bytes);
    }
    private void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        bool lost = false;
        lock (gate)
            if (!closed && ready && ReferenceEquals(sender, device) && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            { ready = false; lost = true; }
        if (lost) Disconnected?.Invoke(new IOException("BLE management link disconnected. Nothing will be replayed."));
    }
    public async Task CloseAsync()
    {
        GattCharacteristic? notify; GattDeviceService? ownedService; BluetoothLEDevice? ownedDevice;
        lock (gate)
        {
            if (closed) return;
            closed = true; ready = false; notify = tx; ownedService = service; ownedDevice = device;
            tx = rx = null; service = null; device = null;
            if (notify is not null) notify.ValueChanged -= ValueChanged;
            if (ownedDevice is not null) ownedDevice.ConnectionStatusChanged -= ConnectionChanged;
        }
        try
        {
            // CCCD disable is the firmware session boundary even while Windows keeps its HID connection alive.
            if (notify is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var result = await Await(notify.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None), timeout.Token);
                if (result.Status != GattCommunicationStatus.Success)
                    throw GattError("unsubscribe from management replies", result.Status, result.ProtocolError);
            }
        }
        catch (Exception error) { throw new IOException("BLE CCCD unsubscribe was not confirmed. Local management revocation or the firmware idle timeout may still be required.", error); }
        finally { ownedService?.Dispose(); ownedDevice?.Dispose(); }
    }
    private static IOException GattError(string operation, GattCommunicationStatus status, byte? protocolError) =>
        new($"Could not {operation}: {status}" + (protocolError is byte error ? $" (ATT {error:X2})" : "") + ". Check pairing, encrypted link, device-local Hub permission and availability; no command is retried.");

    private static async Task<T> Await<T>(IAsyncOperation<T> operation, CancellationToken cancellationToken, Action<T>? lateCleanup = null)
    {
        var pending = operation.AsTask();
        try { return await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Windows may finish a GATT discovery after caller cancellation. Never publish its late result or leak its references.
            try { operation.Cancel(); } catch (Exception) { }
            _ = pending.ContinueWith(done =>
                {
                    try { if (done.IsCompletedSuccessfully) lateCleanup?.Invoke(done.Result); else _ = done.Exception; }
                    catch (Exception) { /* The caller already received cancellation; never publish a late result. */ }
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
