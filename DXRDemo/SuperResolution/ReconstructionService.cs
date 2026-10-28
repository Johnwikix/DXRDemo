using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.SuperResolution;

/// <summary>共享的厂商 SR/RR 端点与独立 NativeTAA；不依赖路径降噪或显示编码。</summary>
internal sealed unsafe class ReconstructionService : IDisposable
{
    private readonly DxrDevice _gpu = new();
    private readonly uint _capabilities;
    private readonly string _availability = "";
    private readonly List<IDisposable> _sized = [];
    private ComputePass? _sdkDepth, _taa;
    private ID3D12Resource? _constants, _depth, _output;
    private readonly ID3D12Resource[] _history = new ID3D12Resource[2], _historyDepth = new ID3D12Resource[2];
    private nint _context;
    private (ReconstructionMode Mode, int Scale, int Width, int Height, bool Rr, bool Taa) _configuration;
    private bool _configured, _disposed, _reset = true, _outputRead;
    private uint _frame;
    internal int ConfigurationVersion
    {
        get; private set;
    }
    internal ReconstructionStatus Status
    {
        get; private set;
    }
    internal ID3D12Resource Output => _output ?? throw new InvalidOperationException("重建尚未配置。");
    internal bool HasVendorContext => _context != 0;

    internal ReconstructionService(DxrDevice device)
    {
        try
        {
            _gpu.Initialize(device.Device, requireRayTracing: false);
        }
        catch { _gpu.Dispose(); throw; }
        try
        {
            _capabilities = NativeReconstruction.Capabilities(_gpu.Device.NativePointer);
            _availability = Marshal.PtrToStringUTF8(NativeReconstruction.FsrDiagnostic()) ?? "";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _availability = "超分 SDK 未安装或架构不匹配。";
        }
        Status = new(_capabilities, ReconstructionMode.Off, ReconstructionMode.Off, 0, 0, 0, 0, "原生分辨率");
    }

    internal void Configure(ReconstructionMode requested, int scale, int width, int height, bool allowRr, bool nativeTaa = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var configuration = (requested, scale, width, height, allowRr, nativeTaa);
        if (_configured && configuration == _configuration)
            return;
        _configured = true;
        _configuration = configuration;
        ConfigurationVersion++;
        ReleaseSizeResources();
        Reset();
        ReconstructionMode active = requested;
        string message = "";
        if (active == ReconstructionMode.DlssRayReconstruction && !allowRr)
        {
            active = Supports(ReconstructionMode.Dlss) ? ReconstructionMode.Dlss : ReconstructionMode.Off;
            message = "当前管线不提供 RR 路径信号，使用常规超分；";
        }
        if (!Supports(active))
        {
            message += $"{active} 在当前设备不可用；" + (active == ReconstructionMode.Fsr ? _availability : "");
            active = ReconstructionMode.Off;
        }
        int inputWidth = active == ReconstructionMode.Off ? width : Math.Max(1, width * scale / 100);
        int inputHeight = active == ReconstructionMode.Off ? height : Math.Max(1, height * scale / 100);
        bool recording = false;
        try
        {
            if (active != ReconstructionMode.Off)
            {
                _gpu.CommandAllocator.Reset();
                _gpu.CommandList.Reset(_gpu.CommandAllocator);
                recording = true;
                int result = NativeReconstruction.Create(_gpu.Device.NativePointer, _gpu.CommandList.NativePointer,
                    (int)active, 11, (uint)width, (uint)height, (uint)inputWidth, (uint)inputHeight, out _context);
                _gpu.CommandList.Close();
                recording = false;
                if (result != 0)
                    throw new InvalidOperationException($"{active} 初始化失败 ({result}) " + Marshal.PtrToStringUTF8(NativeReconstruction.NgxDiagnostic()));
                _gpu.CommandQueue.ExecuteCommandList(_gpu.CommandList);
                _gpu.SignalAndWait();
            }
        }
        catch (Exception error)
        {
            if (recording)
                _gpu.CommandList.Close();
            if (_context != 0)
            {
                NativeReconstruction.Destroy(_context);
                _context = 0;
            }
            if (active == ReconstructionMode.DlssRayReconstruction && Supports(ReconstructionMode.Dlss))
            {
                _configured = false;
                Configure(ReconstructionMode.Dlss, scale, width, height, false, nativeTaa);
                _configuration = configuration;
                Status = Status with
                {
                    Requested = requested,
                    Message = error.Message + "；回退 DLSS SR。"
                };
                return;
            }
            message += error.Message + "；使用原生分辨率。";
            active = ReconstructionMode.Off;
            inputWidth = width;
            inputHeight = height;
        }
        _output = Texture(width, height, Format.R16G16B16A16_Float, ResourceStates.UnorderedAccess);
        _depth = Texture(inputWidth, inputHeight, Format.R32_Float, ResourceStates.NonPixelShaderResource);
        if (nativeTaa && active == ReconstructionMode.Off)
            for (int i = 0; i < 2; i++)
            {
                _history[i] = Texture(width, height, Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource);
                _historyDepth[i] = Texture(width, height, Format.R32_Float, ResourceStates.NonPixelShaderResource);
            }
        // PSO/上传常量在配置阶段准备，避免插入到几何完成与重建提交之间。
        _constants ??= DxrResources.CreateUploadBuffer(_gpu.Device, 256);
        if (_context != 0)
            _sdkDepth ??= new ComputePass(_gpu.Device, "CommonReconstruction.hlsl", "SdkDepth", 6, 3);
        if (nativeTaa && active == ReconstructionMode.Off)
            _taa ??= new ComputePass(_gpu.Device, "CommonReconstruction.hlsl", "NativeTaa", 6, 3);
        Status = new(_capabilities, requested, active, inputWidth, inputHeight, width, height,
            message + (active == ReconstructionMode.Off ? nativeTaa ? "Native TAA" : "原生分辨率" : $"{active}: {inputWidth} × {inputHeight} → {width} × {height}"));
    }

