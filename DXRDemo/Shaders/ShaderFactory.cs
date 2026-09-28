namespace DXRDemo.Shaders;

/// <summary>
/// Lazy lifetime manager for <see cref="IShaderPass"/> instances.
/// Each Id is created at most once; subsequent calls return the cached
/// instance. All instances are disposed together by <see cref="DisposeAll"/>.
/// </summary>
public sealed class ShaderFactory : IDisposable
{
    private readonly Dictionary<string, IShaderPass> _instances = new();
    private bool _disposed;

    public ShaderFactory()
    {
        if (ShaderCatalog.All.Count == 0)
        {
            throw new InvalidOperationException(
                "ShaderCatalog is empty — at least one shader must be registered.");
        }
    }

    public static IReadOnlyList<ShaderAuthoringInfo> Catalog => ShaderCatalog.All;

    public IShaderPass GetOrCreate(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_instances.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var info = ShaderCatalog.All.FirstOrDefault(
            x => string.Equals(x.Id, id, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Unknown shader id '{id}'. Registered: {string.Join(", ", ShaderCatalog.All.Select(x => x.Id))}.",
                nameof(id));

        var created = info.Factory();
        _instances[id] = created;
        return created;
    }

    public IShaderPass GetOrCreateDefault() => GetOrCreate(ShaderCatalog.All[0].Id);

    public void DisposeAll()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var instance in _instances.Values)
        {
            try { instance.Dispose(); }
            catch { /* swallow — Dispose must be idempotent */ }
        }
        _instances.Clear();
    }

    public void Dispose() => DisposeAll();
}