using ComputeSharp;
using DXRDemo.Hdr;
using DXRDemo.DXR;
using DXRDemo.SuperResolution;
using DXRDemo.Camera;
using DXRDemo.Scene;
using DXRDemo.Assets.Importers;
using System.IO;

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
    /// <summary>Gets the fixed catalog index of the packaged rainy convenience-store demo.</summary>
    public static int RainyCornerSceneIndex => MeshData.Choices.Length;
    private static readonly MeshChoice RainyCornerChoice = new("雨后便利店 · Rainy Corner", "RainyCorner.glb", 2019650);
    private static readonly MeshChoice[] DxrDemoChoices = [.. MeshData.Choices, RainyCornerChoice];
    private sealed record Settings(int Scene = 0, RayTraceDenoiserMode Denoiser = RayTraceDenoiserMode.NrdRelax,
        int Bounces = 10, int Samples = 2, Float2 Orbit = default, float Distance = 2.7f, Float2 Mouse = default, int Revision = 0,
        ReconstructionMode Reconstruction = ReconstructionMode.Off, int RenderScale = 67, int CameraReset = 0,
        SceneAsset? Asset = null, float Environment = 1, float Exposure = 0, SunLightSettings Sun = default, bool ExternalLightingEnabled = true);
    private readonly object _settingsLock = new();
    private Settings _settings = new(Orbit: new Float2(0, 0.165f), Sun: SunLightSettings.Default);
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
    private ReadWriteTexture2D<Float4>? _directLight;
    private SuperResolutionRenderer? _reconstruction;
    private Settings? _previousSettings;
    private TimeSpan _lastRenderTime;
    private bool _loadingFramePresented;
    private readonly CameraController _camera = new();
    private int _cameraRevision = -1, _cameraCut = -1;
    private DxrSceneBackend? _sceneBackend;
    private PbrSignals? _pbrSignals;
    private SceneAsset[] _importedScenes = [];
    private SceneAsset? _rainyCorner;
    private Task<SceneDocument>? _rainyCornerLoad;
    private readonly CancellationTokenSource _demoCancellation = new();
    private sealed record PendingImport(SceneDocument Document, TaskCompletionSource<SceneDocument> Completion, CancellationToken Cancellation);
    private PendingImport? _pendingImport;
    public RayTracePass() : this(new SoftwareMeshBackend()) { }
    public RayTracePass(IMeshTraceBackend backend)
    {
        _backend = backend;
        Scenes = backend is SoftwareMeshBackend
            ? [.. MeshData.Choices, new("Original spheres", "", 0)] : DxrDemoChoices;
    }
    public IReadOnlyList<MeshChoice> Scenes { get; private set; }
    /// <summary>Gets the viewport camera controller shared by all trace and reconstruction paths.</summary>
    public CameraController Camera => _camera;
    /// <summary>Gets or sets environment intensity for imported scenes.</summary>
    public float EnvironmentIntensity { get => Snapshot.Environment; set => Change(s => s with { Environment = Math.Clamp(value, 0, 100) }); }
    /// <summary>Gets or sets the switch for external sun and environment; model lights and emissive materials remain enabled.</summary>
    public bool ExternalLightingEnabled { get => Snapshot.ExternalLightingEnabled; set => Change(s => s with { ExternalLightingEnabled = value }); }
    /// <summary>Gets or sets exposure in stops for the linear output pipeline.</summary>
    public float Exposure { get => Snapshot.Exposure; set => Change(s => s with { Exposure = Math.Clamp(value, -16, 16) }); }
    /// <summary>Gets or sets the additional global sun; changes invalidate lighting history without rebuilding geometry.</summary>
    public SunLightSettings Sun
    {
        get => Snapshot.Sun;
        set
        {
            if (!float.IsFinite(value.Azimuth) || !float.IsFinite(value.Elevation) || !float.IsFinite(value.Intensity)) return;
            var normalized = value with { Azimuth = (value.Azimuth % 360 + 360) % 360,
                Elevation = Math.Clamp(value.Elevation, -90, 90), Intensity = Math.Clamp(value.Intensity, 0, 100) };
            Change(s => s with { Sun = normalized });
        }
    }
    /// <summary>Gets or sets the selected packaged model or imported scene.</summary>
    public int SceneIndex
    {
        get => Snapshot.Scene;
        set
        {
            lock (_settingsLock)
            {
                int index = Math.Clamp(value, 0, Scenes.Count - 1);
                int importedIndex = index - DxrDemoChoices.Length;
                SceneAsset? asset = _backend is DxrMeshBackend && index == RainyCornerSceneIndex ? _rainyCorner :
                    importedIndex >= 0 && importedIndex < _importedScenes.Length ? _importedScenes[importedIndex] : null;
                Change(s => s with { Scene = index, Asset = asset, Orbit = new(index == 1 ? MathF.PI : 0, .165f),
                    Distance = index == 3 && asset == null ? 6.5f : 2.7f, Mouse = default, CameraReset = s.CameraReset + 1 });
                if (asset != null) _camera.FrameBounds(asset.Minimum, asset.Maximum, _height > 0 ? _width / (float)_height : 16f / 9);
                else _camera.FrameDemo(index);
            }
        }
    }
    /// <summary>Resets the camera without changing or rebuilding the scene.</summary>
    public void ResetCamera() => _camera.Reset();

    // .NET 10：只在首次选择时后台解析；渲染热路径复用任务与场景，不逐帧创建集合。
    private bool PreparePackagedScene(GraphicsDevice device, ReadWriteTexture2D<Rgba64, Float4> target)
    {
        Settings selected = Snapshot;
        if (_backend is not DxrMeshBackend || selected.Scene != RainyCornerSceneIndex || selected.Asset != null)
        {
            _loading = false;
            return true;
        }
        _rainyCornerLoad ??= GltfImporter.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", RainyCornerChoice.FileName), _demoCancellation.Token);
        _loading = !_rainyCornerLoad.IsCompleted;
        if (_loading)
        {
            device.ForEach(target, new LoadingShader());
            return false;
        }
        try
        {
            SceneDocument document = _rainyCornerLoad.GetAwaiter().GetResult();
            lock (_settingsLock)
            {
                _rainyCorner = document.Scenes[document.DefaultScene];
                // 解析期间允许切换模型；完成后不覆盖用户的新选择。
                if (_settings.Scene == RainyCornerSceneIndex && _settings.Asset == null)
                    SceneIndex = RainyCornerSceneIndex;
            }
            return true;
        }
        catch
        {
            lock (_settingsLock)
            {
                if (_settings.Scene == RainyCornerSceneIndex && _settings.Asset == null) SceneIndex = 0;
            }
            _rainyCornerLoad = null;
            throw;
        }
    }

    /// <summary>Parses a document in the background and commits it only after a successful GPU upload.</summary>
    public async Task<SceneDocument> ImportAsync(string path, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is not DxrMeshBackend) throw new NotSupportedException("场景导入需要 DXR 后端。");
        var document = await GltfImporter.LoadAsync(path, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<SceneDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingImport(document, completion, cancellation);
        var previous = Interlocked.Exchange(ref _pendingImport, pending);
        previous?.Completion.TrySetCanceled();
        return await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private void ApplyPendingImport(GraphicsDevice device)
    {
        var pending = Interlocked.Exchange(ref _pendingImport, null);
        if (pending == null) return;
        try
        {
            pending.Cancellation.ThrowIfCancellationRequested();
            var document = pending.Document;
            SceneAsset asset = document.Scenes[document.DefaultScene];
            _sceneBackend ??= new DxrSceneBackend(); _sceneBackend.Initialize(device); _sceneBackend.SetScene(asset, pending.Cancellation);
            _importedScenes = document.Scenes;
            var choices = new MeshChoice[DxrDemoChoices.Length + document.Scenes.Length];
            Array.Copy(DxrDemoChoices, choices, DxrDemoChoices.Length);
            for (int i = 0; i < document.Scenes.Length; i++) choices[DxrDemoChoices.Length + i] = new($"{Path.GetFileName(document.Path)} · {document.Scenes[i].Name}", document.Path, document.Scenes[i].TriangleCount);
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
    /// <summary>Gets or sets the requested temporal super-resolution algorithm.</summary>
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
            string model = s.Asset is { } asset ? $"{asset.Name}  {asset.TriangleCount:N0} TRIANGLES  {asset.Instances.Length} INSTANCES  {asset.Lights.Length} LIGHTS" :
                s.Scene < 3 ? $"{MeshData.Choices[s.Scene].Name.ToUpperInvariant()}  {MeshData.Choices[s.Scene].TriangleCount:N0} TRIANGLES" :
                _backend is DxrMeshBackend && s.Scene == RainyCornerSceneIndex ? "RAINY CORNER" : "ORIGINAL SPHERES";
            var sr = ReconstructionStatus;
            string reconstruction = sr == null ? "SR INITIALIZING" : sr.Active == ReconstructionMode.Off
                ? (sr.Requested == ReconstructionMode.Off ? "SR OFF / NATIVE" : $"{sr.Requested} UNAVAILABLE / NATIVE")
                : $"SR {sr.Active}  {sr.InputWidth}x{sr.InputHeight} -> {sr.OutputWidth}x{sr.OutputHeight}";
            string denoiser = sr?.Active == ReconstructionMode.DlssRayReconstruction ? "DLSS RAY RECONSTRUCTION" : s.Denoiser == RayTraceDenoiserMode.NrdRelax
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
    // Legacy programmatic input remains available; the UI uses relative Camera.Rotate deltas.
    public void SetMouse(float x, float y, float panelWidth, float panelHeight)
    {
        Float2 orbit = new((x / Math.Max(1, panelWidth) - .5f) * 6.2831853f, Math.Clamp((.5f - y / Math.Max(1, panelHeight)) * 2.4f, -.1f, 1.4f));
        Settings old = Snapshot; _camera.Rotate(orbit.X - old.Orbit.X, orbit.Y - old.Orbit.Y);
        Change(s => s with { Mouse = new(x, panelHeight - y), Orbit = orbit });
    }
    public void SetZoom(float delta) => _camera.Zoom(delta);
    public void Initialize(GraphicsDevice device, Int2 initialSize) { _device = device; _backend.Initialize(device); }
    public void OnResize(Int2 size) { }

    public bool TryExecute(ReadWriteTexture2D<Rgba64, Float4> texture, int width, int height, TimeSpan time, object? parameter)
    {
        var device = _device ?? throw new InvalidOperationException("Initialize before rendering.");
        ApplyPendingImport(device);
        if (!PreparePackagedScene(device, texture)) return true;
        Settings s = Snapshot;
        SunLightSettings sun = s.Sun with { Enabled = s.ExternalLightingEnabled && s.Sun.Enabled };
        float environment = s.ExternalLightingEnabled ? s.Environment : 0;
        bool pbr = s.Asset != null;
        var hdr = parameter is HdrRenderParameters p ? p : HdrRenderParameters.Default;
        MeshData? mesh = null;
        if (!pbr && s.Scene < MeshData.Choices.Length)
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
            _reconstruction.Configure(mesh == null && !pbr ? ReconstructionMode.Off : s.Reconstruction, s.RenderScale, width, height,
                (mesh != null || pbr) && s.Denoiser == RayTraceDenoiserMode.NrdRelax, pbr);
        }
        bool temporal = _reconstruction is { LinearPipelineActive: true };
        bool nrd = _reconstruction?.Status.NrdActive == true;
        RayTraceDenoiserMode denoiserMode = s.Denoiser == RayTraceDenoiserMode.NrdRelax && !nrd ? RayTraceDenoiserMode.Relax : s.Denoiser;
        int inputWidth = temporal ? _reconstruction!.InputWidth : width;
        int inputHeight = temporal ? _reconstruction!.InputHeight : height;
        EnsureTextures(inputWidth, inputHeight, temporal, nrd || pbr, pbr);
        double elapsed = _lastRenderTime == TimeSpan.Zero || time == TimeSpan.Zero ? 1.0 / 60 : (time - _lastRenderTime).TotalSeconds;
        CameraFrame camera = _camera.Advance(elapsed, out int cameraRevision, out int cameraCut);
        if (_revision != s.Revision || _hdr != hdr || (!temporal && cameraRevision != _cameraRevision)) { _frame = 0; _revision = s.Revision; _hdr = hdr; }
        _cameraRevision = cameraRevision;
        if (temporal)
        {
            bool lightingCut = cameraCut != _cameraCut;
            if (_previousSettings is not Settings old || old.CameraReset != s.CameraReset || old.Scene != s.Scene || old.Asset != s.Asset || cameraCut != _cameraCut ||
                old.Samples != s.Samples || old.Bounces != s.Bounces || old.Denoiser != s.Denoiser || old.Environment != s.Environment || old.Sun != s.Sun || old.ExternalLightingEnabled != s.ExternalLightingEnabled)
                _reconstruction!.Reset();
            _cameraCut = cameraCut;
            Float2 jitter = _reconstruction!.Jitter();
            if (pbr)
            {
                _sceneBackend ??= new DxrSceneBackend(); _sceneBackend.Initialize(device); _sceneBackend.SetScene(s.Asset!);
                _sceneBackend.Trace(camera, _frame, s.Samples, s.Bounces, jitter, environment, _raw!, _normal!, _surfaces!, _normalRoughness!,
                    _pbrSignals!.Diffuse, _pbrSignals.Specular, _pbrSignals.Albedo, _pbrSignals.Unfiltered, _pbrSignals.SpecularGuide,
                    sun, _reconstruction.Active == ReconstructionMode.DlssRayReconstruction, lightingCut);
            }
            else _backend.Trace(mesh!, inputWidth, inputHeight, s.Samples, s.Bounces, _frame, s.Orbit, s.Distance, _raw!, _normal!, _surfaces!, jitter, _normalRoughness, camera, sun, _directLight, environment);
            bool produced = _reconstruction.Execute(_raw!, _normal!, _surfaces!, texture, s.Orbit, s.Distance, jitter, denoiserMode, hdr, elapsed, _normalRoughness, camera, _pbrSignals, s.Exposure, _directLight);
            _previousSettings = s; _lastRenderTime = time;
            _frame = Math.Min(_frame + 1, int.MaxValue - 1);
            return produced;
        }
        _previousSettings = s; _lastRenderTime = time;
        if (pbr) throw new InvalidOperationException("PBR 线性管线初始化失败：" + _reconstruction?.Status.Message);
        var signal = denoiserMode == RayTraceDenoiserMode.None ? texture : _signal!;
        if (mesh != null)
        {
            _backend.Trace(mesh, width, height, s.Samples, s.Bounces, _frame, s.Orbit, s.Distance, _raw!, _normal!, camera: camera, sun: sun, environment: environment);
            device.ForEach(signal, new MeshEncodeShader(_raw!, hdr.IsHdrEnabled, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits));
        }
        else device.ForEach(signal, new RayTraceShader((float)time.TotalSeconds, new(width, height), _frame,
            hdr.IsHdrEnabled, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits, s.Bounces, s.Samples, _normal!,
            new(sun.Direction.X, sun.Direction.Y, sun.Direction.Z), new(sun.Radiance.X, sun.Radiance.Y, sun.Radiance.Z), environment,
            new(camera.Origin.X,camera.Origin.Y,camera.Origin.Z), new(camera.Forward.X,camera.Forward.Y,camera.Forward.Z),
            new(camera.Right.X,camera.Right.Y,camera.Right.Z), new(camera.Up.X,camera.Up.Y,camera.Up.Z), MathF.Tan(camera.VerticalFov*.5f)));
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
    private void EnsureTextures(int width, int height, bool temporal, bool nrd, bool pbr = false)
    {
        if (_raw != null && _width == width && _height == height && (_surfaces != null) == temporal && (_normalRoughness != null) == nrd && (_pbrSignals != null) == pbr) return;
        DisposeTextures();
        var device = _device ?? throw new InvalidOperationException("Initialize before allocating textures.");
        _raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        _normal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        if (temporal)
        {
            _surfaces = device.AllocateReadWriteTexture2D<Float4>(width, height);
            if (nrd) _normalRoughness = device.AllocateReadWriteTexture2D<Float4>(width, height);
            if (nrd && !pbr) _directLight = device.AllocateReadWriteTexture2D<Float4>(width, height);
            if (pbr) _pbrSignals = new(device, width, height);
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
        _pbrSignals?.Dispose(); _pbrSignals = null;
        _directLight?.Dispose(); _directLight = null;
        _raw = null; _normal = null;
        _signal = _historyA = _historyB = _filterA = _filterB = null;
        _momentA = _momentB = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _demoCancellation.Cancel(); _demoCancellation.Dispose(); Interlocked.Exchange(ref _pendingImport, null)?.Completion.TrySetCanceled(); _reconstruction?.Dispose(); _sceneBackend?.Dispose(); DisposeTextures(); _backend.Dispose(); }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct LoadingShader : IComputeShader<Float4>
{
    public Float4 Execute() => new(0.025f, 0.03f, 0.04f, 1);
}
