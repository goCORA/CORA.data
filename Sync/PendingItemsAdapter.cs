namespace CORA.Data.Sync;

/// <summary>
/// Keeps sync's hands off items that are still waiting for "Keep these with …"
/// (claude/design-per-account-ownership.md): items without an owner that the account hasn't
/// answered for yet ("Not now", "Don't ask me again"). They aren't this account's, so they are
/// never sent to its cloud; but they aren't gone either, so the account's cloud copy of them is
/// not deleted, and nothing arriving from the cloud is written over them. Items the account chose
/// to leave without an owner are not pending: those do leave its cloud.
/// </summary>
public sealed class PendingItemsAdapter(ISyncAdapter inner, IReadOnlySet<string> pending) : ISyncAdapter
{
    public string Kind => inner.Kind;

    public Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);

    public Task AlignAsync(IReadOnlyDictionary<string, string> cloudItems, CancellationToken cancellationToken) =>
        inner.AlignAsync(cloudItems, cancellationToken);

    public Task ApplyAsync(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken) =>
        inner.ApplyAsync(
            upserts.Where(u => !pending.Contains(u.Key)).ToDictionary(u => u.Key, u => u.Value),
            deletes.Where(d => !pending.Contains(d)).ToList(),
            cancellationToken);

    public async Task<IReadOnlySet<string>> GetElsewhereAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(await inner.GetElsewhereAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(pending);
        return ids;
    }

    public async Task<IReadOnlySet<string>> GetNotAppliedAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(await inner.GetNotAppliedAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(pending);
        return ids;
    }

    public (string Item, string Detail) Describe(string itemId) => inner.Describe(itemId);
}
