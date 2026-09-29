using ComputeSharp;
using DXRDemo.Hdr;
using DXRDemo.DXR;
using DXRDemo.SuperResolution;

namespace DXRDemo.Shaders.RayTrace;

public enum RayTraceDenoiserMode
{
    None, TemporalOnly, Relax,
    /// <summary>Uses the complete NVIDIA NRD RELAX diffuse denoiser for the diffuse-only mesh renderer.</summary>
    NrdRelax
}

/// <summary>Both trace backends feed the same HDR encoder and temporal / SVGF-style filters.</summary>
public sealed class RayTracePass : IShaderPass, IRenderDiagnostics
{
    private sealed record Settings(int Scene = 0, RayTraceDenoiserMode Denoiser = RayTraceDenoiserMode.NrdRelax,
        int Bounces = 10, int Samples = 2, Float2 Orbit = default, float Distance = 2.7f, Float2 Mouse = default, int Revision = 0,
        ReconstructionMode Reconstruction = ReconstructionMode.Off, int RenderScale = 67, int CameraReset = 0);
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
    private ReadWriteTexture2D<Float4>? _surfaces;
    private ReadWriteTexture2D<Float4>? _normalRoughness;
    private SuperResolutionRenderer? _reconstruction;
    private Settings? _previousSettings;
    private TimeSpan _lastRenderTime;
    private bool _loadingFramePresented;
    public RayTracePass() : this(new SoftwareMeshBackend()) { }
    public RayTracePass(IMeshTraceBackend backend)
    {
        _backend = backend;
        Scenes = backend is SoftwareMeshBackend
            ? [.. MeshData.Choices, new("Original spheres", "", 0)] : MeshData.Choices;
    }
    public IReadOnlyList<MeshChoice> Scenes { get; }
    public int SceneIndex { get => Snapshot.Scene; set => Change(s => s with { Scene = Math.Clamp(value, 0, Scenes.Count - 1), Orbit = new(value == 1 ? MathF.PI : 0, 0.165f), Distance = value == 3 ? 6.5f : 2.7f, Mouse = default, CameraReset = s.CameraReset + 1 }); }
    public RayTraceDenoiserMode DenoiserMode { get => Snapshot.Denoiser; set => Change(s => s with { Denoiser = value }); }
    public int MaxBounces { get => Snapshot.Bounces; set => Change(s => s with { Bounces = Math.Clamp(value, 1, 32) }); }
    public int Samples { get => Snapshot.Samples; set => Change(s => s with { Samples = Math.Clamp(value, 1, 16) }); }
    /// <summary>Gets or sets the requested temporal super-resolution algorithm.</summary>
    public ReconstructionMode ReconstructionMode
    {
        get => Snapshot.Reconstruction;
        set
        {
            if (value is not (ReconstructionMode.Off or ReconstructionMode.Fsr or ReconstructionMode.XeSS or ReconstructionMode.Dlss))
                throw new ArgumentOutOfRangeException(nameof(value));
            Change(s => s with { Reconstruction = value });
        }
    }
    /// <summary>Gets or sets the exact input-size percentage used when super resolution is active.</summary>
    public int RenderScalePercent { get => Snapshot.RenderScale; set => Change(s => s with { RenderScale = Math.Clamp(value, 1, 100) }); }
    /// <summary>Gets the most recently published capabilities and actual reconstruction mode.</summary>
    public ReconstructionStatus? ReconstructionStatus => Volatile.Read(ref _reconstruction)?.Status;
    internal uint NrdDispatchCount => _reconstruction?.NrdDispatchCount ?? 0;
    public string DiagnosticText
    {
        get
        {
            var s = Snapshot;
            string model = s.Scene < 3 ? $"{MeshData.Choices[s.Scene].Name.ToUpperInvariant()}  {MeshData.Choices[s.Scene].TriangleCount:N0} TRIANGLES" : "ORIGINAL SPHERES";
            var sr = ReconstructionStatus;
            string reconstruction = sr == null ? "SR INITIALIZING" : sr.Active == ReconstructionMode.Off
                ? (sr.Requested == ReconstructionMode.Off ? "SR OFF / NATIVE" : $"{sr.Requested} UNAVAILABLE / NATIVE")
                : $"SR {sr.Active}  {sr.InputWidth}x{sr.InputHeight} -> {sr.OutputWidth}x{sr.OutputHeight}";
            string denoiser = s.Denoiser == RayTraceDenoiserMode.NrdRelax
                ? (sr?.NrdActive == true ? "NRD RELAX" : "LEGACY RELAX (NRD UNAVAILABLE)") : s.Denoiser.ToString().ToUpperInvariant();
            return $"{_backend.Name}\n{model}{(_loading ? "  LOADING" : "")}\n{s.Samples} SPP  {s.Bounces} BOUNCES  DENOISE {denoiser}\n{reconstruction}";
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
        MeshData? mesh = null;
        if (s.Scene < MeshData.Choices.Length)
        {
            var load = MeshData.LoadAsync(s.Scene);
            _loading = !load.IsCompleted;
            if (_loading || !_loadingFramePresented)
            {
                _loadingFramePresented = true;
                device.ForEach(texture, new LoadingShader());
                return true;
            }
            mesh = load.GetAwaiter().GetResult();
        }
        if (_reconstruction != null || _backend is DxrMeshBackend || s.Reconstruction != ReconstructionMode.Off || s.Denoiser == RayTraceDenoiserMode.NrdRelax)
        {
            if (_reconstruction == null) Volatile.Write(ref _reconstruction, new SuperResolutionRenderer(device));
            _reconstruction.Configure(mesh == null ? ReconstructionMode.Off : s.Reconstruction, s.RenderScale, width, height,
                mesh != null && s.Denoiser == RayTraceDenoiserMode.NrdRelax);
        }
        bool temporal = _reconstruction is { LinearPipelineActive: true };
        bool nrd = _reconstruction?.Status.NrdActive == true;
        RayTraceDenoiserMode denoiserMode = s.Denoiser == RayTraceDenoiserMode.NrdRelax && !nrd ? RayTraceDenoiserMode.Relax : s.Denoiser;
        int inputWidth = temporal ? _reconstruction!.InputWidth : width;
        int inputHeight = temporal ? _reconstruction!.InputHeight : height;
        EnsureTextures(inputWidth, inputHeight, temporal, nrd);
        if (_revision != s.Revision || _hdr != hdr) { _frame = 0; _revision = s.Revision; _hdr = hdr; }
        if (temporal)
        {
            if (_previousSettings is not Settings old || old.CameraReset != s.CameraReset || old.Scene != s.Scene ||
                old.Samples != s.Samples || old.Bounces != s.Bounces || old.Denoiser != s.Denoiser)
                _reconstruction!.Reset();
            Float2 jitter = _reconstruction!.Jitter();
            _backend.Trace(mesh!, inputWidth, inputHeight, s.Samples, s.Bounces, _frame, s.Orbit, s.Distance, _raw!, _normal!, _surfaces!, jitter, _normalRoughness);
            double elapsed = _lastRenderTime == TimeSpan.Zero ? 1.0 / 60 : (time - _lastRenderTime).TotalSeconds;
            // Deterministic offscreen callers may intentionally pass TimeSpan.Zero on every frame.
            if (time == TimeSpan.Zero) elapsed = 1.0 / 60;
            bool produced = _reconstruction.Execute(_raw!, _normal!, _surfaces!, texture, s.Orbit, s.Distance, jitter, denoiserMode, hdr, elapsed, _normalRoughness);
            _previousSettings = s; _lastRenderTime = time;
            _frame = Math.Min(_frame + 1, int.MaxValue - 1);
            return produced;
        }
        _previousSettings = s; _lastRenderTime = time;
        var signal = denoiserMode == RayTraceDenoiserMode.None ? texture : _signal!;
        if (mesh != null)
        {
            _backend.Trace(mesh, width, height, s.Samples, s.Bounces, _frame, s.Orbit, s.Distance, _raw!, _normal!);
            device.ForEach(signal, new MeshEncodeShader(_raw!, hdr.IsHdrEnabled, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits));
        }
        else device.ForEach(signal, new RayTraceShader((float)time.TotalSeconds, s.Mouse, new(width, height), _frame, s.Distance,
            hdr.IsHdrEnabled, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits, s.Bounces, s.Samples, _normal!));
        if (denoiserMode == RayTraceDenoiserMode.TemporalOnly)
            device.ForEach(texture, new NaiveTemporalAccumulationShader(_frame, _signal!, _historyA!));
        else if (denoiserMode == RayTraceDenoiserMode.Relax)
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
    private void EnsureTextures(int width, int height, bool temporal, bool nrd)
    {
        if (_raw != null && _width == width && _height == height && (_surfaces != null) == temporal && (_normalRoughness != null) == nrd) return;
        DisposeTextures();
        var device = _device ?? throw new InvalidOperationException("Initialize before allocating textures.");
        _raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        _normal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        if (temporal)
        {
            _surfaces = device.AllocateReadWriteTexture2D<Float4>(width, height);
            if (nrd) _normalRoughness = device.AllocateReadWriteTexture2D<Float4>(width, height);
            _width = width; _height = height; _frame = 0;
            return;
        }
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
        _surfaces?.Dispose(); _surfaces = null;
        _normalRoughness?.Dispose(); _normalRoughness = null;
        _raw = null; _normal = null;
        _signal = _historyA = _historyB = _filterA = _filterB = null;
        _momentA = _momentB = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _reconstruction?.Dispose(); DisposeTextures(); _backend.Dispose(); }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct LoadingShader : IComputeShader<Float4>
{
    public Float4 Execute() => new(0.025f, 0.03f, 0.04f, 1);
}
