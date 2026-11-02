using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.Rendering;

/// <summary>光栅不透明 GI 专属的官方 RELAX_DIFFUSE_SPECULAR；借用调用方队列和 opaque guides。</summary>
internal sealed unsafe class RasterHardwareDenoiser : GpuResourceOwner
{
    private sealed class Context(nint pointer) : IDisposable
    {
        private nint _pointer = pointer;
        public void Dispose()
        {
            nint value = Interlocked.Exchange(ref _pointer, 0);
            if (value != 0) NativeNrd.Destroy(value);
        }
    }
    private readonly nint _context;
    private readonly ComputePass _prepare, _compose;
    private readonly ID3D12Resource _constants, _packedDiffuse, _packedSpecular, _normal, _viewZ, _motion, _diffuse, _specular;
    private readonly bool _cachedGi;
    private readonly int _width, _height, _sourceWidth, _sourceHeight, _divisor;
    internal ID3D12Resource DenoisedDiffuse => _diffuse;
    internal ID3D12Resource DenoisedSpecular => _specular;
    private bool _executed;
    internal uint DispatchCount => NativeNrd.DispatchCount(_context);
    internal uint MaxAccumulatedFrames => NativeNrd.MaxAccumulatedFrames(_context);
    internal ID3D12Resource DiffuseInput => _packedDiffuse;
    internal ID3D12Resource SpecularInput => _packedSpecular;
    internal ID3D12Resource ViewZ => _viewZ;
    internal ID3D12Resource Motion => _motion;

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        internal Vector4 Origin, Forward, Right, Up, PreviousOrigin, PreviousForward, PreviousRight, PreviousUp, Size, Jitter, SourceSize;
    }

    internal RasterHardwareDenoiser(ID3D12Device5 device, int width, int height, ID3D12Resource material, ID3D12Resource normal,
        ID3D12Resource depth, ID3D12Resource emission, ID3D12Resource direct, ID3D12Resource rawDiffuse, ID3D12Resource rawSpecular, ID3D12Resource output, int sourceWidth, int sourceHeight, int divisor, bool cachedGi)
    {
        _cachedGi = cachedGi;
        _width = width; _height = height; _sourceWidth = sourceWidth; _sourceHeight = sourceHeight; _divisor = divisor;
        try
        {
            if (NativeNrd.CreateRadiance(device.NativePointer, (uint)width, (uint)height, out _context) != 0)
                throw new InvalidOperationException(Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? "光栅 NRD 初始化失败。");
            Keep(new Context(_context));
            _prepare = Keep(new ComputePass(device, "RasterHardwareNrd.hlsl", "Prepare", 9, 6));
            _compose = Keep(new ComputePass(device, "RasterHardwareNrd.hlsl", "Composite", 9, 6));
            _constants = Keep(DxrResources.CreateUploadBuffer(device, 256));
            ID3D12Resource Texture(Format format) => Keep(DxrResources.CreateUnorderedAccessTexture2D(device, (uint)width, (uint)height, format, ResourceStates.NonPixelShaderResource));
            _packedDiffuse = Texture(Format.R16G16B16A16_Float);
            _packedSpecular = Texture(Format.R16G16B16A16_Float);
            _normal = Texture(Format.R16G16B16A16_Float);
            _viewZ = Texture(Format.R32_Float);
            _motion = Texture(Format.R16G16_Float);
            _diffuse = Texture(Format.R16G16B16A16_Float);
            _specular = Texture(Format.R16G16B16A16_Float);
            _prepare.Srv(0, material); _prepare.Srv(1, normal); _prepare.Srv(2, depth);
            _prepare.Srv(8, emission);
            _prepare.Srv(3, rawDiffuse); _prepare.Srv(4, rawSpecular);
            _prepare.Uav(0, _packedDiffuse); _prepare.Uav(1, _packedSpecular); _prepare.Uav(2, _normal);
            _prepare.Uav(3, _viewZ); _prepare.Uav(4, _motion);
            _compose.Srv(0, material); _compose.Srv(1, normal); _compose.Srv(2, depth);
            _compose.Srv(3, rawDiffuse); _compose.Srv(4, rawSpecular); _compose.Srv(8, emission);
            _compose.Srv(5, _diffuse); _compose.Srv(6, _specular); _compose.Srv(7, direct);
            _compose.Uav(5, output);
        }
        catch { Dispose(); throw; }
    }

    private Vector2 SampleOffset => _divisor == 1 ? default : new(.5f);
    private Matrix4x4 Projection(in CameraFrame camera)
    {
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(camera.VerticalFov,_sourceWidth/(float)_sourceHeight,camera.Near,camera.Far);
        // 低分辨率网格的最后一个不完整 block 作为无效 guide；投影覆盖完整的 padded 网格。
        float x=_sourceWidth/(float)(_width*_divisor), y=_sourceHeight/(float)(_height*_divisor);
        projection.M11*=x;projection.M22*=y;projection.M31=1-x;projection.M32=y-1;
        return projection;
    }

    private void Inputs(ID3D12GraphicsCommandList4 commands, bool write)
    {
        ResourceStates before = write ? ResourceStates.NonPixelShaderResource : ResourceStates.UnorderedAccess;
        ResourceStates after = write ? ResourceStates.UnorderedAccess : ResourceStates.NonPixelShaderResource;
        commands.ResourceBarrierTransition(_packedDiffuse, before, after);
        commands.ResourceBarrierTransition(_packedSpecular, before, after);
        commands.ResourceBarrierTransition(_normal, before, after);
        commands.ResourceBarrierTransition(_viewZ, before, after);
        commands.ResourceBarrierTransition(_motion, before, after);
    }

    internal void Record(ID3D12GraphicsCommandList4 commands, in CameraFrame camera, in CameraFrame previous,
        Vector2 jitter, Vector2 previousJitter, uint frameIndex, double seconds)
    {
        bool reset = !_executed || frameIndex == 0;
        CameraFrame oldCamera = reset ? camera : previous;
        Vector2 oldJitter = reset ? jitter : previousJitter;
        Constants data = new()
        {
            Origin = new(camera.Origin, camera.Near), Forward = new(camera.Forward, MathF.Tan(camera.VerticalFov * .5f)),
            Right = new(camera.Right, camera.Far), Up = new(camera.Up, 0),
            PreviousOrigin = new(oldCamera.Origin, oldCamera.Near), PreviousForward = new(oldCamera.Forward, MathF.Tan(oldCamera.VerticalFov * .5f)),
            PreviousRight = new(oldCamera.Right, oldCamera.Far), PreviousUp = new(oldCamera.Up, 0),
            Size = new(_width, _height, reset ? 1 : 0, 0), Jitter = new(jitter.X, jitter.Y, oldJitter.X, oldJitter.Y),
            SourceSize = new(_sourceWidth, _sourceHeight, _divisor, _cachedGi ? 1 : 0)
        };
        void* pointer;
        _constants.Map(0, null, &pointer).CheckError(); *(Constants*)pointer = data; _constants.Unmap(0);
        Inputs(commands, true);
        _prepare.Dispatch(commands, _constants, _width, _height);
        Inputs(commands, false);
        NativeNrd.Frame frame = new()
        {
            Color = _packedDiffuse.NativePointer, Specular = _packedSpecular.NativePointer,
            NormalRoughness = _normal.NativePointer, ViewZ = _viewZ.NativePointer, Motion = _motion.NativePointer,
            Output = _diffuse.NativePointer, SpecularOutput = _specular.NativePointer,
            WorldToView = Matrix4x4.CreateLookAt(camera.Origin, camera.Origin + camera.Forward, camera.Up),
            WorldToViewPrevious = Matrix4x4.CreateLookAt(oldCamera.Origin, oldCamera.Origin + oldCamera.Forward, oldCamera.Up),
            ViewToClip = Projection(camera),
            ViewToClipPrevious = Projection(oldCamera),
            Jitter = (jitter + SampleOffset) / _divisor, PreviousJitter = (oldJitter + SampleOffset) / _divisor, Milliseconds = (float)Math.Clamp(seconds * 1000, 1, 250),
            FrameIndex = frameIndex, Reset = reset ? 1u : 0u, DenoisingRange = camera.Far,
            MaxAccumulatedFrames = camera == oldCamera ? NativeNrd.DefaultMaxAccumulatedFrames : NativeNrd.MotionMaxAccumulatedFrames
        };
        if (NativeNrd.Execute(_context, commands.NativePointer, &frame) != 0)
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? "光栅 NRD 执行失败。");
        if (_divisor == 1) _compose.Dispatch(commands, _constants, _width, _height);
        _executed = true;
    }
}
