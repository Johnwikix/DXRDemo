using DXRDemo.Hdr;
using DXRDemo.DXR;
using DXRDemo.SuperResolution;
using DXRDemo.Camera;
using DXRDemo.Scene;
using DXRDemo.Assets.Importers;
using System.IO;
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

public sealed class RayTracePass : IShaderPass, IRenderDiagnostics
{
    public static int RainyCornerSceneIndex => 0;
    public static int RtxptKitchenSceneIndex => 1;
    public static int RtxptConvergenceSceneIndex => 2;
    public static int RtxptBistroSceneIndex => 3;
    public static int PackagedSceneCount => DxrDemoChoices.Length;

    private static readonly SceneChoice RainyCornerChoice = new("雨后便利店 · Rainy Corner", "RainyCorner.glb", 2019650);
    private static readonly SceneChoice[] DxrDemoChoices =
    [
        RainyCornerChoice,
        new("RTXPT · Kitchen", RtxptAssets.KitchenPath, 1474751),
        new("RTXPT · Convergence Test", RtxptAssets.ConvergencePath, 46110),
        new("RTXPT · Bistro 街角", RtxptAssets.BistroPath, 4209006)
    ];

    private sealed record Settings(int Scene = 0, RayTraceDenoiserMode Denoiser = RayTraceDenoiserMode.NrdRelax,
        int Bounces = 10, int Samples = 1, Float2 Orbit = default, float Distance = 2.7f, Float2 Mouse = default, int Revision = 0,
        ReconstructionMode Reconstruction = ReconstructionMode.DlssRayReconstruction, int RenderScale = 50, int CameraReset = 0,
        SceneAsset? Asset = null, float Environment = 1, float Exposure = 0, SunLightSettings Sun = default, bool ExternalLightingEnabled = true);

    private readonly object _settingsLock = new();
    private Settings _settings = new(Scene: RtxptKitchenSceneIndex, Orbit: new Float2(0, 0.165f), Sun: SunLightSettings.Default);
    private Settings Snapshot => Volatile.Read(ref _settings);

    private void Change(Func<Settings, Settings> update)
    {
        lock (_settingsLock)
        {
            var next = update(_settings);
            if (next != _settings) Volatile.Write(ref _settings, next with { Revision = _settings.Revision + 1 });
        }
    }

    private DxrDevice? _device;
    private GpuFrameEffects? _effects;
    private int _frame, _revision = -1, _width, _height;
    private HdrRenderParameters _hdr;
    private bool _disposed;
    private volatile bool _loading;
    private GpuTexture<Float4>? _raw;
    private GpuTexture<Rgba32>? _normal;
    private GpuTexture<Float4>? _surfaces;
    private GpuTexture<Float4>? _normalRoughness;
    private SuperResolutionRenderer? _reconstruction;
    private Settings? _previousSettings;
    private TimeSpan _lastRenderTime;
    private readonly CameraController _camera = new();
    private int _cameraCut = -1;
    private readonly DxrSceneBackend _sceneBackend = new();
    private PbrSignals? _pbrSignals;
    private SceneAsset[] _importedScenes = [];
    private readonly SceneAsset?[] _packagedScenes = new SceneAsset?[PackagedSceneCount];
    private readonly Task<SceneDocument>?[] _packagedLoads = new Task<SceneDocument>?[PackagedSceneCount];
    private readonly CancellationTokenSource _demoCancellation = new();
    private sealed record PendingImport(SceneDocument Document, TaskCompletionSource<SceneDocument> Completion, CancellationToken Cancellation);
    private PendingImport? _pendingImport;

    public IReadOnlyList<SceneChoice> Scenes { get; private set; } = DxrDemoChoices;
    public CameraController Camera => _camera;
    internal bool SupportsOptix => Snapshot.Asset != null;
    public float EnvironmentIntensity { get => Snapshot.Environment; set => Change(s => s with { Environment = Math.Clamp(value, 0, 100) }); }
    public bool ExternalLightingEnabled { get => Snapshot.ExternalLightingEnabled; set => Change(s => s with { ExternalLightingEnabled = value }); }
    public float Exposure { get => Snapshot.Exposure; set => Change(s => s with { Exposure = Math.Clamp(value, -16, 16) }); }

