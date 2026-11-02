using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using Vortice.Direct3D12;

/// <summary>验证三级世界缓存的独立层查询、固定调度预算、远处照明和滚动身份。</summary>
internal static class RasterWorldClipmapProbe
{
    internal static void Run(DxrDevice device, RasterBackend backend)
    {
        QueryContracts(device);
        CoverageAndScroll(device, backend);
    }

    private static unsafe void QueryContracts(DxrDevice device)
    {
        float[] distances = [0, 6.5f, 13, 24, 27, 29, 6.999f, 7.001f];
        float[] expected = [.25f, .375f, .625f, .75f, .1875f, 0, .49975f, .5f];
        using var constants = DxrResources.CreateUploadBuffer(device.Device, 512);
        using var probes = DxrResources.CreateUploadBuffer(device.Device, RasterWorldCache.Count * RasterWorldCache.Stride);
        using var queries = DxrResources.CreateUploadBuffer(device.Device, (ulong)distances.Length * 32);
        using var result = DxrResources.CreateDefaultBuffer(device.Device, (ulong)distances.Length * 16, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess);
        using var readback = device.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
            ResourceDescription.Buffer((ulong)distances.Length * 16), ResourceStates.CopyDest);
        using var pass = new ComputePass(device.Device, "RasterWorldContract.hlsl", "Query", 10, 1);
        pass.BufferSrv(0, queries, 16, (uint)distances.Length * 2);
        pass.BufferSrv(9, probes, RasterWorldCache.Stride, RasterWorldCache.Count);
        pass.BufferUav(0, result, 16, (uint)distances.Length);
        var asset = RasterProbe.Fixture(SceneMaterial.Default);
        void* pointer;
        double maximumError = 0;
        foreach (Vector3 origin in new Vector3[] { new(0,0,3), new(.49999f,0,3), new(.50001f,0,3), new(-37.13f,-2.19f,-3.27f) })
        {
            var camera = CameraFrame.Look(origin, -Vector3.UnitZ, .9f, .01f, 100);
            constants.Map(0, null, &pointer).CheckError();
            new Span<byte>((void*)pointer, 512).Clear();
            var data = new Span<Vector4>((void*)pointer, 20);
            data[0] = new(origin, 0);
            data[5] = new(distances.Length, 0, 0, 0);
            Vector4 nearGrid = RasterWorldCache.Grid(asset, camera);
            data[10] = nearGrid;
            data[11] = new(RasterWorldCache.Width, RasterWorldCache.Height, RasterWorldCache.Depth, RasterWorldCache.Count);
            constants.Unmap(0);
            queries.Map(0, null, &pointer).CheckError();
            var queryData = new Span<Vector4>((void*)pointer, distances.Length * 2);
            Vector3 queryOrigin = origin.X is > 0 and < 1 ? new(.5f,0,3) : origin;
            for (int i = 0; i < distances.Length; i++)
            {
                queryData[i*2] = new(queryOrigin + Vector3.UnitX * distances[i] * nearGrid.W, 1);
                queryData[i*2+1] = new(Vector3.UnitY, 0);
            }
            queries.Unmap(0);
            for (int state = 0; state < 4; state++)
            {
                // 0=分层常量；1=细层被遮挡；2=细层身份过期；3=细层在实体内部。
                probes.Map(0, null, &pointer).CheckError();
                var values = new Span<Vector4>((void*)pointer, RasterWorldCache.Count * (RasterWorldCache.Stride/16));
                values.Clear();
                for (int level = 0; level < RasterWorldCache.Levels; level++)
                {
                    Vector4 grid = RasterWorldCache.Grid(asset, camera, level);
                    for (int index = 0; index < RasterWorldCache.ProbesPerLevel; index++)
                    {
                        Vector3 cell = Cell(grid, index);
                        int k = (level * RasterWorldCache.ProbesPerLevel + index) * (RasterWorldCache.Stride/16);
                        values[k] = new(cell + (level == 0 && state == 2 ? Vector3.UnitX * 16 : Vector3.Zero), level == 0 && state == 3 ? -1 : 1);
                        values[k+1] = new((cell + new Vector3(.5f)) * grid.W, 1);
                        for (int axis = 0; axis < 6; axis++)
                        {
                            values[k+2+axis] = new(new Vector3((level+1)*.25f), 1);
                        }
                        for (int bin = 0; bin < 32; bin++)
                            values[k+8+bin] = level == 0 && state == 1 ? Vector4.Zero : new(100,10000,100,10000);
                    }
                }
                probes.Unmap(0);
                device.CommandAllocator.Reset(); device.CommandList.Reset(device.CommandAllocator);
                pass.Dispatch(device.CommandList, constants, distances.Length, 1);
                device.CommandList.ResourceBarrierTransition(result, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                device.CommandList.CopyResource(readback, result);
                device.CommandList.ResourceBarrierTransition(result, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
                device.CommandList.Close(); device.CommandQueue.ExecuteCommandList(device.CommandList); device.SignalAndWait();
                readback.Map(0, null, &pointer).CheckError();
                var actual = new ReadOnlySpan<Vector4>((void*)pointer, distances.Length);
                if (state == 0)
                    for (int i = 0; i < distances.Length; i++)
                    {
                        double error = Vector3.Distance(new(actual[i].X,actual[i].Y,actual[i].Z), new(expected[i]));
                        maximumError = Math.Max(maximumError, error);
                        RasterProbe.Require(error < 1e-4, $"Clipmap coverage/blend mismatch: origin={origin}, x={distances[i]}, actual={actual[i]}, expected={expected[i]:R}");
                    }
                else
                    RasterProbe.Require(actual[0].X == 0 && actual[0].Y == 0 && actual[0].Z == 0,
                        $"Occluded/stale/interior fine cache promoted to a bright coarse level: state={state}, radiance={actual[0]}");
                readback.Unmap(0);
            }
        }
        Console.WriteLine($"PASS: clipmap GPU coverage/blend max RGB error={maximumError:R}; negative coordinates, anchored queries across a cell boundary, outer fade, occluded/stale/interior fine probes do not promote to coarse lighting.");
    }

    private static void CoverageAndScroll(DxrDevice device, RasterBackend backend)
    {
        var material = SceneMaterial.Default; material.BaseColor = new(.5f,.5f,.5f,1); material.Factors = new(0,1,1,1);
        var asset = RasterProbe.Fixture(material);
        asset = asset with { Instances = [new("far receiver",0,Matrix4x4.CreateScale(6)*Matrix4x4.CreateTranslation(0,0,-9))],
            Minimum = new(-6,-6,-9), Maximum = new(6,6,-9),
            EnvironmentMap = new(8,4,Enumerable.Repeat(new Vector4(.5f,.5f,.5f,0),32).ToArray()) };
        var camera = CameraFrame.Look(new(0,0,3), -Vector3.UnitZ, .4f, .01f, 100);
        backend.SetScene(asset);
        double energy = 0; int samples = 0;
        for (int frame = 0; frame < 32; frame++)
        {
            var output = backend.Render(camera,camera,default,default,64,48,1.0/60,frame==0,1,default);
            if (frame == 0) VerifySchedule(RasterWorldProbe.ReadProbes(device,backend.WorldProbeData!), 1, camera, asset);
            if (frame >= 24)
            {
                var pixels = ReconstructionProbe.ReadTexture(device.Device,output.Color,true);
                for (int y = 22; y < 26; y++) for (int x = 30; x < 34; x++) { energy += pixels[(y*64+x)*4]; samples++; }
            }
        }
        double mean = energy / samples;
        RasterProbe.Require(mean is > .20 and < .30, $"Far receiver outside first two levels lost cached GI: {mean:R}");
        var before = RasterWorldProbe.ReadProbes(device,backend.WorldProbeData!);
        VerifySchedule(before, 32, camera, asset);
        float spacing = RasterWorldCache.Grid(asset,camera).W;
        var moved = camera with { Origin = camera.Origin + new Vector3(-5.25f*spacing,0,5.25f*spacing) };
        backend.Render(moved,camera,default,default,65,49,1.0/60,true,1,default);
        var after = RasterWorldProbe.ReadProbes(device,backend.WorldProbeData!);
        int[] updated = new int[RasterWorldCache.Levels], retained = new int[RasterWorldCache.Levels];
        for (int i = 0; i < RasterWorldCache.Count; i++)
        {
            int k = i*(RasterWorldCache.Stride/16), level = i/RasterWorldCache.ProbesPerLevel;
            if (after[k+1].W == 33)
            {
                updated[level]++;
                Vector3 expectedCell = Cell(RasterWorldCache.Grid(asset,moved,level), i%RasterWorldCache.ProbesPerLevel);
                RasterProbe.Require(new Vector3(after[k].X,after[k].Y,after[k].Z) == expectedCell, "Refreshed clipmap slot retained a stale world cell");
            }
            else
            {
                for (int v = 0; v < RasterWorldCache.Stride/16; v++) RasterProbe.Require(before[k+v] == after[k+v], "Scrolling changed an untouched clipmap slot");
                retained[level]++;
            }
        }
        RasterProbe.Require(updated.SequenceEqual(new[] {128,64,64}) && backend.WorldUpdates == 33, "Scroll/resize changed total budget or reset world history");
        Console.WriteLine($"PASS: far receiver red={mean:R}; 32-frame layer coverage=2048/2048/2048; scroll updates={string.Join('/',updated)}, retained={string.Join('/',retained)}; 16640 probe directions/frame.");
    }

    private static void VerifySchedule(Vector4[] probes, int frames, in CameraFrame camera, SceneAsset asset)
    {
        for (int level = 0; level < RasterWorldCache.Levels; level++)
        {
            int count = 0;
            for (int i = 0; i < RasterWorldCache.ProbesPerLevel; i++)
            {
                int k = (level*RasterWorldCache.ProbesPerLevel+i)*(RasterWorldCache.Stride/16);
                if (probes[k+1].W == 0) continue;
                count++;
                Vector3 expected = Cell(RasterWorldCache.Grid(asset,camera,level), i);
                RasterProbe.Require(new Vector3(probes[k].X,probes[k].Y,probes[k].Z) == expected, "Clipmap slot/world identity disagreement");
            }
            int budget = level == 0 ? RasterWorldCache.NearBudget : RasterWorldCache.FarBudget;
            RasterProbe.Require(count == Math.Min(frames*budget,RasterWorldCache.ProbesPerLevel), $"Layer {level} did not honor bounded schedule: {count}");
        }
    }

    private static Vector3 Cell(Vector4 grid, int index)
    {
        static int Mod(int value,int size) => (value%size+size)%size;
        return new(grid.X+Mod(index%16-(int)grid.X,16), grid.Y+Mod(index/16%8-(int)grid.Y,8), grid.Z+Mod(index/(16*8)-(int)grid.Z,16));
    }
}
