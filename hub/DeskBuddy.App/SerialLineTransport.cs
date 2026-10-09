using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using DeskBuddy.Core;
using Microsoft.Win32;

namespace DeskBuddy.App;

public sealed record PortCandidate(string PortName, string Label)
{
    public override string ToString() => Label;
}

public static class FeatherDiscovery
{
    public static PortCandidate[] Scan()
    {
        // Enumerate only the approved USB VID/PID. Enumeration never opens a port.
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, PortCandidate>(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return [];
        foreach (var device in usb.GetSubKeyNames())
        {
            if (!Regex.IsMatch(device, @"^VID_239A&PID_8029(?:&|$)", RegexOptions.IgnoreCase)) continue;
            using var key = usb.OpenSubKey(device);
            if (key is null) continue;
            foreach (var instance in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(instance);
                using var parameters = child?.OpenSubKey("Device Parameters");
                var port = parameters?.GetValue("PortName") as string;
                if (port is null || !Regex.IsMatch(port, @"^COM[0-9]+$", RegexOptions.IgnoreCase) || !present.Contains(port)) continue;
                result[port] = new PortCandidate(port, $"{port}  ·  Adafruit Feather 239A:8029");
            }
        }
        return result.Values.OrderBy(p => p.PortName.Length).ThenBy(p => p.PortName).ToArray();
    }
}

// All SerialPort calls are owned by one worker. No WPF thread waits on serial I/O.
public sealed class SerialLineTransport(string portName) : IManagementTransport
{
    public ManagementChannel Channel => ManagementChannel.Usb;
    public string PortName => portName;
    public event Action<string>? LineReceived;
    public event Action<Exception?>? Disconnected;
    private readonly ConcurrentQueue<(string Line, CancellationToken Token, TaskCompletionSource Completion)> writes = new();
    private readonly SerialWorkerLifetime worker = new(portName);
    private CancellationToken workerToken;
    private bool closing;
    public bool IsClosing => worker.IsClosing;
    private volatile bool isOpen;
    private readonly object gate = new();

    public async Task OpenAsync(CancellationToken token)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Start(workerLifetime => Run(opened, workerLifetime), token);
        await opened.Task.WaitAsync(token);
    }

    private void Run(TaskCompletionSource opened, CancellationToken token)
    {
        Exception? error = null;
        try
        {
            using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                Encoding = Encoding.ASCII, NewLine = "\n", DtrEnable = true, RtsEnable = false,
                ReadTimeout = 100, WriteTimeout = 500, Handshake = Handshake.None
            };
            token.ThrowIfCancellationRequested();
            port.Open();
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (closing) throw new OperationCanceledException("Serial connection was closed during opening.", token);
                workerToken = token; isOpen = true;
            }
            opened.TrySetResult();
            var line = new StringBuilder();
            bool discard = false;
            while (!token.IsCancellationRequested)
            {
                while (writes.TryDequeue(out var item))
                {
                    if (token.IsCancellationRequested) { item.Completion.TrySetCanceled(token); break; }
                    if (item.Token.IsCancellationRequested) { item.Completion.TrySetCanceled(item.Token); continue; }
                    try { port.Write(item.Line + "\n"); item.Completion.TrySetResult(); }
                    catch (Exception e) { item.Completion.TrySetException(e); throw; }
                }
                string incoming = port.ReadExisting();
                if (token.IsCancellationRequested) break;
                foreach (char ch in incoming)
                {
                    if (ch == '\n')
                    {
                        if (!discard && line.Length > 0) LineReceived?.Invoke(line.ToString().TrimEnd('\r'));
                        line.Clear(); discard = false;
                    }
                    else if (!discard)
                    {
                        if (line.Length >= 4096) { discard = true; line.Clear(); }
                        else line.Append(ch);
                    }
                }
                Thread.Sleep(8);
            }
        }
        catch (OperationCanceledException) { opened.TrySetCanceled(token); }
        catch (Exception ex) { error = ex; opened.TrySetException(ex); }
        finally
        {
            lock (gate)
            {
                isOpen = false;
                while (writes.TryDequeue(out var item)) item.Completion.TrySetException(new IOException("Connection closed; command was not replayed."));
            }
            Disconnected?.Invoke(error);
        }
    }

    public Task SendLineAsync(string line, CancellationToken token)
    {
        if (line.Length > 511 || line.Any(c => c > 127 || c == '\r' || c == '\n')) throw new ArgumentException("Invalid protocol line.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!isOpen || closing || workerToken.IsCancellationRequested) throw new IOException("Serial connection is closed.");
            if (writes.Count >= 32) throw new IOException("Serial command queue is full.");
            writes.Enqueue((line, token, done));
        }
        return done.Task.WaitAsync(token);
    }

    public async Task CloseAsync()
    {
        lock (gate)
        {
            closing = true; isOpen = false;
            while (writes.TryDequeue(out var item)) item.Completion.TrySetException(new IOException("Connection closing; command was not replayed."));
        }
        await worker.CloseAsync();
    }
}