    public SunLightSettings Sun
    {
        get => Snapshot.Sun;
        set
        {
            if (!float.IsFinite(value.Azimuth) || !float.IsFinite(value.Elevation) || !float.IsFinite(value.Intensity) || !float.IsFinite(value.AngularDiameter)) return;
            var normalized = value with
            {
                Azimuth = (value.Azimuth % 360 + 360) % 360,
                Elevation = Math.Clamp(value.Elevation, -90, 90),
                Intensity = Math.Clamp(value.Intensity, 0, 100),
                AngularDiameter = Math.Clamp(value.AngularDiameter, 0, 20)
            };
            Change(s => s with { Sun = normalized });
        }
    }

    public int SceneIndex
    {
        get => Snapshot.Scene;
        set
        {
            lock (_settingsLock)
            {
                int index = Math.Clamp(value, 0, Scenes.Count - 1);
                int importedIndex = index - DxrDemoChoices.Length;
                SceneAsset? asset = index < DxrDemoChoices.Length ? _packagedScenes[index]
                    : importedIndex >= 0 && importedIndex < _importedScenes.Length ? _importedScenes[importedIndex] : null;
                Change(s => s with
                {
                    Scene = index,
                    Asset = asset,
                    Orbit = new(0, .165f),
                    Exposure = index != s.Scene && index is >= 1 and <= 3
                        ? RtxptAssets.InitialExposure((RtxptScene)(index - 1)) : s.Exposure,
                    Mouse = default,
                    CameraReset = s.CameraReset + 1
                });
                if (asset != null) _camera.FrameScene(asset, _height > 0 ? _width / (float)_height : 16f / 9);
            }
        }
    }

    public void ResetCamera() => _camera.Reset();

    private bool PreparePackagedScene(GpuTexture<Rgba64> target)
    {
        Settings selected = Snapshot;
        int packaged = selected.Scene;
        if ((uint)packaged >= (uint)PackagedSceneCount || selected.Asset != null)
        {
            _loading = false;
            return true;
        }
        Task<SceneDocument> load = _packagedLoads[packaged] ??= packaged == 0
            ? GltfImporter.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", RainyCornerChoice.FileName), _demoCancellation.Token)
            : RtxptAssets.LoadAsync((RtxptScene)(packaged - 1), _demoCancellation.Token);
        _loading = !load.IsCompleted;
        if (_loading)
        {
            _effects!.DrawLoading(target);
            return false;
        }
        try
        {
            SceneDocument document = load.GetAwaiter().GetResult();
            lock (_settingsLock)
            {
                _packagedScenes[packaged] = document.Scenes[document.DefaultScene];
                if (_settings.Scene == selected.Scene && _settings.Asset == null)
                    SceneIndex = selected.Scene;
            }
            return true;
        }
        catch
        {
            lock (_settingsLock)
            {
                if (_settings.Scene == selected.Scene && _settings.Asset == null)
                {
                    int fallback = Array.FindIndex(_packagedScenes, asset => asset != null);
                    SceneIndex = fallback >= 0 ? fallback : RainyCornerSceneIndex;
                }
            }
            _packagedLoads[packaged] = null;
            throw;
        }
    }

