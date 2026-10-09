namespace DeskBuddy.Core;

/// <summary>Read-only discovery policy. Delegates return only successful enumeration results and retain the caller's deadline.
/// An empty exact query gets one cached full-table fallback. All unselected service references are disposed.</summary>
public static class GattServiceDiscovery
{
    public static async Task<T> SelectAsync<T>(Guid expectedUuid,
        Func<CancellationToken, Task<IReadOnlyList<T>>> exactUncached,
        Func<CancellationToken, Task<IReadOnlyList<T>>> allCached,
        Func<T, Guid> uuid, CancellationToken cancellationToken) where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(exactUncached); ArgumentNullException.ThrowIfNull(allCached); ArgumentNullException.ThrowIfNull(uuid);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<T> candidates = await exactUncached(cancellationToken).ConfigureAwait(false);
        T? selected = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidates.Count == 0)
            {
                // Never retry exceptions, failed statuses, user commands, or a nonempty inconsistent exact result.
                candidates = await allCached(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            foreach (T candidate in candidates)
            {
                if (candidate is null) throw new FormatException("Windows returned an invalid GATT service reference.");
                if (uuid(candidate) != expectedUuid) continue;
                if (selected is not null) { selected = null; throw new FormatException("Multiple matching BLE management services; refusing ambiguous selection."); }
                selected = candidate;
            }
            if (selected is null)
                throw new HubProtocolException("GATT_SERVICE_MISSING", "Windows did not discover the Desk Buddy management service. Select the correct device, renew its local permission and reconnect explicitly. If firmware lacks BLE management, upgrade through USB.");
            return selected;
        }
        catch { selected = null; throw; }
        finally
        {
            var disposed = new HashSet<T>(ReferenceEqualityComparer.Instance);
            foreach (T candidate in candidates)
                if (candidate is not null && !ReferenceEquals(candidate, selected) && disposed.Add(candidate))
                    try { candidate.Dispose(); } catch (Exception) { /* Attempt every cleanup; do not replace the original discovery error. */ }
        }
    }
}
