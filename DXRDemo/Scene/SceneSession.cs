using System.IO;
using System.Numerics;
using DXRDemo.Assets.Importers;
using DXRDemo.Camera;
using DXRDemo.SuperResolution;

namespace DXRDemo.Scene;

/// <summary>上传只准备候选资源；Commit 必须在渲染线程快速发布，不再执行 GPU 工作。</summary>
internal interface IPreparedScene : IDisposable
{
    void Commit();
}

internal interface ISceneUploader
{
    IPreparedScene PrepareScene(SceneAsset asset, CancellationToken cancellation);
}

/// <summary>已提交场景和公共设置的一致快照；渲染帧只读取一次。</summary>
public sealed record SceneSessionSnapshot(int SceneIndex, SceneAsset? Asset, IReadOnlyList<SceneChoice> Scenes,
    float Environment, float Exposure, SunLightSettings Sun, bool ExternalLightingEnabled,
    ReconstructionMode Reconstruction, int RenderScale, int Revision = 0, int SceneRevision = 0);

/// <summary>窗口持有的唯一场景、相机与公共控制状态，GPU 上传由当前管线提供。</summary>
public sealed class SceneSession : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _shutdown;
    private readonly Func<int, CancellationToken, Task<SceneDocument>> _loadPackaged;
    private readonly Func<string, CancellationToken, Task<SceneDocument>> _loadImported;
    private readonly Task<SceneDocument>?[] _packaged = new Task<SceneDocument>?[SceneCatalog.Packaged.Count];
    private SceneAsset[] _imported = [];
    private SceneSessionSnapshot _snapshot = new(SceneCatalog.Kitchen, null, SceneCatalog.Packaged,
        1, 0, SunLightSettings.Default, true, ReconstructionMode.DlssRayReconstruction, 50);
    private PendingScene? _pending;
    private int _requestedIndex = SceneCatalog.Kitchen, _exposureRevision;
    private bool _initialStarted, _disposed;
    private Vector2 _mouseOrbit = new(0, .165f);
    private ReconstructionStatus? _reconstructionStatus;

    private sealed class PendingScene(int index, CancellationToken cancellation,
        TaskCompletionSource<SceneDocument>? completion = null, int exposureRevision = 0)
    {
        internal readonly int Index = index;
        internal readonly CancellationToken Cancellation = cancellation;
        internal readonly TaskCompletionSource<SceneDocument>? Completion = completion;
        internal readonly int ExposureRevision = exposureRevision;
        internal Task<SceneDocument>? Load;
        internal SceneDocument? Document;
        internal SceneAsset? Asset;
    }

    public SceneSession() : this(SceneCatalog.LoadAsync,
        static (path, cancellation) => GltfImporter.LoadAsync(path, cancellation)) { }

    internal SceneSession(Func<int, CancellationToken, Task<SceneDocument>> loadPackaged,
        Func<string, CancellationToken, Task<SceneDocument>> loadImported)
    {
        _shutdown = _lifetime.Token;
        _loadPackaged = loadPackaged;
        _loadImported = loadImported;
    }

    public SceneSessionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public CameraController Camera { get; } = new();
    public IReadOnlyList<SceneChoice> Scenes => Snapshot.Scenes;
    public bool IsLoading => Volatile.Read(ref _pending) != null;
    public ReconstructionStatus? ReconstructionStatus => Volatile.Read(ref _reconstructionStatus);
    internal void PublishReconstructionStatus(ReconstructionStatus status) => Volatile.Write(ref _reconstructionStatus, status);

    // 修改仅由 UI/设置事件触发；逐帧路径复用快照，不复制集合。
    private void Change(Func<SceneSessionSnapshot, SceneSessionSnapshot> update)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SceneSessionSnapshot next = update(_snapshot);
            if (next != _snapshot)
            {
                if (next.Exposure != _snapshot.Exposure) _exposureRevision++;
                Volatile.Write(ref _snapshot, next with { Revision = _snapshot.Revision + 1 });
            }
        }
    }

    public float EnvironmentIntensity { get => Snapshot.Environment; set { if (float.IsFinite(value)) Change(s => s with { Environment = Math.Clamp(value, 0, 100) }); } }
    public float Exposure { get => Snapshot.Exposure; set { if (float.IsFinite(value)) Change(s => s with { Exposure = Math.Clamp(value, -16, 16) }); } }
    public bool ExternalLightingEnabled { get => Snapshot.ExternalLightingEnabled; set => Change(s => s with { ExternalLightingEnabled = value }); }
    public int RenderScalePercent { get => Snapshot.RenderScale; set => Change(s => s with { RenderScale = Math.Clamp(value, 1, 100) }); }
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
    public SunLightSettings Sun
    {
        get => Snapshot.Sun;
        set
        {
            if (!float.IsFinite(value.Azimuth) || !float.IsFinite(value.Elevation) || !float.IsFinite(value.Intensity) || !float.IsFinite(value.AngularDiameter)) return;
            Change(s => s with { Sun = value with { Azimuth = (value.Azimuth % 360 + 360) % 360,
                Elevation = Math.Clamp(value.Elevation, -90, 90), Intensity = Math.Clamp(value.Intensity, 0, 100),
                AngularDiameter = Math.Clamp(value.AngularDiameter, 0, 20) } });
        }
    }

    /// <summary>选择是请求；旧场景与相机保留到候选 GPU 资源成功提交。</summary>
    public int SceneIndex
    {
        get => Volatile.Read(ref _requestedIndex);
        set
        {
            PendingScene? previous;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int index = Math.Clamp(value, 0, _snapshot.Scenes.Count - 1);
                previous = _pending;
                _initialStarted = true;
                _requestedIndex = index;
                if (index == _snapshot.SceneIndex && _snapshot.Asset != null)
                    _pending = null;
                else
                    _pending = CreateSelection(index);
            }
            previous?.Completion?.TrySetCanceled();
        }
    }

    private PendingScene CreateSelection(int index)
    {
        var pending = new PendingScene(index, _shutdown, exposureRevision: _exposureRevision);
        if (index < SceneCatalog.Packaged.Count)
        {
            Task<SceneDocument> load = _packaged[index] ??= _loadPackaged(index, _shutdown);
            Observe(load);
            pending.Load = load;
        }
        else pending.Asset = _imported[index - SceneCatalog.Packaged.Count];
        return pending;
    }

    public void ResetCamera() => Camera.Reset();
    public void SetZoom(float delta) => Camera.Zoom(delta);
    public void SetMouse(float x, float y, float panelWidth, float panelHeight)
    {
        Vector2 orbit = new((x / Math.Max(1, panelWidth) - .5f) * MathF.Tau,
            Math.Clamp((.5f - y / Math.Max(1, panelHeight)) * 2.4f, -.1f, 1.4f));
        lock (_gate)
        {
            Camera.Rotate(orbit.X - _mouseOrbit.X, orbit.Y - _mouseOrbit.Y);
            _mouseOrbit = orbit;
        }
    }

    public async Task<SceneDocument> ImportAsync(string path, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown);
        var completion = new TaskCompletionSource<SceneDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PendingScene(-1, linked.Token, completion);
        PendingScene? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked.Token.ThrowIfCancellationRequested();
            previous = _pending;
            _pending = request; // 解析之前确定新旧顺序，旧的慢解析不能覆盖后来的请求。
            _initialStarted = true;
        }
        previous?.Completion?.TrySetCanceled();
        try
        {
            Task<SceneDocument> load = _loadImported(path, linked.Token);
            Observe(load);
            SceneDocument document = await load.WaitAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_pending, request)) throw new OperationCanceledException("场景请求已被替换。");
                request.Document = document;
            }
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { Retire(request); }
    }

    /// <summary>渲染线程调用；慢上传不持有 UI 锁，最后复核请求后同时提交 GPU/CPU/相机。</summary>
    internal void PrepareFrame(ISceneUploader uploader, int width, int height)
    {
        PendingScene? request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialStarted) { _pending = CreateSelection(_requestedIndex); _initialStarted = true; }
            request = _pending;
            if (request == null || (request.Asset == null && request.Document == null && request.Load?.IsCompleted != true)) return;
        }
        try
        {
            request.Cancellation.ThrowIfCancellationRequested();
            SceneDocument? document = request.Document ?? request.Load?.GetAwaiter().GetResult();
            if (document != null && ((uint)document.DefaultScene >= (uint)document.Scenes.Length))
                throw new InvalidDataException("文档的默认场景索引无效。");
            SceneAsset asset = request.Asset ?? document!.Scenes[document.DefaultScene];
            IReadOnlyList<SceneChoice> choices = Snapshot.Scenes;
            int index = request.Index;
            if (request.Completion != null)
            {
                var entries = new SceneChoice[SceneCatalog.Packaged.Count + document!.Scenes.Length];
                for (int i = 0; i < SceneCatalog.Packaged.Count; i++) entries[i] = SceneCatalog.Packaged[i];
                for (int i = 0; i < document.Scenes.Length; i++) entries[SceneCatalog.Packaged.Count + i] =
                    new($"{Path.GetFileName(document.Path)} · {document.Scenes[i].Name}", document.Path, document.Scenes[i].TriangleCount);
                choices = Array.AsReadOnly(entries);
                index = SceneCatalog.Packaged.Count + document.DefaultScene;
            }
            using IPreparedScene upload = uploader.PrepareScene(asset, request.Cancellation);
            lock (_gate)
            {
                request.Cancellation.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(_pending, request)) return;
                SceneSessionSnapshot next = _snapshot with { Asset = asset, SceneIndex = index, Scenes = choices,
                    Revision = _snapshot.Revision + 1, SceneRevision = _snapshot.SceneRevision + 1,
                    Exposure = index != _snapshot.SceneIndex && request.ExposureRevision == _exposureRevision &&
                        index is >= SceneCatalog.Kitchen and <= SceneCatalog.Bistro
                        ? RtxptAssets.InitialExposure((RtxptScene)(index - 1)) : _snapshot.Exposure };
                upload.Commit();
                Camera.FrameScene(asset, width / (float)Math.Max(1, height));
                _mouseOrbit = new(0, .165f);
                if (request.Completion != null) _imported = document!.Scenes;
                _requestedIndex = index;
                if (next.Exposure != _snapshot.Exposure) _exposureRevision++;
                Volatile.Write(ref _snapshot, next);
                _pending = null;
            }
            request.Completion?.TrySetResult(document!);
        }
        catch (OperationCanceledException) { request.Completion?.TrySetCanceled(); Retire(request); }
        catch (Exception error)
        {
            Retire(request);
            if (request.Completion != null) request.Completion.TrySetException(error);
            else throw;
        }
    }

    private void Retire(PendingScene request)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, request)) return;
            _pending = null;
            // 导入失败不能吞掉用户此前尚未上传完成的目录选择。
            if (!_disposed && request.Completion != null && _requestedIndex != _snapshot.SceneIndex)
                _pending = CreateSelection(_requestedIndex);
            else _requestedIndex = _snapshot.SceneIndex;
            if (request.Load?.IsFaulted == true) _packaged[request.Index] = null;
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(static fault => { _ = fault.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        PendingScene? request;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            request = _pending;
            _pending = null;
        }
        request?.Completion?.TrySetCanceled();
        try { _lifetime.Cancel(); }
        finally { _lifetime.Dispose(); }
    }
}
