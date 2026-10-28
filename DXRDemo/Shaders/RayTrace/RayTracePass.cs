using DXRDemo.Hdr;
using DXRDemo.DXR;
using DXRDemo.SuperResolution;
using DXRDemo.Camera;
using DXRDemo.Scene;
using DXRDemo.Rendering;
using Float2 = System.Numerics.Vector2;
using Float4 = System.Numerics.Vector4;

namespace DXRDemo.Shaders.RayTrace;

public enum RayTraceDenoiserMode
{
    None = 0,
    TemporalOnly = 1,
    NrdRelax = 2,
    Optix = 3
}

public sealed class RayTracePass : ISceneShaderPass, IRenderDiagnostics
{
    private sealed record Settings(RayTraceDenoiserMode Denoiser = RayTraceDenoiserMode.NrdRelax,
        int Bounces = 10, int Samples = 1, int Revision = 0);
    private readonly object _settingsLock = new();
    private Settings _settings = new();
    private Settings Snapshot => Volatile.Read(ref _settings);
    private void Change(Func<Settings, Settings> update)
    {
        lock (_settingsLock)
        {
            Settings next = update(_settings);
            if (next != _settings) Volatile.Write(ref _settings, next with { Revision = _settings.Revision + 1 });
        }
    }

    private DxrDevice? _device;
    private GpuFrameEffects? _effects;
    private int _frame, _revision = -1, _sessionRevision = -1, _width, _height;
    private HdrRenderParameters _hdr;
    private bool _disposed;
    private readonly bool _ownsSession;
    private readonly SceneRenderer _renderer;
    private readonly bool _ownsRenderer;
    private GpuTexture<Float4>? _raw;
    private GpuTexture<Rgba32>? _normal;
    private GpuTexture<Float4>? _surfaces, _normalRoughness;
    private PathTracingResolve? _reconstruction;
    private Settings? _previousSettings;
    private SceneSessionSnapshot? _previousScene;
    private TimeSpan _lastRenderTime;
    private int _cameraCut = -1;
    private readonly DxrSceneBackend _sceneBackend;
    private PbrSignals? _pbrSignals;

    public RayTracePass() : this(new SceneSession(), true, null) { }
    public RayTracePass(SceneSession session) : this(session, false, null) { }
    internal RayTracePass(SceneSession session, GpuSceneCache scenes, SceneRenderer renderer) : this(session, false, scenes, renderer) { }
    private RayTracePass(SceneSession session, bool ownsSession, GpuSceneCache? scenes, SceneRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        _ownsSession = ownsSession;
        _sceneBackend = scenes == null ? new DxrSceneBackend() : new DxrSceneBackend(scenes);
        _renderer = renderer ?? new SceneRenderer(); _ownsRenderer = renderer == null;
    }

    public SceneSession Session { get; }
    public IReadOnlyList<SceneChoice> Scenes => Session.Scenes;
    public CameraController Camera => Session.Camera;
    internal bool SupportsOptix => Session.Snapshot.Asset != null;
    public float EnvironmentIntensity { get => Session.EnvironmentIntensity; set => Session.EnvironmentIntensity = value; }
    public bool ExternalLightingEnabled { get => Session.ExternalLightingEnabled; set => Session.ExternalLightingEnabled = value; }
    public float Exposure { get => Session.Exposure; set => Session.Exposure = value; }
    public SunLightSettings Sun { get => Session.Sun; set => Session.Sun = value; }
    public int SceneIndex { get => Session.SceneIndex; set => Session.SceneIndex = value; }
    public void ResetCamera() => Session.ResetCamera();
    public Task<SceneDocument> ImportAsync(string path, CancellationToken cancellation = default) => Session.ImportAsync(path, cancellation);

    public RayTraceDenoiserMode DenoiserMode { get => Snapshot.Denoiser; set => Change(s => s with { Denoiser = value }); }
    public int MaxBounces { get => Snapshot.Bounces; set => Change(s => s with { Bounces = Math.Clamp(value, 1, 32) }); }
    public int Samples { get => Snapshot.Samples; set => Change(s => s with { Samples = Math.Clamp(value, 1, 16) }); }

    public ReconstructionMode ReconstructionMode { get => Session.ReconstructionMode; set => Session.ReconstructionMode = value; }
    public int RenderScalePercent { get => Session.RenderScalePercent; set => Session.RenderScalePercent = value; }
    public ReconstructionStatus? ReconstructionStatus => Session.ReconstructionStatus;
    internal uint NrdDispatchCount => _reconstruction?.NrdDispatchCount ?? 0;
    internal uint OptixDispatchCount => _reconstruction?.OptixDispatchCount ?? 0;

