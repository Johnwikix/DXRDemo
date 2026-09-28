using ComputeSharp;
using DXRDemo.Hdr;

namespace DXRDemo.Shaders.RayTrace;

public enum RayTraceDenoiserMode { None, TemporalOnly, Relax }

/// <summary>Both trace backends feed the same HDR encoder and temporal / SVGF-style filters.</summary>
public sealed class RayTracePass : IShaderPass, IRenderDiagnostics
{
    private sealed record Settings(int Scene = 0, RayTraceDenoiserMode Denoiser = RayTraceDenoiserMode.Relax,
        int Bounces = 10, int Samples = 2, Float2 Orbit = default, float Distance = 2.7f, Float2 Mouse = default, int Revision = 0);
    private readonly object _settingsLock = new();
    private Settings _settings = new(Orbit: new Float2(0, 0.165f));
    private Settings Snapshot => Volatile.Read(ref _settings);
    private void Change(Func<Settings, Settings> update)
    {
        lock (_settingsLock)
        {
            var next = update(_settings);
            if (next != _settings) Volatile.Write(ref _settings, next with { Revision = _settings.Revision + 1 });
        }
    }
    private readonly IMeshTraceBackend _backend;
    private GraphicsDevice? _device;
    private int _frame, _revision = -1, _width, _height;
    private HdrRenderParameters _hdr;
    private bool _disposed;
    private volatile bool _loading;
    private ReadWriteTexture2D<Float4>? _raw;
    private ReadWriteTexture2D<Rgba64, Float4>? _signal, _historyA, _historyB, _filterA, _filterB;
    private ReadWriteTexture2D<R16, float>? _momentA, _momentB;
    private ReadWriteTexture2D<Rgba32, Float4>? _normal;
    public RayTracePass() : this(new SoftwareMeshBackend()) { }
    public RayTracePass(IMeshTraceBackend backend)
    {
        _backend = backend;
        Scenes = backend is SoftwareMeshBackend
            ? [.. MeshData.Choices, new("Original spheres", "", 0)] : MeshData.Choices;
    }
    public IReadOnlyList<MeshChoice> Scenes { get; }
    public int SceneIndex { get => Snapshot.Scene; set => Change(s => s with { Scene = Math.Clamp(value, 0, Scenes.Count - 1), Orbit = new(value == 1 ? MathF.PI : 0, 0.165f), Distance = value == 3 ? 6.5f : 2.7f, Mouse = default }); }
    public RayTraceDenoiserMode DenoiserMode { get => Snapshot.Denoiser; set => Change(s => s with { Denoiser = value }); }
    public int MaxBounces { get => Snapshot.Bounces; set => Change(s => s with { Bounces = Math.Clamp(value, 1, 32) }); }
    public int Samples { get => Snapshot.Samples; set => Change(s => s with { Samples = Math.Clamp(value, 1, 16) }); }
    public string DiagnosticText
    {
        get
        {
            var s = Snapshot;
            string model = s.Scene < 3 ? $"{MeshData.Choices[s.Scene].Name.ToUpperInvariant()}  {MeshData.Choices[s.Scene].TriangleCount:N0} TRIANGLES" : "ORIGINAL SPHERES";
            return $"{_backend.Name}\n{model}{(_loading ? "  LOADING" : "")}\n{s.Samples} SPP  {s.Bounces} BOUNCES  DENOISE {s.Denoiser.ToString().ToUpperInvariant()}";
        }
    }
    public string Id => "ray-trace";
    public string DisplayName => "Path trace";
    public string Description => "Stanford meshes, HDR10 and temporal / SVGF-style denoising.";
    public ShaderAuthor Author { get; } = new("RT Demo", null, "See Assets/Models/SOURCES.md for model terms");
    public string? OriginalUrl => null;
    public ShaderCapabilities Capabilities => ShaderCapabilities.UsesMouse | ShaderCapabilities.UsesResolution;
    public void SetMouse(float x, float y, float panelWidth, float panelHeight) => Change(s => s with
    {
        Mouse = new(x, panelHeight - y),
        Orbit = new((x / Math.Max(1, panelWidth) - 0.5f) * 6.2831853f, Math.Clamp((0.5f - y / Math.Max(1, panelHeight)) * 2.4f, -0.1f, 1.4f))
    });
    public void SetZoom(float delta) => Change(s => s with { Distance = Math.Clamp(s.Distance * MathF.Exp(-delta * 0.1f), 1, 50) });
    public void Initialize(GraphicsDevice device, Int2 initialSize) { _device = device; _backend.Initialize(device); }
    public void OnResize(Int2 size) { }

