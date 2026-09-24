using CORA.Core.Security;

namespace CORA.Data.Tests.App;

/// <summary>Dictionary-backed <see cref="IPreferenceStore"/> so consent can be tested without MAUI Preferences.</summary>
internal sealed class InMemoryPreferenceStore : IPreferenceStore
{
    public Dictionary<string, string> Values { get; } = new();

    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string value) => Values[key] = value;

    public void Remove(string key) => Values.Remove(key);
}
