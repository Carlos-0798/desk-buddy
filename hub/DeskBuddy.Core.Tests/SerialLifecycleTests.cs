using DeskBuddy.Core;
using DeskBuddy.App;

internal static partial class Program
{
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(2, deadline.Token);
    }

    private static async Task SerialWorkerCleanup()
    {
        Throws<ArgumentOutOfRangeException>(() => new SerialWorkerLifetime("TEST", TimeSpan.Zero), "invalid worker deadline");
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            var unused = new SerialWorkerLifetime("TEST_CANCELLED");
            Throws<OperationCanceledException>(() => unused.Start(_ => throw new Exception("must not run"), alreadyCancelled.Token), "pre-cancelled start");
            Check(!SerialWorkerLifetime.IsReserved("TEST_CANCELLED"), "pre-cancelled start owns no resource");
            await unused.CloseAsync();
        }
        foreach (bool stuckDisposal in new[] { false, true })
        {
            string port = "TEST_STUCK_" + stuckDisposal;
            using var entered = new ManualResetEventSlim();
            using var disposing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            bool disposed = false, cancelled = false;
            int unexpectedStarts = 0;
            var owner = new SerialWorkerLifetime(port, TimeSpan.FromMilliseconds(40));
            owner.Start(token =>
            {
                entered.Set();
                if (stuckDisposal) { token.WaitHandle.WaitOne(); disposing.Set(); }
                release.Wait(); // Models a driver ignoring cancellation in Open or Dispose, without touching a real device.
                cancelled = token.IsCancellationRequested;
                disposed = true;
            }, CancellationToken.None);
            try
            {
                await Until(() => entered.IsSet);
                Check(SerialWorkerLifetime.IsReserved(port.ToLowerInvariant()), "lease is process-wide and case-insensitive");
                var contender = new SerialWorkerLifetime(port.ToLowerInvariant());
                Throws<IOException>(() => contender.Start(_ => unexpectedStarts++, CancellationToken.None), "second owner blocked before close");
                var close = owner.CloseAsync();
                await ThrowsAsync<WorkerClosePendingException>(() => close.WaitAsync(TimeSpan.FromSeconds(2)), "close returns pending rather than hanging");
                Check(owner.IsClosing && SerialWorkerLifetime.IsReserved(port) && !disposed, "timeout retains resource; never fakes Dispose");
                if (stuckDisposal) Check(disposing.IsSet, "blocked specifically inside driver disposal");
                await ThrowsAsync<WorkerClosePendingException>(() => owner.CloseAsync(), "repeat close does not reset the deadline");
                Throws<WorkerClosePendingException>(() => contender.Start(_ => unexpectedStarts++, CancellationToken.None), "new instance cannot steal timed-out resource");
                Check(unexpectedStarts == 0, "rejected opens do not start a worker");
                release.Set();
                await Until(() => !owner.IsClosing && !SerialWorkerLifetime.IsReserved(port));
                await owner.CloseAsync();
                Check(disposed && cancelled, "actual return observes cancellation before lease is released");
                contender.Start(_ => unexpectedStarts++, CancellationToken.None);
                await contender.CloseAsync();
                Check(unexpectedStarts == 1 && !SerialWorkerLifetime.IsReserved(port), "fresh instance allowed only after genuine release");
                await contender.CloseAsync();
                Throws<InvalidOperationException>(() => owner.Start(_ => { }, CancellationToken.None), "old worker instance never reused");
            }
            finally { release.Set(); await Until(() => !SerialWorkerLifetime.IsReserved(port)); }
        }
        var faulted = new SerialWorkerLifetime("TEST_FAULT");
        faulted.Start(_ => throw new InvalidOperationException("simulated driver failure"), CancellationToken.None);
        await ThrowsAsync<InvalidOperationException>(() => faulted.CloseAsync(), "worker fault is observed");
        Check(!faulted.IsClosing && !SerialWorkerLifetime.IsReserved("TEST_FAULT"), "faulted worker releases only after exit");
    }

    private sealed class StuckSerialTransport(bool stuckOpen) : IManagementTransport
    {
        public ManagementChannel Channel => ManagementChannel.Usb;
        public event Action<string>? LineReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public readonly string Resource = "TEST_CORE_" + Guid.NewGuid().ToString("N");
        public readonly List<string> Writes = [];
        public readonly ManualResetEventSlim Release = new();
        private SerialWorkerLifetime? worker;
        public bool WorkerHasExited => worker?.IsClosing != true;
        public async Task OpenAsync(CancellationToken token)
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            worker = new SerialWorkerLifetime(Resource, TimeSpan.FromMilliseconds(40));
            worker.Start(workerToken =>
            {
                if (stuckOpen) { Release.Wait(); return; }
                opened.SetResult();
                workerToken.WaitHandle.WaitOne();
                Release.Wait();
            }, token);
            await opened.Task.WaitAsync(token);
        }
        public Task SendLineAsync(string line, CancellationToken token) { token.ThrowIfCancellationRequested(); Writes.Add(line); return Task.CompletedTask; }
        public Task CloseAsync() => worker?.CloseAsync() ?? Task.CompletedTask;
    }

    private static async Task SerialConnectCleanup()
    {
        foreach (bool stuckOpen in new[] { true, false })
        {
            var serial = new StuckSerialTransport(stuckOpen);
            var client = new HubClient(serial, TimeSpan.FromMilliseconds(25));
            using var connectDeadline = new CancellationTokenSource(stuckOpen ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromSeconds(2));
            try
            {
                await ThrowsAsync<WorkerClosePendingException>(() => client.ConnectAsync(connectDeadline.Token).WaitAsync(TimeSpan.FromSeconds(3)), "failed connection cannot wait forever for cleanup");
                Check(client.State == HubConnectionState.Faulted && client.Configuration is null && client.Status is null, "cleanup timeout cannot publish a ready session");
                Check(client.LastError?.Contains("still closing") == true, "Core surfaces uncertain resource release");
                Check(SerialWorkerLifetime.IsReserved(serial.Resource), "failed connection retains lease");
                Check(serial.Writes.Count == (stuckOpen ? 0 : 2), "no writes during blocked open; no-response path only sends HELLO and legacy STATUS once");
                Check(serial.Writes.All(line => !line.Contains("SAVE") && !line.Contains("OUTPUT")), "failure cleanup never saves or arms output");
                await ThrowsAsync<WorkerClosePendingException>(() => client.DisconnectAsync(), "repeat disconnect remains bounded and honest");
                var next = new SerialWorkerLifetime(serial.Resource);
                Throws<WorkerClosePendingException>(() => next.Start(_ => throw new Exception("must not run"), CancellationToken.None), "new Core transport cannot bypass retained lease");
            }
            finally
            {
                serial.Release.Set();
                // The lease is removed in the worker's finally, just before its Task completes.
                // Wait for both facts before retrying a previously timed-out Close in fixture cleanup.
                await Until(() => !SerialWorkerLifetime.IsReserved(serial.Resource) && serial.WorkerHasExited);
                await client.DisposeAsync();
                serial.Release.Dispose();
            }
        }
    }

    private static void ConnectionNoticeRegression()
    {
        foreach (bool chinese in new[] { true, false })
        {
            var notices = new ConnectionNotices();
            var locked = new HubProtocolException("LOCKED");
            string pollError = notices.Error(locked, chinese);
            Check(pollError.Contains(chinese ? "允许蓝牙连接" : "Allow BLE connection"), "poll error names real local authorization control");
            Check(pollError.Contains(chinese ? "手动重连" : "reconnect manually"), "poll tells user how to recover");
            Check(pollError.Contains(chinese ? "没有重放" : "Nothing was replayed"), "permission recovery never claims automatic retry");
            Check(notices.Disconnected(null, chinese) == pollError, "late generic disconnect cannot overwrite LOCKED guidance");
            Check(notices.Disconnected(new IOException("transport closed"), chinese) == pollError, "late driver notification also preserves guidance");
            notices.Reset();
            Check(notices.Disconnected(null, chinese) != pollError, "new connection clears prior permission notice");
            Check(notices.Error(locked, chinese) == pollError, "disconnect-before-poll order gives same instructions");
            notices.Reset();
            Check(notices.Disconnected(locked, chinese) == pollError, "permission reason on direct disconnect is meaningful too");
            string pending = notices.Error(new WorkerClosePendingException("TEST_PORT"), chinese);
            Check(pending.Contains("TEST_PORT") && pending.Contains(chinese ? "尚未确认释放" : "not confirmed release"), "pending close cannot claim release");
            Check(pending.Contains(chinese ? "退出 Hub" : "exit Hub"), "pending close includes bounded user recovery");
            Check(notices.Error(new IOException("other failure"), chinese) == "other failure", "unrelated failures not relabelled as permission loss");
        }
    }
}