    public async Task<SceneDocument> ImportAsync(string path, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var document = await GltfImporter.LoadAsync(path, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<SceneDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingImport(document, completion, cancellation);
        var previous = Interlocked.Exchange(ref _pendingImport, pending);
        previous?.Completion.TrySetCanceled();
        return await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private void ApplyPendingImport()
    {
        var pending = Interlocked.Exchange(ref _pendingImport, null);
        if (pending == null) return;
        try
        {
            pending.Cancellation.ThrowIfCancellationRequested();
            var document = pending.Document;
            SceneAsset asset = document.Scenes[document.DefaultScene];
            _sceneBackend.SetScene(asset, pending.Cancellation);
            _importedScenes = document.Scenes;
            var choices = new SceneChoice[DxrDemoChoices.Length + document.Scenes.Length];
            Array.Copy(DxrDemoChoices, choices, DxrDemoChoices.Length);
            for (int i = 0; i < document.Scenes.Length; i++)
                choices[DxrDemoChoices.Length + i] = new($"{Path.GetFileName(document.Path)} · {document.Scenes[i].Name}", document.Path, document.Scenes[i].TriangleCount);
            Scenes = choices;
            SceneIndex = DxrDemoChoices.Length + document.DefaultScene;
            pending.Completion.TrySetResult(document);
        }
        catch (OperationCanceledException) { pending.Completion.TrySetCanceled(pending.Cancellation); }
        catch (Exception error) { pending.Completion.TrySetException(error); }
    }

    public RayTraceDenoiserMode DenoiserMode { get => Snapshot.Denoiser; set => Change(s => s with { Denoiser = value }); }
    public int MaxBounces { get => Snapshot.Bounces; set => Change(s => s with { Bounces = Math.Clamp(value, 1, 32) }); }
    public int Samples { get => Snapshot.Samples; set => Change(s => s with { Samples = Math.Clamp(value, 1, 16) }); }

    public ReconstructionMode ReconstructionMode
    {
        get => Snapshot.Reconstruction;
        set
        {
            if (value is not (ReconstructionMode.Off or ReconstructionMode.Fsr or ReconstructionMode.XeSS or ReconstructionMode.Dlss or ReconstructionMode.DlssRayReconstruction))
                throw new ArgumentOutOfRangeException(nameof(value));
            Change(s => s with { Reconstruction = value });
        }
    }

    public int RenderScalePercent { get => Snapshot.RenderScale; set => Change(s => s with { RenderScale = Math.Clamp(value, 1, 100) }); }
    public ReconstructionStatus? ReconstructionStatus => Volatile.Read(ref _reconstruction)?.Status;
    internal uint NrdDispatchCount => _reconstruction?.NrdDispatchCount ?? 0;
    internal uint OptixDispatchCount => _reconstruction?.OptixDispatchCount ?? 0;

    public string DiagnosticText
    {
        get
        {
            var s = Snapshot;
            string model = s.Asset is { } asset
                ? $"{asset.Name}  {asset.TriangleCount:N0} TRIANGLES  {asset.Instances.Length} INSTANCES  {asset.Lights.Length} LIGHTS"
                : Scenes[s.Scene].Name;
            var sr = ReconstructionStatus;
            string reconstruction = sr == null ? "SR INITIALIZING" : sr.Active == ReconstructionMode.Off
                ? (sr.Requested == ReconstructionMode.Off ? "SR OFF / NATIVE" : $"{sr.Requested} UNAVAILABLE / NATIVE")
                : $"SR {sr.Active}  {sr.InputWidth}x{sr.InputHeight} -> {sr.OutputWidth}x{sr.OutputHeight}";
            string denoiser = sr?.Active == ReconstructionMode.DlssRayReconstruction ? "DLSS RAY RECONSTRUCTION" : s.Denoiser == RayTraceDenoiserMode.NrdRelax
                ? (sr?.NrdActive == true ? "NRD RELAX" : "TEMPORAL (NRD UNAVAILABLE)") : s.Denoiser == RayTraceDenoiserMode.Optix
                ? (sr?.OptixActive == true ? "OPTIX AI" : "TEMPORAL (OPTIX UNAVAILABLE)") : s.Denoiser.ToString().ToUpperInvariant();
            return $"DXR RTXPT\n{model}{(_loading ? "  LOADING" : "")}\n{s.Samples} SPP  {s.Bounces} BOUNCES  DENOISE {denoiser}\n{reconstruction}";
        }
    }

    public string Id => "ray-trace";
    public string DisplayName => "Path trace";
    public string Description => "PBR scenes, RTXPT transport, HDR10 and temporal reconstruction.";
    public ShaderAuthor Author { get; } = new("RT Demo", null, "See Assets/Models/SOURCES.md for model terms");
    public string? OriginalUrl => null;
    public ShaderCapabilities Capabilities => ShaderCapabilities.UsesMouse | ShaderCapabilities.UsesResolution;

    public void SetMouse(float x, float y, float panelWidth, float panelHeight)
    {
        Float2 orbit = new((x / Math.Max(1, panelWidth) - .5f) * 6.2831853f, Math.Clamp((.5f - y / Math.Max(1, panelHeight)) * 2.4f, -.1f, 1.4f));
        Settings old = Snapshot;
        _camera.Rotate(orbit.X - old.Orbit.X, orbit.Y - old.Orbit.Y);
        Change(s => s with { Mouse = new(x, panelHeight - y), Orbit = orbit });
    }

    public void SetZoom(float delta) => _camera.Zoom(delta);

    public void Initialize(DxrDevice device, RenderSize initialSize)
    {
        _device = device;
        _sceneBackend.Initialize(device);
        _effects = new(device);
    }

    public void OnResize(RenderSize size) { }

    public bool TryExecute(GpuTexture<Rgba64> texture, int width, int height, TimeSpan time, object? parameter)
    {
        ApplyPendingImport();
        if (!PreparePackagedScene(texture)) return true;
        Settings s = Snapshot;
        if (s.Asset == null)
        {
            _effects!.DrawLoading(texture);
            return true;
        }

        SunLightSettings sun = s.Sun with { Enabled = s.ExternalLightingEnabled && s.Sun.Enabled };
        float environment = s.ExternalLightingEnabled ? s.Environment : 0;
        var hdr = parameter is HdrRenderParameters p ? p : HdrRenderParameters.Default;
        if (_reconstruction == null) Volatile.Write(ref _reconstruction, new SuperResolutionRenderer(_device!));
        _reconstruction.Configure(s.Reconstruction, s.RenderScale, width, height,
            s.Denoiser == RayTraceDenoiserMode.NrdRelax, pbr: true, s.Denoiser == RayTraceDenoiserMode.Optix);
        if (!_reconstruction.LinearPipelineActive)
            throw new InvalidOperationException("PBR 线性管线初始化失败：" + _reconstruction.Status.Message);

        bool nrd = _reconstruction.Status.NrdActive;
        RayTraceDenoiserMode denoiserMode = s.Denoiser == RayTraceDenoiserMode.NrdRelax && !nrd ? RayTraceDenoiserMode.TemporalOnly : s.Denoiser;
        int inputWidth = _reconstruction.InputWidth;
        int inputHeight = _reconstruction.InputHeight;
        EnsureTextures(inputWidth, inputHeight);
        double elapsed = _lastRenderTime == TimeSpan.Zero || time == TimeSpan.Zero ? 1.0 / 60 : (time - _lastRenderTime).TotalSeconds;
        CameraFrame camera = _camera.Advance(elapsed, out _, out int cameraCut);
        if (_revision != s.Revision || _hdr != hdr)
        {
            _frame = 0;
            _revision = s.Revision;
            _hdr = hdr;
        }

        bool lightingCut = cameraCut != _cameraCut;
        if (_previousSettings is not Settings old || old.CameraReset != s.CameraReset || old.Scene != s.Scene || old.Asset != s.Asset || cameraCut != _cameraCut ||
            old.Samples != s.Samples || old.Bounces != s.Bounces || old.Denoiser != s.Denoiser || old.Environment != s.Environment || old.Sun != s.Sun || old.ExternalLightingEnabled != s.ExternalLightingEnabled)
            _reconstruction.Reset();
        _cameraCut = cameraCut;
        Float2 jitter = _reconstruction.Jitter();
        _sceneBackend.SetScene(s.Asset);
        if (nrd && _sceneBackend.StablePlanesEnabled) _reconstruction.PrepareStablePlanes();
        _sceneBackend.Trace(camera, _frame, s.Samples, s.Bounces, jitter, environment, _raw!, _normal!, _surfaces!, _normalRoughness!,
            _pbrSignals!, sun, _reconstruction.Active == ReconstructionMode.DlssRayReconstruction, lightingCut, nrd,
            optixGuides: s.Denoiser == RayTraceDenoiserMode.Optix && _reconstruction.Active != ReconstructionMode.DlssRayReconstruction,
            textureLodBias: _reconstruction.TextureLodBias,
            stabilizeGlass: (_reconstruction.Active is ReconstructionMode.Dlss or ReconstructionMode.XeSS or ReconstructionMode.Fsr) &&
                (denoiserMode is RayTraceDenoiserMode.TemporalOnly or RayTraceDenoiserMode.Optix));
        bool produced = _reconstruction.Execute(_raw!, _normal!, _surfaces!, texture, s.Orbit, s.Distance, jitter, denoiserMode, hdr, elapsed,
            _normalRoughness, camera, _pbrSignals, s.Exposure, directLight: null, nrd ? _sceneBackend : null);
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
        _demoCancellation.Cancel();
        _demoCancellation.Dispose();
        Interlocked.Exchange(ref _pendingImport, null)?.Completion.TrySetCanceled();
        _reconstruction?.Dispose();
        _sceneBackend.Dispose();
        _effects?.Dispose();
        DisposeTextures();
    }
}

public sealed record SceneChoice(string Name, string FileName, int TriangleCount)
{
    public override string ToString() => $"{Name} · {TriangleCount:N0} triangles";
}
