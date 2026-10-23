using System.Numerics;
using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.SuperResolution;

/// <summary>Independent NRD histories for reflected/refracted virtual surfaces; primary NRD stays in its existing pipeline.</summary>
internal sealed unsafe class StablePlaneDenoiser : IDisposable
{
    private const int Descriptors = 12;
    private readonly DxrDevice _gpu;
    private readonly List<IDisposable> _owned = [];
    private readonly nint[] _contexts = new nint[2];
    private readonly ID3D12Resource?[] _inputs = new ID3D12Resource?[6];
    private readonly ID3D12Resource[] _denoised = new ID3D12Resource[2];
    private readonly int _width, _height, _stride;
    private ID3D12RootSignature _root = null!;
    private ID3D12PipelineState _prepare = null!, _compose = null!;
    private ID3D12DescriptorHeap _heap = null!;
    private ID3D12Resource _constants = null!, _packed = null!, _normal = null!, _viewZ = null!, _motion = null!;
    private PbrSignals? _bound;
    private CameraFrame _previous;
    private Vector2 _previousJitter;
    private uint _frame;
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants { public Vector4 Origin, Forward, Right, Up, PreviousOrigin, PreviousForward, PreviousRight, PreviousUp, Size, Jitter; }
    private T Keep<T>(T value) where T : IDisposable { _owned.Add(value); return value; }

