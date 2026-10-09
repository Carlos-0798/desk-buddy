using DeskBuddy.Core;

internal static partial class Program
{
    private static async Task GattDiscoveryFallback()
    {
        var expected = Guid.Parse("8e7b0001-6f9a-4c4d-9f32-2f3c1d0b5a60");
        var unrelated = Guid.Parse("00001812-0000-1000-8000-00805f9b34fb");
        static Task<IReadOnlyList<TestGattService>> Rows(params TestGattService[] rows) => Task.FromResult<IReadOnlyList<TestGattService>>(rows);
        int fallbackCalls = 0;
        var direct = new TestGattService(expected);
        var selected = await GattServiceDiscovery.SelectAsync(expected, _ => Rows(direct), _ => { fallbackCalls++; return Rows(); }, s => s.Uuid, CancellationToken.None);
        Check(ReferenceEquals(selected, direct) && fallbackCalls == 0 && direct.Disposals == 0, "exact success preserves selected reference and never falls back"); selected.Dispose();

        int exactCalls = 0; fallbackCalls = 0;
        var first = new TestGattService(unrelated); var match = new TestGattService(expected); var last = new TestGattService(Guid.Empty);
        using var suppliedDeadline = new CancellationTokenSource();
        selected = await GattServiceDiscovery.SelectAsync(expected,
            token => { Check(token == suppliedDeadline.Token, "exact discovery shares connection deadline"); exactCalls++; return Rows(); },
            token => { Check(token == suppliedDeadline.Token, "fallback shares original connection deadline"); fallbackCalls++; return Rows(first, match, last); },
            s => s.Uuid, suppliedDeadline.Token);
        Check(exactCalls == 1 && fallbackCalls == 1 && ReferenceEquals(selected, match), "one empty exact result gets exactly one full cached fallback with strict UUID match");
        Check(first.Disposals == 1 && last.Disposals == 1 && match.Disposals == 0, "unselected full-table services disposed; selected kept for validation"); selected.Dispose();

        foreach (bool duplicate in new[] { false, true })
        {
            var a = new TestGattService(duplicate ? expected : unrelated); var b = new TestGattService(duplicate ? expected : Guid.Empty);
            exactCalls = fallbackCalls = 0;
            Task<TestGattService> Attempt() => GattServiceDiscovery.SelectAsync(expected,
                _ => { exactCalls++; return Rows(); }, _ => { fallbackCalls++; return Rows(a, b); }, s => s.Uuid, CancellationToken.None);
            if (duplicate) await ThrowsAsync<FormatException>(async () => await Attempt(), "duplicate UUID rejected");
            else await AnimationError("GATT_SERVICE_MISSING", async () => await Attempt());
            Check(exactCalls == 1 && fallbackCalls == 1 && a.Disposals == 1 && b.Disposals == 1, "missing/duplicate candidates all disposed and never retried");
        }

        var wrong = new TestGattService(unrelated); fallbackCalls = 0;
        await AnimationError("GATT_SERVICE_MISSING", async () => await GattServiceDiscovery.SelectAsync(expected,
            _ => Rows(wrong), _ => { fallbackCalls++; return Rows(new TestGattService(expected)); }, s => s.Uuid, CancellationToken.None));
        Check(fallbackCalls == 0 && wrong.Disposals == 1, "nonempty wrong exact UUID is rejected, not used or silently retried");
        foreach (bool failExact in new[] { true, false })
        {
            exactCalls = fallbackCalls = 0;
            await ThrowsAsync<IOException>(async () => await GattServiceDiscovery.SelectAsync<TestGattService>(expected,
                _ => { exactCalls++; if (failExact) throw new IOException("WinRT or ATT error"); return Rows(); },
                _ => { fallbackCalls++; throw new IOException("Cached enumeration failed"); }, s => s.Uuid, CancellationToken.None), "discovery error propagates");
            Check(exactCalls == 1 && fallbackCalls == (failExact ? 0 : 1), "no fallback on failed exact status/exception; no repeated fallback");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); exactCalls = fallbackCalls = 0;
            await ThrowsAsync<OperationCanceledException>(async () => await GattServiceDiscovery.SelectAsync<TestGattService>(expected,
                _ => { exactCalls++; return Rows(); }, _ => { fallbackCalls++; return Rows(); }, s => s.Uuid, canceled.Token), "pre-canceled discovery");
            Check(exactCalls == 0 && fallbackCalls == 0, "cancelled caller starts no discovery");
        }
        foreach (bool cancelOnFallback in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource(); var owned = new TestGattService(expected); fallbackCalls = 0;
            await ThrowsAsync<OperationCanceledException>(async () => await GattServiceDiscovery.SelectAsync(expected,
                _ => { if (cancelOnFallback) return Rows(); cancel.Cancel(); return Rows(owned); },
                _ => { fallbackCalls++; cancel.Cancel(); return Rows(owned); }, s => s.Uuid, cancel.Token), "cancellation after enumeration abandons service");
            Check(owned.Disposals == 1 && fallbackCalls == (cancelOnFallback ? 1 : 0), "canceled results disposed and no new fallback or selection published");
        }
        var duplicateReference = new TestGattService(expected);
        await ThrowsAsync<FormatException>(async () => await GattServiceDiscovery.SelectAsync(expected,
            _ => Rows(duplicateReference, duplicateReference), _ => Rows(), s => s.Uuid, CancellationToken.None), "same reference repeated is still ambiguous");
        Check(duplicateReference.Disposals == 1, "duplicate reference cleanup occurs once");
    }
    private sealed class TestGattService(Guid uuid) : IDisposable
    {
        public Guid Uuid => uuid;
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