    private bool Supports(ReconstructionMode mode) => mode == ReconstructionMode.Off || (_capabilities & (1u << (int)mode)) != 0;
    private ID3D12Resource Texture(int width, int height, Format format, ResourceStates state)
    {
        var resource = DxrResources.CreateUnorderedAccessTexture2D(_gpu.Device, (uint)width, (uint)height, format, state);
        _sized.Add(resource);
        return resource;
    }
    internal void Reset()
    {
        _reset = true;
        _frame = 0;
    }
    internal Vector2 Jitter(bool nativeTaa = false)
    {
        if (Status.Active == ReconstructionMode.Off && !nativeTaa)
            return default;
        uint phases = (uint)Math.Max(8, Math.Ceiling(8.0 * Status.OutputWidth * Status.OutputWidth / ((double)Status.InputWidth * Status.InputWidth)));
        uint sample = _frame % phases + 1;
        static float Halton(uint index, uint radix)
        {
            float value = 0, fraction = 1;
            while (index != 0)
            {
                fraction /= radix;
                value += fraction * (index % radix);
                index /= radix;
            }
            return value;
        }
        return new(Halton(sample, 2) - .5f, Halton(sample, 3) - .5f);
    }

    internal ID3D12Resource Record(ID3D12GraphicsCommandList4 commands, in LinearFrame frame,
        NativeReconstruction.RayReconstructionFrame? rayReconstruction = null)
    {
        if (frame.Width != Status.InputWidth || frame.Height != Status.InputHeight)
            throw new InvalidOperationException("LinearFrame 尺寸与有效重建配置不一致。");
        if (frame.Seconds > .25 || frame.Seconds <= 0)
            Reset();
        bool reset = frame.Reset || _reset;
        if (_context == 0 && !frame.NeedsNativeTaa)
        {
            _frame++;
            _reset = false;
            return frame.Color;
        }
        _constants ??= DxrResources.CreateUploadBuffer(_gpu.Device, 256);
        void* pointer;
        _constants.Map(0, null, &pointer).CheckError();
        ((Vector4*)pointer)[0] = new(frame.Width, frame.Height, reset ? 1 : 0, 0);
        ((Vector4*)pointer)[1] = new(frame.Jitter, frame.PreviousJitter.X, frame.PreviousJitter.Y);
        ((Vector4*)pointer)[2] = new(frame.Camera.Near, frame.Camera.Far, 0, 0);
        _constants.Unmap(0);
        if (_context != 0)
        {
            // SDK 桥保留 forward-Z ABI；公共帧始终 reverse-Z，在端点显式转换。
            _sdkDepth ??= new ComputePass(_gpu.Device, "CommonReconstruction.hlsl", "SdkDepth", 6, 3);
            _sdkDepth.Srv(1, frame.Depth);
            _sdkDepth.Uav(2, _depth!);
            commands.ResourceBarrierTransition(_depth!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            _sdkDepth.Dispatch(commands, _constants, frame.Width, frame.Height);
            commands.ResourceBarrierTransition(_depth!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            NativeReconstruction.Frame native = new()
            {
                Color = frame.Color.NativePointer,
                Depth = _depth!.NativePointer,
                Motion = frame.Motion.NativePointer,
                Reactive = frame.Reactive.NativePointer,
                Output = Output.NativePointer,
                Width = (uint)frame.Width,
                Height = (uint)frame.Height,
                JitterX = -frame.Jitter.X,
                JitterY = -frame.Jitter.Y,
                Milliseconds = (float)Math.Clamp(frame.Seconds * 1000, 1, 250),
                Reset = reset ? 1u : 0u,
                NearPlane = frame.Camera.Near,
                FarPlane = frame.Camera.Far,
                VerticalFieldOfView = frame.Camera.VerticalFov
            };
            int result;
            if (Status.Active == ReconstructionMode.DlssRayReconstruction)
            {
                var rr = rayReconstruction ?? throw new InvalidOperationException("DLSSD 路径 guides 缺失。");
                // RR 的 Color 是原始 noisy radiance，不能替换成路径降噪输出。
                native.Color = rr.Common.Color;
                rr.Common = native;
                result = NativeReconstruction.ExecuteDlssd(_context, commands.NativePointer, &rr);
            }
            else
                result = NativeReconstruction.Execute(_context, commands.NativePointer, &native);
            if (result != 0)
                throw new VendorReconstructionException($"{Status.Active} 执行失败 ({result}) " + Marshal.PtrToStringUTF8(NativeReconstruction.NgxDiagnostic()));
        }
        else
        {
            _taa ??= new ComputePass(_gpu.Device, "CommonReconstruction.hlsl", "NativeTaa", 6, 3);
            int slot = (int)(_frame % 2);
            _taa.Srv(0, frame.Color);
            _taa.Srv(1, frame.Depth);
            _taa.Srv(2, frame.Motion);
            _taa.Srv(3, frame.Reactive);
            _taa.Srv(4, _history[1 - slot]);
            _taa.Srv(5, _historyDepth[1 - slot]);
            _taa.Uav(0, Output);
            _taa.Uav(1, _history[slot]);
            _taa.Uav(2, _historyDepth[slot]);
            commands.ResourceBarrierTransition(_history[slot], ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            commands.ResourceBarrierTransition(_historyDepth[slot], ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            _taa.Dispatch(commands, _constants, frame.Width, frame.Height);
            commands.ResourceBarrierTransition(_history[slot], ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            commands.ResourceBarrierTransition(_historyDepth[slot], ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        }
        commands.ResourceBarrierTransition(Output, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        _outputRead = true;
        _frame++;
        _reset = false;
        return Output;
    }
    internal void Complete(ID3D12GraphicsCommandList4 commands)
    {
        if (_outputRead)
            commands.ResourceBarrierTransition(Output, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        _outputRead = false;
    }
    internal void FallbackToNative(string reason)
    {
        var requested = _configuration;
        _configured = false;
        Configure(ReconstructionMode.Off, 100, requested.Width, requested.Height, false, requested.Taa);
        // 保留请求作为缓存键，避免失败 SDK 在每帧被重新创建；显式改设置后才重试。
        _configuration = requested;
        Status = Status with { Requested = requested.Mode, Message = reason + "；" + Status.Message };
    }
    private void ReleaseSizeResources()
    {
        var cleanup = new ResourceCleanup();
        cleanup.Run(() => _gpu.SignalAndWait());
        if (_context != 0)
        {
            nint context = _context;
            _context = 0;
            cleanup.Run(() => NativeReconstruction.Destroy(context));
        }
        for (int i = _sized.Count - 1; i >= 0; i--)
            cleanup.Release(_sized[i]);
        _sized.Clear();
        _output = _depth = null;
        _outputRead = false;
        Array.Clear(_history);
        Array.Clear(_historyDepth);
        cleanup.Finish();
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var cleanup = new ResourceCleanup();
        cleanup.Run(ReleaseSizeResources);
        cleanup.Release(_taa);
        cleanup.Release(_sdkDepth);
        cleanup.Release(_constants);
        cleanup.Release(_gpu);
        cleanup.Finish();
    }
}

internal sealed class VendorReconstructionException(string message) : Exception(message);