    public string DiagnosticText
    {
        get
        {
            var s = Snapshot;
            var scene = Session.Snapshot;
            string model = scene.Asset is { } asset
                ? $"{asset.Name}  {asset.TriangleCount:N0} TRIANGLES  {asset.Instances.Length} INSTANCES  {asset.Lights.Length} LIGHTS"
                : scene.Scenes[scene.SceneIndex].Name;
            var sr = ReconstructionStatus;
            string reconstruction = sr == null ? "SR INITIALIZING" : sr.Active == ReconstructionMode.Off
                ? (sr.Requested == ReconstructionMode.Off ? "SR OFF / NATIVE" : $"{sr.Requested} UNAVAILABLE / NATIVE")
                : $"SR {sr.Active}  {sr.InputWidth}x{sr.InputHeight} -> {sr.OutputWidth}x{sr.OutputHeight}";
            string denoiser = sr?.Active == ReconstructionMode.DlssRayReconstruction ? "DLSS RAY RECONSTRUCTION" : s.Denoiser == RayTraceDenoiserMode.NrdRelax
                ? (sr?.NrdActive == true ? "NRD RELAX" : "TEMPORAL (NRD UNAVAILABLE)") : s.Denoiser == RayTraceDenoiserMode.Optix
                ? (sr?.OptixActive == true ? "OPTIX AI" : "TEMPORAL (OPTIX UNAVAILABLE)") : s.Denoiser.ToString().ToUpperInvariant();
            return $"DXR RTXPT\n{model}{(Session.IsLoading ? "  LOADING" : "")}\n{s.Samples} SPP  {s.Bounces} BOUNCES  DENOISE {denoiser}\n{reconstruction}";
        }
    }

    public string Id => "ray-trace";
    public string DisplayName => "Path trace";
    public string Description => "PBR scenes, RTXPT transport, HDR10 and temporal reconstruction.";
    public ShaderAuthor Author { get; } = new("RT Demo", null, "See Assets/Models/SOURCES.md for model terms");
    public string? OriginalUrl => null;
    public ShaderCapabilities Capabilities => ShaderCapabilities.UsesMouse | ShaderCapabilities.UsesResolution;

    public void SetMouse(float x, float y, float panelWidth, float panelHeight) => Session.SetMouse(x, y, panelWidth, panelHeight);
    public void SetZoom(float delta) => Session.SetZoom(delta);

    public void Initialize(DxrDevice device, RenderSize initialSize)
    {
        _device = device;
        _renderer.Initialize(device);
        _sceneBackend.Initialize(device);
        _effects = new(device);
    }

    public void OnResize(RenderSize size) { }

    public bool TryExecute(GpuTexture<Rgba64> texture, int width, int height, TimeSpan time, object? parameter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_device == null) throw new InvalidOperationException("Initialize before rendering.");
        Session.PrepareFrame(_sceneBackend, width, height);
        SceneSessionSnapshot scene = Session.Snapshot;
        Settings s = Snapshot;
        if (scene.Asset == null)
        {
            _effects!.DrawLoading(texture);
            return true;
        }

        SunLightSettings sun = scene.Sun with { Enabled = scene.ExternalLightingEnabled && scene.Sun.Enabled };
        float environment = scene.ExternalLightingEnabled ? scene.Environment : 0;
        var hdr = parameter is HdrRenderParameters p ? p : HdrRenderParameters.Default;
        if (_reconstruction == null) Volatile.Write(ref _reconstruction, new PathTracingResolve(_device!, _renderer.Reconstruction, _renderer.Display));
        if (_renderer.Activate(this)) { _reconstruction.Reset(); _previousSettings = null; }
        _reconstruction.Configure(scene.Reconstruction, scene.RenderScale, width, height,
            s.Denoiser == RayTraceDenoiserMode.NrdRelax, pbr: true, s.Denoiser == RayTraceDenoiserMode.Optix);
        Session.PublishReconstructionStatus(_reconstruction.Status);
        if (!_reconstruction.LinearPipelineActive)
            throw new InvalidOperationException("PBR 线性管线初始化失败：" + _reconstruction.Status.Message);