    internal StablePlaneDenoiser(DxrDevice gpu, int width, int height)
    {
        _gpu = gpu;
        var device = gpu.Device;
        _width = width; _height = height;
        try
        {
            _stride = (int)device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            _root = Keep(device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 7, 0),
                    new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 5, 0)), ShaderVisibility.All)])));
            ID3D12PipelineState Compile(string entry)
            {
                using var compiler = new DxrShaderCompiler();
                compiler.CompileCompute(Path.Combine(AppContext.BaseDirectory, "Shaders/DXR/StableReconstruction.hlsl"), entry);
                return Keep(device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = _root, ComputeShader = compiler.DxilBytes }));
            }
            _prepare = Compile("PrepareStable"); _compose = Compile("ComposeStable");
            _heap = Keep(device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3 * Descriptors, DescriptorHeapFlags.ShaderVisible)));
            _constants = Keep(DxrResources.CreateUploadBuffer(device, 256));
            ID3D12Resource Texture(Format format) => Keep(DxrResources.CreateUnorderedAccessTexture2D(device, (uint)width, (uint)height, format, ResourceStates.NonPixelShaderResource));
            _packed = Texture(Format.R16G16B16A16_Float); _normal = Texture(Format.R16G16B16A16_Float);
            _viewZ = Texture(Format.R32_Float); _motion = Texture(Format.R16G16_Float);
            for (int i = 0; i < 2; i++)
            {
                _denoised[i] = Texture(Format.R16G16B16A16_Float);
                if (NativeNrd.Create(device.NativePointer, (uint)width, (uint)height, out _contexts[i]) != 0)
                    throw new InvalidOperationException(Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()));
            }
            var nullSrv = new ShaderResourceViewDescription { Format = Format.R32G32B32A32_Float,
                ViewDimension = ShaderResourceViewDimension.Texture2D, Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new() { MipLevels = 1 } };
            var nullUav = new UnorderedAccessViewDescription { Format = Format.R32G32B32A32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D };
            for (int set = 0; set < 3; set++)
            {
                for (int i = 0; i < 7; i++) device.CreateShaderResourceView(null, nullSrv, _heap.GetCPUDescriptorHandleForHeapStart() + (set * Descriptors + i) * _stride);
                for (int i = 0; i < 5; i++) device.CreateUnorderedAccessView(null, null, nullUav, _heap.GetCPUDescriptorHandleForHeapStart() + (set * Descriptors + 7 + i) * _stride);
            }
        }
        catch { Dispose(); throw; }
    }
    private static ID3D12Resource Resource(ReadWriteTexture2D<Float4> texture)
    {
        Guid iid = typeof(ID3D12Resource).GUID; nint pointer;
        InteropServices.GetID3D12Resource(texture, &iid, (void**)&pointer); return new(pointer);
    }
    private void Srv(int set, int slot, ID3D12Resource resource) => _gpu.Device.CreateShaderResourceView(resource, null,
        _heap.GetCPUDescriptorHandleForHeapStart() + (set * Descriptors + slot) * _stride);
    private void Uav(int set, int slot, ID3D12Resource resource) => _gpu.Device.CreateUnorderedAccessView(resource, null, null,
        _heap.GetCPUDescriptorHandleForHeapStart() + (set * Descriptors + 7 + slot) * _stride);
    private void Dispatch(int set, ID3D12PipelineState pipeline)
    {
        var cmd = _gpu.CommandList; cmd.SetDescriptorHeaps(_heap); cmd.SetComputeRootSignature(_root);
        cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
        cmd.SetComputeRootDescriptorTable(1, _heap.GetGPUDescriptorHandleForHeapStart() + set * Descriptors * _stride);
        cmd.SetPipelineState(pipeline); cmd.Dispatch((uint)(_width + 7) / 8, (uint)(_height + 7) / 8, 1);
    }
    internal void Execute(PbrSignals signals, ID3D12Resource primary, ID3D12Resource output, CameraFrame camera, Float2 jitter, double seconds, bool reset)
    {
        if (!ReferenceEquals(_bound, signals))
        {
            foreach (var old in _inputs) old?.Dispose();
            _inputs[0] = Resource(signals.StableReflection); _inputs[1] = Resource(signals.StableReflectionSurface); _inputs[2] = Resource(signals.StableReflectionNormal);
            _inputs[3] = Resource(signals.StableTransmission); _inputs[4] = Resource(signals.StableTransmissionSurface); _inputs[5] = Resource(signals.StableTransmissionNormal);
            _bound = signals; reset = true;
        }
        if (reset || _frame == 0) { _previous = camera; _previousJitter = new(jitter.X, jitter.Y); _frame = 0; }
        Constants data = new() { Origin = new(camera.Origin, 0), Forward = new(camera.Forward, MathF.Tan(camera.VerticalFov * .5f)),
            Right = new(camera.Right, 0), Up = new(camera.Up, 0), PreviousOrigin = new(_previous.Origin, 0), PreviousForward = new(_previous.Forward, 0),
            PreviousRight = new(_previous.Right, 0), PreviousUp = new(_previous.Up, 0), Size = new(_width, _height, camera.Far, 0),
            Jitter = new(jitter.X, jitter.Y, _previousJitter.X, _previousJitter.Y) };
        void* pointer; _constants.Map(0, null, &pointer).CheckError(); *(Constants*)pointer = data; _constants.Unmap(0);
        for (int plane = 0; plane < 2; plane++)
        {
            for (int i = 0; i < 3; i++) Srv(plane, i, _inputs[plane * 3 + i]!);
            Uav(plane, 0, _packed); Uav(plane, 1, _normal); Uav(plane, 2, _viewZ); Uav(plane, 3, _motion);
        }
        Srv(2, 0, _inputs[0]!); Srv(2, 1, _inputs[1]!); Srv(2, 2, _inputs[3]!); Srv(2, 3, _inputs[4]!);
        Srv(2, 4, _denoised[0]); Srv(2, 5, _denoised[1]); Srv(2, 6, primary); Uav(2, 4, output);
        // Record into the caller's open list. Resource barriers order the three
        // denoisers and composition without intermediate CPU fence waits.
        var cmd = _gpu.CommandList;
        foreach (var input in _inputs) cmd.ResourceBarrierTransition(input!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        for (int plane = 0; plane < 2; plane++)
        {
            cmd.ResourceBarrierTransition(_packed, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cmd.ResourceBarrierTransition(_normal, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cmd.ResourceBarrierTransition(_viewZ, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cmd.ResourceBarrierTransition(_motion, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Dispatch(plane, _prepare);
            cmd.ResourceBarrierTransition(_packed, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cmd.ResourceBarrierTransition(_normal, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cmd.ResourceBarrierTransition(_viewZ, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cmd.ResourceBarrierTransition(_motion, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            NativeNrd.Frame frame = new()
            {
                Color = _packed.NativePointer, NormalRoughness = _normal.NativePointer, ViewZ = _viewZ.NativePointer, Motion = _motion.NativePointer, Output = _denoised[plane].NativePointer,
                WorldToView = Matrix4x4.CreateLookAt(camera.Origin, camera.Origin + camera.Forward, camera.Up),
                WorldToViewPrevious = Matrix4x4.CreateLookAt(_previous.Origin, _previous.Origin + _previous.Forward, _previous.Up),
                ViewToClip = Matrix4x4.CreatePerspectiveFieldOfView(camera.VerticalFov, _width / (float)_height, camera.Near, camera.Far),
                Jitter = new(jitter.X, jitter.Y), PreviousJitter = _previousJitter, Milliseconds = (float)Math.Clamp(seconds * 1000, 1, 250),
                FrameIndex = _frame, Reset = _frame == 0 ? 1u : 0u, DenoisingRange = camera.Far
            };
            if (NativeNrd.Execute(_contexts[plane], cmd.NativePointer, &frame) != 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()));
        }
        Dispatch(2, _compose); cmd.ResourceBarrierUnorderedAccessView(output);
        foreach (var input in _inputs) cmd.ResourceBarrierTransition(input!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        _previous = camera; _previousJitter = new(jitter.X, jitter.Y); _frame++;
    }
    public void Dispose()
    {
        if (_gpu.Device != null) _gpu.SignalAndWait();
        foreach (nint context in _contexts) if (context != 0) NativeNrd.Destroy(context);
        foreach (var input in _inputs) input?.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
}
