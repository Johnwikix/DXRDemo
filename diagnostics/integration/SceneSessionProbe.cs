using System.Numerics;
using System.Text.Json;
using DXRDemo.Assets.Importers;
using DXRDemo.Scene;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12.Debug;

internal static class SceneSessionProbe
{
    internal static async Task RunGpu()
    {
        await VerifyLogicalIdentity();
        SceneDocument document = await GltfImporter.LoadAsync(Path.GetFullPath("output/rtxpt-m0/logical-identity.gltf"));
        SceneDocument invalid = document with { Scenes = [document.Scenes[0] with { Instances = [] }] };
        DxrDevice.TryActivateAgilityRuntime();
        using var debugLayer = Vortice.Direct3D12.D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        debugLayer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var debug = device.Device.QueryInterface<ID3D12InfoQueue>();
        using var session = new SceneSession((_, _) => Task.FromResult(document),
            (path, _) => Task.FromResult(path == "invalid" ? invalid : document));
        session.ReconstructionMode = ReconstructionMode.Off;
        using var pass = new RayTracePass(session) { DenoiserMode = RayTraceDenoiserMode.None, Samples = 1, MaxBounces = 2 };
        pass.Initialize(device, new(64, 48));
        using var target = device.CreateTexture2D<Rgba64>(64, 48);
        pass.TryExecute(target, 64, 48, TimeSpan.Zero, HdrRenderParameters.Default);
        SceneSessionSnapshot before = session.Snapshot;
        Require(ReferenceEquals(before.Asset, document.Scenes[0]), "Real GPU upload did not commit CPU scene");
        Task<SceneDocument> failed = session.ImportAsync("invalid");
        pass.TryExecute(target, 64, 48, TimeSpan.Zero, HdrRenderParameters.Default);
        await ExpectFailure(failed);
        Require(ReferenceEquals(session.Snapshot, before), "Rejected GPU scene changed committed snapshot");
        Task<SceneDocument> imported = session.ImportAsync("valid");
        while (!imported.IsCompleted)
        {
            pass.TryExecute(target, 64, 48, TimeSpan.Zero, HdrRenderParameters.Default);
            await Task.Delay(1);
        }
        await imported;
        Require(session.SceneIndex == SceneCatalog.Packaged.Count && session.Snapshot.SceneRevision == before.SceneRevision + 1,
            "Real import transaction did not publish catalog/scene revision");
        pass.TryExecute(target, 64, 48, TimeSpan.FromSeconds(1.0 / 60), HdrRenderParameters.Default);
        _ = target.ToArray();
        for (ulong i = 0; i < debug.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = debug.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        pass.Dispose();
        session.Exposure = 2;
        Require(session.Exposure == 2, "Borrowing renderer disposed the shared session");
        Console.WriteLine("PASS: real GPU scene commit/failure recovery, SceneAsset import/catalog, native frame readback, borrowed session lifetime; D3D12 Error/Corruption=0.");
    }

    internal static async Task Run()
    {
        SceneDocument first = Document("first"), second = Document("second"), third = Document("third");
        var loads = new Dictionary<string, Task<SceneDocument>>();
        using var session = new SceneSession((_, _) => Task.FromResult(first), (path, _) => loads[path]);
        var uploader = new Uploader();
        session.PrepareFrame(uploader, 320, 200);
        Require(ReferenceEquals(session.Snapshot.Asset, first.Scenes[0]) && uploader.Prepared == 1, "Initial scene not committed");
        var camera = session.Camera.Advance(0, out _, out _);
        SceneSessionSnapshot before = session.Snapshot;
        for (int i = 0; i < 100; i++) session.PrepareFrame(uploader, 320, 200);
        Require(uploader.Prepared == 1 && ReferenceEquals(session.Snapshot, before), "Idle frame rebuilt scene/state");

        loads["failed"] = Task.FromException<SceneDocument>(new InvalidDataException("parse failure"));
        await ExpectFailure(session.ImportAsync("failed"));
        Require(ReferenceEquals(session.Snapshot, before), "Parse failure changed committed scene");

        loads["second"] = Task.FromResult(second);
        uploader.Fail = true;
        Task<SceneDocument> failedUpload = session.ImportAsync("second");
        await Pump(session, uploader, failedUpload);
        await ExpectFailure(failedUpload);
        Require(ReferenceEquals(session.Snapshot, before) && session.Camera.Advance(0, out _, out _) == camera,
            "GPU preparation failure changed CPU scene or camera");
        uploader.Fail = false;

        // 旧解析晚到，不能覆盖已提交的新请求；名称/路径不参与请求顺序判断。
        var slow = new TaskCompletionSource<SceneDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        loads["slow"] = slow.Task;
        Task<SceneDocument> old = session.ImportAsync("slow");
        Task<SceneDocument> latest = session.ImportAsync("second");
        await Pump(session, uploader, latest);
        await latest;
        slow.SetResult(third);
        await ExpectCanceled(old);
        Require(ReferenceEquals(uploader.Active, second.Scenes[0]) && ReferenceEquals(session.Snapshot.Asset, uploader.Active),
            "Slow stale import replaced latest GPU/CPU scene");

        using (var cancellation = new CancellationTokenSource())
        {
            before = session.Snapshot;
            uploader.AfterPrepare = cancellation.Cancel;
            Task<SceneDocument> canceled = session.ImportAsync("second", cancellation.Token);
            await Pump(session, uploader, canceled);
            await ExpectCanceled(canceled);
            Require(ReferenceEquals(session.Snapshot, before) && uploader.Discarded > 0, "Canceled upload was published");
        }

        // 模拟 UI 在慢 GPU 准备期间提交另一请求；未提交的候选必须释放。
        loads["third"] = Task.FromResult(third);
        Task<SceneDocument>? replacement = null;
        uploader.AfterPrepare = () => replacement = session.ImportAsync("third");
        Task<SceneDocument> superseded = session.ImportAsync("second");
        session.PrepareFrame(uploader, 320, 200);
        await ExpectCanceled(superseded);
        Require(replacement != null, "Replacement did not run during preparation");
        await Pump(session, uploader, replacement!);
        await replacement!;
        Require(ReferenceEquals(uploader.Active, third.Scenes[0]) && ReferenceEquals(session.Snapshot.Asset, uploader.Active),
            "Prepared stale GPU scene was committed");
        Require(session.Scenes.Count == SceneCatalog.Packaged.Count + 1, "Imported catalog duplicated packaged entries");
        session.SceneIndex = SceneCatalog.Kitchen;
        await ExpectFailure(session.ImportAsync("failed"));
        Require(session.SceneIndex == SceneCatalog.Kitchen, "Failed import swallowed a pending catalog selection");
        session.PrepareFrame(uploader, 320, 200);
        Require(ReferenceEquals(session.Snapshot.Asset, first.Scenes[0]), "Pending catalog selection did not resume after failed import");
        session.SceneIndex = SceneCatalog.Bistro;
        session.Exposure = 2.75f;
        session.PrepareFrame(uploader, 320, 200);
        Require(session.Exposure == 2.75f, "Late scene commit overwrote exposure restored/edited during loading");
        before = session.Snapshot;
        session.EnvironmentIntensity = float.NaN;
        session.Exposure = float.PositiveInfinity;
        Require(ReferenceEquals(session.Snapshot, before), "Non-finite common settings published");

        var shutdownLoad = new TaskCompletionSource<SceneDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        loads["shutdown"] = shutdownLoad.Task;
        Task<SceneDocument> shuttingDown = session.ImportAsync("shutdown");
        session.Dispose(); session.Dispose();
        await ExpectCanceled(shuttingDown);
        shutdownLoad.SetResult(first);
        Console.WriteLine("PASS: session transactional upload, failure/cancellation, stale parse/upload, shutdown and idle reuse.");
        await VerifyLogicalIdentity();
    }

    private static async Task VerifyLogicalIdentity()
    {
        string directory = Path.Combine("output", "rtxpt-m0");
        Directory.CreateDirectory(directory);
        var bytes = new byte[48];
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        Buffer.BlockCopy(positions, 0, bytes, 0, 36);
        Buffer.BlockCopy(new uint[] { 0, 1, 2 }, 0, bytes, 36, 12);
        var model = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = bytes.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 36 }, new { buffer = 0, byteOffset = 36, byteLength = 12 } },
            accessors = new object[] { new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3", min = new[] { 0, 0, 0 }, max = new[] { 1, 1, 0 } },
                new { bufferView = 1, componentType = 5125, count = 3, type = "SCALAR" } },
            materials = new object[] { new { }, new { } },
            meshes = new[] { new { name = "duplicate", primitives = new[] {
                new { attributes = new { POSITION = 0 }, indices = 1, material = 0 },
                new { attributes = new { POSITION = 0 }, indices = 1, material = 1 } } } },
            nodes = new[] { new { name = "duplicate", mesh = 0 }, new { name = "duplicate", mesh = 0 } },
            scenes = new[] { new { nodes = new[] { 0, 1 } } }, scene = 0
        };
        string path = Path.GetFullPath(Path.Combine(directory, "logical-identity.gltf"));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(model));
        SceneAsset scene = (await GltfImporter.LoadAsync(path)).Scenes[0];
        SceneAsset reloaded = (await GltfImporter.LoadAsync(path)).Scenes[0];
        Require(scene.Meshes.Length == 2 && scene.Instances.Length == 4, "RT primitive/instance layout changed");
        Require(scene.LogicalMeshes.Length == 1 && scene.LogicalMeshes[0].Primitives.Length == 2, "Multi-material logical mesh not preserved");
        Require(scene.Meshes[0].LogicalMeshId == scene.Meshes[1].LogicalMeshId && scene.Meshes[0].PrimitiveIndex == 0 && scene.Meshes[1].PrimitiveIndex == 1,
            "Primitive logical identity incorrect");
        Require(scene.Instances[0].GeometryInstanceId == scene.Instances[1].GeometryInstanceId &&
            scene.Instances[2].GeometryInstanceId == scene.Instances[3].GeometryInstanceId &&
            scene.Instances[0].GeometryInstanceId != scene.Instances[2].GeometryInstanceId,
            "Identical names/transforms merged distinct logical nodes");
        for (int i = 0; i < scene.Instances.Length; i++)
            Require(scene.Instances[i].GeometryInstanceId == reloaded.Instances[i].GeometryInstanceId, "Logical identity changed on reload");
        for (int i = 0; i < 2; i++) Require(scene.LogicalMeshes[0].Primitives[i] == new ScenePrimitiveRange(i, 0, 3, i), "Material range incorrect");
        Require(new SceneMesh("legacy", [], [], 0).LogicalMeshId == -1 && new SceneInstance("legacy", 0, Matrix4x4.Identity).GeometryInstanceId == -1,
            "Unidentified asset was assigned a guessed identity");
        Console.WriteLine("PASS: multi-material logical meshes, distinct identical nodes, stable reload identities and explicit unknown fallback.");
    }

    private static SceneDocument Document(string name) => new(name,
        [new(name, [], [], [SceneMaterial.Default], [], [], new(-1), new(1), [])], 0);

    private static async Task Pump(SceneSession session, Uploader uploader, Task task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!task.IsCompleted)
        {
            session.PrepareFrame(uploader, 320, 200);
            await Task.Delay(1, timeout.Token);
        }
    }
    private static async Task ExpectFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected scene failure");
    }
    private static async Task ExpectCanceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected canceled scene request");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Uploader : ISceneUploader
    {
        internal int Prepared, Discarded;
        internal SceneAsset? Active;
        internal bool Fail;
        internal Action? AfterPrepare;
        public IPreparedScene PrepareScene(SceneAsset asset, CancellationToken cancellation)
        {
            Prepared++;
            if (Fail) throw new InvalidDataException("GPU preparation failure");
            Action? callback = AfterPrepare; AfterPrepare = null; callback?.Invoke();
            return new Candidate(this, asset);
        }
        private sealed class Candidate(Uploader owner, SceneAsset asset) : IPreparedScene
        {
            private bool _committed;
            public void Commit() { owner.Active = asset; _committed = true; }
            public void Dispose() { if (!_committed) owner.Discarded++; }
        }
    }
}