    public bool TryExecute(ReadWriteTexture2D<Rgba64, Float4> texture, int width, int height, TimeSpan time, object? parameter)
    {
        var device = _device ?? throw new InvalidOperationException("Initialize before rendering.");
        Settings s = Snapshot;
        var hdr = parameter is HdrRenderParameters p ? p : HdrRenderParameters.Default;
        EnsureTextures(width, height);
        if (_revision != s.Revision || _hdr != hdr) { _frame = 0; _revision = s.Revision; _hdr = hdr; }
        var signal = s.Denoiser == RayTraceDenoiserMode.None ? texture : _signal!;
        if (s.Scene < MeshData.Choices.Length)
        {
            var load = MeshData.LoadAsync(s.Scene);
            _loading = !load.IsCompleted;
            if (_loading) { device.ForEach(texture, new LoadingShader()); return true; }
            MeshData mesh = load.GetAwaiter().GetResult();
            _backend.Trace(mesh, width, height, s.Samples, s.Bounces, _frame, s.Orbit, s.Distance, _raw!, _normal!);
            device.ForEach(signal, new MeshEncodeShader(_raw!, hdr.IsHdrEnabled, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits));
        }
        else device.ForEach(signal, new RayTraceShader((float)time.TotalSeconds, s.Mouse, new(width, height), _frame, s.Distance,
            hdr.IsHdrEnabled, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits, s.Bounces, s.Samples, _normal!));
        if (s.Denoiser == RayTraceDenoiserMode.TemporalOnly)
            device.ForEach(texture, new NaiveTemporalAccumulationShader(_frame, _signal!, _historyA!));
        else if (s.Denoiser == RayTraceDenoiserMode.Relax)
        {
            Float2 res = new(width, height);
            device.ForEach(_historyB!, new TemporalAccumulationShader(_frame, res, _signal!, _historyA!, _momentA!, _momentB!));
            device.ForEach(_filterA!, new SpatialFilterShader(0, res, _historyB!, _signal!, _normal!));
            device.ForEach(_filterB!, new SpatialFilterShader(1, res, _filterA!, _signal!, _normal!));
            device.ForEach(_filterA!, new SpatialFilterShader(2, res, _filterB!, _signal!, _normal!));
            device.ForEach(_filterB!, new SpatialFilterShader(3, res, _filterA!, _signal!, _normal!));
            device.ForEach(texture, new SpatialFilterShader(4, res, _filterB!, _signal!, _normal!));
            (_historyA, _historyB) = (_historyB, _historyA);
            (_momentA, _momentB) = (_momentB, _momentA);
        }
        _frame = Math.Min(_frame + 1, int.MaxValue - 1);
        return true;
    }
    private void EnsureTextures(int width, int height)
    {
        if (_raw != null && _width == width && _height == height) return;
        DisposeTextures();
        var device = _device ?? throw new InvalidOperationException("Initialize before allocating textures.");
        _raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        _normal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        _signal = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _historyA = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _historyB = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _filterA = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _filterB = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _momentA = device.AllocateReadWriteTexture2D<R16, float>(width, height);
        _momentB = device.AllocateReadWriteTexture2D<R16, float>(width, height);
        _width = width; _height = height; _frame = 0;
    }
    private void DisposeTextures()
    {
        _raw?.Dispose(); _normal?.Dispose(); _signal?.Dispose(); _historyA?.Dispose(); _historyB?.Dispose();
        _filterA?.Dispose(); _filterB?.Dispose(); _momentA?.Dispose(); _momentB?.Dispose();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; DisposeTextures(); _backend.Dispose(); }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct LoadingShader : IComputeShader<Float4>
{
    public Float4 Execute() => new(0.025f, 0.03f, 0.04f, 1);
}
