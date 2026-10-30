using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using DXRDemo.SuperResolution;

namespace DXRDemo.Shaders.Raster;

/// <summary>共用场景、相机与帧服务的原生光栅入口。</summary>
public sealed class RasterPass : ISceneShaderPass, IRenderDiagnostics
{
    private readonly GpuSceneCache _cache;
    private readonly SceneRenderer _renderer;
    private readonly bool _ownsServices, _ownsSession;
    private RasterBackend? _backend;
    private GpuFrameEffects? _effects;
    private SceneSessionSnapshot? _previousScene;
    private CameraFrame _previousCamera;
    private Vector2 _previousJitter;
    private TimeSpan _lastTime;
    private int _cameraCut = -1, _width, _height;
    private bool _disposed;
    private ReconstructionStatus? _statusSource, _publishedStatus;
    private string? _nrdStatus;
    public RasterPass() : this(new SceneSession(), new GpuSceneCache(), new SceneRenderer(), true, true) { }
    public RasterPass(SceneSession session) : this(session, new GpuSceneCache(), new SceneRenderer(), true, false) { }
    internal RasterPass(SceneSession session, GpuSceneCache cache, SceneRenderer renderer) : this(session, cache, renderer, false, false) { }
    private RasterPass(SceneSession session, GpuSceneCache cache, SceneRenderer renderer, bool ownsServices, bool ownsSession)
    {
        Session = session;
        _cache = cache;
        _renderer = renderer;
        _ownsServices = ownsServices;
        _ownsSession = ownsSession;
    }
    internal bool UseHardwareLighting { get; init; } = true;
    public SceneSession Session
    {
        get;
    }
    public string Id => "raster";
    public string DisplayName => "Clustered raster";
    public string Description => "LOD0 clustered PBR, CSM, optional one-bounce hardware GI/hit-lighting reflections, forward transparency and shared HDR reconstruction.";
    public ShaderAuthor Author { get; } = new("RT Demo", null, "See Assets/Models/SOURCES.md for model terms");
    public string? OriginalUrl => null;
    public ShaderCapabilities Capabilities => ShaderCapabilities.UsesMouse | ShaderCapabilities.UsesResolution;
    public string DiagnosticText
    {
        get
        {
            var scene = Session.Snapshot;
            var status = Session.ReconstructionStatus;
            string reconstruction = status == null ? "SR INITIALIZING" : status.Active == ReconstructionMode.Off ? "NATIVE TAA" :
                $"SR {status.Active} {status.InputWidth}x{status.InputHeight} -> {status.OutputWidth}x{status.OutputHeight}";
            string lighting = _backend?.HardwareLightingActive == true ? $"RT GI + HIT LIGHTING | {(_backend.NrdActive ? "NRD RELAX" : "NRD FALLBACK")}" : "SSR + FILTERED IBL | NO GI";
            return $"CLUSTERED RASTER | LOD0\n{scene.Asset?.Name ?? "LOADING"}  {scene.Asset?.TriangleCount ?? 0:N0} TRIANGLES\nCSM | LOCAL SHADOWS {_backend?.ShadowedLocalLights ?? 0}/2 | {lighting}\n{reconstruction}";
        }
    }
    public void Initialize(DxrDevice device, RenderSize size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer.Initialize(device);
        _backend = new RasterBackend(_cache, hardwareLighting: UseHardwareLighting);
        _backend.Initialize(device);
        _effects = new GpuFrameEffects(device);
    }
    public void OnResize(RenderSize size)
    {
    }
    public void SetMouse(float x, float y, float width, float height) => Session.SetMouse(x, y, width, height);
    public void SetZoom(float delta) => Session.SetZoom(delta);
    public bool TryExecute(GpuTexture<Rgba64> target, int width, int height, TimeSpan time, object? parameter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend == null)
            throw new InvalidOperationException("先初始化光栅入口。");
        Session.PrepareFrame(_backend, width, height);
        var scene = Session.Snapshot;
        if (scene.Asset == null)
        {
            _effects!.DrawLoading(target);
            return true;
        }
        bool activated = _renderer.Activate(this);
        var reconstruction = _renderer.Reconstruction;
        reconstruction.Configure(scene.Reconstruction, scene.RenderScale, width, height, allowRr: false, nativeTaa: true);
        double seconds = _lastTime == TimeSpan.Zero || time == TimeSpan.Zero ? 1.0 / 60 : (time - _lastTime).TotalSeconds;
        var camera = Session.Camera.Advance(seconds, out _, out int cut);
        bool reset = activated || _previousScene == null || scene.Revision != _previousScene.Revision || cut != _cameraCut || _width != width || _height != height;
        if (reset)
        {
            reconstruction.Reset();
            _previousCamera = camera;
            _previousJitter = default;
        }
        var jitter = reconstruction.Jitter(nativeTaa: true);
        _backend.SetScene(scene.Asset);
        var sun = scene.Sun with
        {
            Enabled = scene.ExternalLightingEnabled && scene.Sun.Enabled
        };
        var frame = _backend.Render(camera, _previousCamera, jitter, _previousJitter, reconstruction.Status.InputWidth, reconstruction.Status.InputHeight,
            seconds, reset, scene.ExternalLightingEnabled ? scene.Environment : 0, sun,
            0);
        frame = frame with
        {
            Revisions = new(scene.SceneRevision, scene.Revision, cut, _renderer.PipelineRevision)
        };
        bool produced = _renderer.Output(frame, target, parameter is HdrRenderParameters hdr ? hdr : HdrRenderParameters.Default, scene.Exposure);
        var status = reconstruction.Status;
        string nrdStatus = _backend.NrdStatus;
        if (!ReferenceEquals(_statusSource, status) || _publishedStatus?.NrdActive != _backend.NrdActive || _nrdStatus != nrdStatus)
        {
            // 只在状态变化时创建不可变快照，避免每个渲染帧复制 record/拼接字符串。
            _statusSource = status;
            _nrdStatus = nrdStatus;
            _publishedStatus = status with
            {
                NrdAvailable = _backend.NrdAvailable, NrdActive = _backend.NrdActive,
                Message = _backend.HardwareLightingActive ? status.Message + " · " + nrdStatus : status.Message
            };
        }
        Session.PublishReconstructionStatus(_publishedStatus!);
        _previousCamera = camera;
        _previousJitter = jitter;
        _previousScene = scene;
        _cameraCut = cut;
        _lastTime = time;
        _width = width;
        _height = height;
        return produced;
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var cleanup = new ResourceCleanup();
        cleanup.Release(_backend);
        cleanup.Release(_effects);
        if (_ownsServices)
        {
            cleanup.Release(_renderer);
            cleanup.Release(_cache);
        }
        if (_ownsSession)
            cleanup.Release(Session);
        cleanup.Finish();
    }
}