        bool nrd = _reconstruction.Status.NrdActive;
        RayTraceDenoiserMode denoiserMode = s.Denoiser == RayTraceDenoiserMode.NrdRelax && !nrd ? RayTraceDenoiserMode.TemporalOnly : s.Denoiser;
        int inputWidth = _reconstruction.InputWidth;
        int inputHeight = _reconstruction.InputHeight;
        EnsureTextures(inputWidth, inputHeight);
        double elapsed = _lastRenderTime == TimeSpan.Zero || time == TimeSpan.Zero ? 1.0 / 60 : (time - _lastRenderTime).TotalSeconds;
        CameraFrame camera = Camera.Advance(elapsed, out _, out int cameraCut);
        if (_revision != s.Revision || _sessionRevision != scene.Revision || _hdr != hdr)
        {
            _frame = 0;
            _revision = s.Revision;
            _sessionRevision = scene.Revision;
            _hdr = hdr;
        }

        bool lightingCut = cameraCut != _cameraCut;
        if (_previousSettings != s || _previousScene is not { } previous || previous.SceneRevision != scene.SceneRevision ||
            previous.Environment != scene.Environment || previous.Sun != scene.Sun ||
            previous.ExternalLightingEnabled != scene.ExternalLightingEnabled || cameraCut != _cameraCut)
            _reconstruction.Reset();
        _cameraCut = cameraCut;
        Float2 jitter = _reconstruction.Jitter();
        _sceneBackend.SetScene(scene.Asset);
        if (nrd && _sceneBackend.StablePlanesEnabled) _reconstruction.PrepareStablePlanes();
        _sceneBackend.Trace(camera, _frame, s.Samples, s.Bounces, jitter, environment, _raw!, _normal!, _surfaces!, _normalRoughness!,
            _pbrSignals!, sun, _reconstruction.Active == ReconstructionMode.DlssRayReconstruction, lightingCut, nrd,
            optixGuides: s.Denoiser == RayTraceDenoiserMode.Optix && _reconstruction.Active != ReconstructionMode.DlssRayReconstruction,
            textureLodBias: _reconstruction.TextureLodBias,
            stabilizeGlass: (_reconstruction.Active is ReconstructionMode.Dlss or ReconstructionMode.XeSS or ReconstructionMode.Fsr) &&
                (denoiserMode is RayTraceDenoiserMode.TemporalOnly or RayTraceDenoiserMode.Optix));
        bool produced = _reconstruction.Execute(_raw!, _normal!, _surfaces!, texture, default, 2.7f, jitter, denoiserMode, hdr, elapsed,
            _normalRoughness, camera, _pbrSignals, scene.Exposure, directLight: null, nrd ? _sceneBackend : null,
            revisions: new(scene.SceneRevision, scene.Revision, cameraCut, _renderer.PipelineRevision));
        Session.PublishReconstructionStatus(_reconstruction.Status);
        _previousScene = scene;
        _previousSettings = s;
        _lastRenderTime = time;
        _frame = Math.Min(_frame + 1, int.MaxValue - 1);
        return produced;
    }

    private void EnsureTextures(int width, int height)
    {
        if (_raw != null && _width == width && _height == height && _normalRoughness != null && _pbrSignals != null) return;
        DisposeTextures();
        var device = _device ?? throw new InvalidOperationException("Initialize before allocating textures.");
        _raw = device.CreateTexture2D<Float4>(width, height);
        _normal = device.CreateTexture2D<Rgba32>(width, height);
        _surfaces = device.CreateTexture2D<Float4>(width, height);
        _normalRoughness = device.CreateTexture2D<Float4>(width, height);
        _pbrSignals = new(device, width, height);
        _width = width;
        _height = height;
        _frame = 0;
    }

    private void DisposeTextures()
    {
        _raw?.Dispose();
        _normal?.Dispose();
        _surfaces?.Dispose();
        _normalRoughness?.Dispose();
        _pbrSignals?.Dispose();
        _raw = null;
        _normal = null;
        _surfaces = null;
        _normalRoughness = null;
        _pbrSignals = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var cleanup = new ResourceCleanup();
        if (_ownsSession) cleanup.Release(Session);
        cleanup.Release(_reconstruction); cleanup.Release(_sceneBackend); cleanup.Release(_effects);
        cleanup.Run(DisposeTextures);
        if (_ownsRenderer) cleanup.Release(_renderer);
        cleanup.Finish();
    }
}
