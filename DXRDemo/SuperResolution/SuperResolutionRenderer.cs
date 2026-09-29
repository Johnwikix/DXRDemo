using ComputeSharp;
using ComputeSharp.Interop;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Shaders.RayTrace;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.SuperResolution;

/// <summary>Owns linear denoising, vendor reconstruction and encoding on a synchronized render-thread queue.</summary>
internal sealed unsafe class SuperResolutionRenderer : IDisposable
{
    private const int DescriptorsPerSet = 12;
    private readonly DxrDevice _gpu = new();
    private readonly List<IDisposable> _pipelineResources = [];
    private readonly List<IDisposable> _sizeResources = [];
    private readonly uint _capabilities;
    private readonly bool _nrdAvailable;
    private string _nrdMessage = "";
    private readonly string _availabilityMessage = "";
    private ID3D12RootSignature _root = null!;
    private ID3D12PipelineState _prepare = null!, _filter = null!, _encode = null!;
    private ID3D12PipelineState _prepareNrd = null!, _composeNrd = null!;
    private ID3D12DescriptorHeap _heap = null!;
    private ID3D12Resource _constants = null!;
    private nint _constantPointer, _context;
    private nint _nrdContext;
    private int _descriptorStride;
    private ID3D12Resource _color = null!, _depth = null!, _motion = null!, _reactive = null!, _output = null!;
    private ID3D12Resource _viewZ = null!, _nrdOutput = null!;
    private readonly ID3D12Resource[] _history = new ID3D12Resource[2];
    private readonly ID3D12Resource[] _filters = new ID3D12Resource[2];
    private ID3D12Resource? _raw, _normals, _surfaces;
    private ID3D12Resource? _nrdNormals;
    private ReadWriteTexture2D<Float4>? _boundRaw;
    private readonly ReadWriteTexture2D<Rgba64, Float4>?[] _targets = new ReadWriteTexture2D<Rgba64, Float4>?[2];
    private readonly ID3D12Resource?[] _targetResources = new ID3D12Resource?[2];
    private int _targetSlot;
    private (ReconstructionMode Mode, int Scale, int Width, int Height, bool Nrd) _configuration;
    private bool _configured, _disposed, _recording, _pipelinesReady, _reset = true;
    private uint _frame;
    private Float2 _previousJitter;
    private Camera _previousCamera;
    private ReconstructionStatus _status;

    internal SuperResolutionRenderer(GraphicsDevice device)
    {
        Guid iid = typeof(ID3D12Device5).GUID;
        nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        _gpu.Initialize(new ID3D12Device5(pointer));
        try
        {
            _capabilities = NativeReconstruction.Capabilities(_gpu.Device.NativePointer);
            if ((_capabilities & (1u << (int)ReconstructionMode.Fsr)) == 0)
                _availabilityMessage = Marshal.PtrToStringUTF8(NativeReconstruction.FsrDiagnostic()) ?? "";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _availabilityMessage = "超分 SDK 未安装或架构不匹配。";
        }
        try
        {
            _nrdAvailable = NativeNrd.Available() != 0;
            if (!_nrdAvailable) _nrdMessage = Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? "NRD 不可用";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { _nrdMessage = "NRD SDK 未安装或架构不匹配"; }
        _status = new(_capabilities, ReconstructionMode.Off, ReconstructionMode.Off, 0, 0, 0, 0, "原生分辨率", _nrdAvailable);
    }

    internal ReconstructionStatus Status => Volatile.Read(ref _status);
    internal ReconstructionMode Active => Status.Active;
    internal bool LinearPipelineActive => Active != ReconstructionMode.Off || _nrdContext != 0;
    internal uint NrdDispatchCount => _nrdContext == 0 ? 0 : NativeNrd.DispatchCount(_nrdContext);
    internal int InputWidth => Status.InputWidth;
    internal int InputHeight => Status.InputHeight;
    internal bool HistoryReset => _reset;
    // Exposed internally for the offscreen resource-contract probe, never used by the UI.
    internal ID3D12Resource Depth => _depth;
    internal ID3D12Resource Motion => _motion;
    internal ID3D12Resource LinearOutput => _output;
    internal ID3D12Resource DenoisedColor => _color;
    internal ID3D12Resource NrdViewZ => _viewZ;
    /// <summary>Gets the most recently written linear denoiser history for offscreen regression checks.</summary>
    internal ID3D12Resource DenoiserHistory => _history[(int)((_frame - 1) % 2)];

    private T Keep<T>(T value) where T : IDisposable { _pipelineResources.Add(value); return value; }
    private T Sized<T>(T value) where T : IDisposable { _sizeResources.Add(value); return value; }

    internal void Configure(ReconstructionMode mode, int scale, int outputWidth, int outputHeight, bool nrd = false)
    {
        var configuration = (mode, scale, outputWidth, outputHeight, nrd);
        if (_configured && _configuration == configuration) return;
        _configured = true;
        _configuration = configuration;
        ReleaseSizeResources();
        Reset();
        bool useNrd = nrd && _nrdAvailable;
        string message = "原生分辨率";
        if (mode != ReconstructionMode.Off && (_capabilities & (1u << (int)mode)) == 0)
        {
            message = $"{mode} 在当前设备不可用，使用原生分辨率。" + (mode == ReconstructionMode.Fsr ? _availabilityMessage : "");
            mode = ReconstructionMode.Off;
        }
        if (mode == ReconstructionMode.Off && !useNrd)
        {
            Publish(ReconstructionMode.Off, outputWidth, outputHeight, message);
            return;
        }

        int width = mode == ReconstructionMode.Off ? outputWidth : Math.Max(1, outputWidth * scale / 100);
        int height = mode == ReconstructionMode.Off ? outputHeight : Math.Max(1, outputHeight * scale / 100);
        try
        {
            EnsurePipelines();
            if (mode != ReconstructionMode.Off)
            {
                Begin();
                int result = NativeReconstruction.Create(_gpu.Device.NativePointer, _gpu.CommandList.NativePointer,
                    (int)mode, 11, (uint)outputWidth, (uint)outputHeight, (uint)width, (uint)height, out _context);
                if (result != 0) throw new InvalidOperationException($"{mode} 初始化失败 ({result})");
                End(); // NGX creation can record initialization commands.
                message = $"{mode}: {width} × {height} → {outputWidth} × {outputHeight}";
            }
            if (useNrd)
            {
                int result = NativeNrd.Create(_gpu.Device.NativePointer, (uint)width, (uint)height, out _nrdContext);
                if (result != 0)
                {
                    _nrdMessage = Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? $"NRD 初始化失败 ({result})";
                    if (mode == ReconstructionMode.Off) { Publish(mode, width, height, message); return; }
                }
                else
                {
                    _viewZ = Texture(width, height, Format.R32_Float);
                    _nrdOutput = Texture(width, height, Format.R16G16B16A16_Float);
                }
            }
            _color = Texture(width, height, Format.R16G16B16A16_Float);
            _depth = Texture(width, height, Format.R32_Float);
            _motion = Texture(width, height, Format.R16G16_Float);
            _reactive = Texture(width, height, Format.R8_UNorm);
            _output = Texture(outputWidth, outputHeight, Format.R16G16B16A16_Float, ResourceStates.UnorderedAccess);
            for (int i = 0; i < 2; i++)
            {
                _history[i] = Texture(width, height, Format.R32G32B32A32_Float);
                _filters[i] = Texture(width, height, Format.R16G16B16A16_Float);
            }
            Publish(mode, width, height, message);
        }
        catch (Exception e)
        {
            Discard(); // Discard partially recorded work; no resource state has changed on the GPU.
            ReleaseSizeResources();
            Publish(ReconstructionMode.Off, outputWidth, outputHeight, $"{e.Message}；使用原生分辨率。");
        }
    }

    private void Publish(ReconstructionMode active, int width, int height, string message)
        => Volatile.Write(ref _status, new(_capabilities, _configuration.Mode, active, width, height,
            _configuration.Width, _configuration.Height, message + (_nrdContext != 0 ? " · NVIDIA NRD RELAX" :
                _configuration.Nrd ? $" · NRD 不可用，使用旧版滤波：{_nrdMessage}" : ""), _nrdAvailable, _nrdContext != 0));

    private ID3D12Resource Texture(int width, int height, Format format,
        ResourceStates state = ResourceStates.NonPixelShaderResource)
        => Sized(DxrResources.CreateUnorderedAccessTexture2D(_gpu.Device, (uint)width, (uint)height, format, state));

    private void EnsurePipelines()
    {
        if (_pipelinesReady) return;
        ReleasePipelines();
        _root = Keep(_gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
             new RootParameter1(new RootDescriptorTable1(
                new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, 0, 0),
                 new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 6, 0, 0)), ShaderVisibility.All)])));
        string file = Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", "Reconstruction.hlsl");
        _prepare = Compile(file, "Prepare");
        _filter = Compile(file, "Filter");
        _encode = Compile(file, "Encode");
        _prepareNrd = Compile(file, "PrepareNrd");
        _composeNrd = Compile(file, "ComposeNrd");
        _heap = Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, DescriptorsPerSet * 9, DescriptorHeapFlags.ShaderVisible)));
        _descriptorStride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        // .NET 10: 常量持久映射，帧内只写入值类型，描述符与资源复用。
        _constants = Keep(DxrResources.CreateUploadBuffer(_gpu.Device, 256 * 7));
        void* pointer;
        _constants.Map(0, null, &pointer).CheckError();
        _constantPointer = (nint)pointer;
        // Static descriptor tables must be fully initialized even when an entry point only uses a subset.
        var nullSrv = new ShaderResourceViewDescription
        {
            Format = Format.R32G32B32A32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
        };
        var nullUav = new UnorderedAccessViewDescription
        {
            Format = Format.R32G32B32A32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D
        };
        for (int set = 0; set < 9; set++) for (int i = 0; i < 6; i++)
        {
            var start = _heap.GetCPUDescriptorHandleForHeapStart() + set * DescriptorsPerSet * _descriptorStride;
            _gpu.Device.CreateShaderResourceView(null, nullSrv, start + i * _descriptorStride);
            _gpu.Device.CreateUnorderedAccessView(null, null, nullUav, start + (6 + i) * _descriptorStride);
        }
        _pipelinesReady = true;
    }

    private ID3D12PipelineState Compile(string file, string entry)
    {
        using var compiler = new DxrShaderCompiler();
        compiler.CompileCompute(file, entry);
        return Keep(_gpu.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _root, ComputeShader = compiler.DxilBytes
        }));
    }

    internal void Reset() { _reset = true; _frame = 0; _previousJitter = default; }

    internal Float2 Jitter()
    {
        if (Active == ReconstructionMode.Off) return default;
        uint phases = (uint)Math.Max(8, Math.Ceiling(8.0 * Status.OutputWidth * Status.OutputWidth / ((double)InputWidth * InputWidth)));
        uint sample = _frame % phases + 1;
        return new(Halton(sample, 2) - 0.5f, Halton(sample, 3) - 0.5f);
    }

    private static float Halton(uint index, uint radix)
    {
        float sum = 0, fraction = 1;
        while (index != 0) { fraction /= radix; sum += fraction * (index % radix); index /= radix; }
        return sum;
    }

    private static ID3D12Resource Resource<T>(ReadWriteTexture2D<T> texture) where T : unmanaged
    {
        Guid iid = typeof(ID3D12Resource).GUID;
        nint pointer;
        InteropServices.GetID3D12Resource(texture, &iid, (void**)&pointer);
        return new(pointer);
    }

    private static ID3D12Resource Resource<T>(ReadWriteTexture2D<T, Float4> texture)
        where T : unmanaged, IPixel<T, Float4>
    {
        Guid iid = typeof(ID3D12Resource).GUID;
        nint pointer;
        InteropServices.GetID3D12Resource(texture, &iid, (void**)&pointer);
        return new(pointer);
    }

    private void BindInputs(ReadWriteTexture2D<Float4> raw, ReadWriteTexture2D<Rgba32, Float4> normals,
        ReadWriteTexture2D<Float4> surfaces, ReadWriteTexture2D<Float4>? nrdNormals)
    {
        if (ReferenceEquals(raw, _boundRaw)) return;
        _raw?.Dispose(); _normals?.Dispose(); _surfaces?.Dispose(); _nrdNormals?.Dispose();
        _raw = Resource(raw); _normals = Resource(normals); _surfaces = Resource(surfaces);
        _nrdNormals = nrdNormals == null ? null : Resource(nrdNormals);
        _boundRaw = raw;
    }

    private ID3D12Resource BindTarget(ReadWriteTexture2D<Rgba64, Float4> target)
    {
        for (int i = 0; i < 2; i++) if (ReferenceEquals(_targets[i], target)) return _targetResources[i]!;
        int slot = _targetSlot++ % 2;
        _targetResources[slot]?.Dispose();
        _targets[slot] = target;
        return _targetResources[slot] = Resource(target);
    }

    private void Srv(int set, int index, ID3D12Resource resource)
        => _gpu.Device.CreateShaderResourceView(resource, null, _heap.GetCPUDescriptorHandleForHeapStart() + (set * DescriptorsPerSet + index) * _descriptorStride);

    private void Uav(int set, int index, ID3D12Resource resource)
        => _gpu.Device.CreateUnorderedAccessView(resource, null, null, _heap.GetCPUDescriptorHandleForHeapStart() + (set * DescriptorsPerSet + 6 + index) * _descriptorStride);

    private void Bind(int set, ID3D12PipelineState pipeline, int constants)
    {
        var cmd = _gpu.CommandList;
        cmd.SetDescriptorHeaps(_heap);
        cmd.SetComputeRootSignature(_root);
        cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress + (ulong)(constants * 256));
        cmd.SetComputeRootDescriptorTable(1, _heap.GetGPUDescriptorHandleForHeapStart() + set * DescriptorsPerSet * _descriptorStride);
        cmd.SetPipelineState(pipeline);
    }

    private void Transition(ID3D12Resource resource, ResourceStates before, ResourceStates after)
        => _gpu.CommandList.ResourceBarrierTransition(resource, before, after);

    internal bool Execute(ReadWriteTexture2D<Float4> raw, ReadWriteTexture2D<Rgba32, Float4> normals,
        ReadWriteTexture2D<Float4> surfaces, ReadWriteTexture2D<Rgba64, Float4> target,
        Float2 orbit, float distance, Float2 jitter, RayTraceDenoiserMode denoiser, in HdrRenderParameters hdr, double seconds,
        ReadWriteTexture2D<Float4>? nrdNormals = null)
    {
        if (seconds > 0.25 || seconds <= 0) Reset();
        BindInputs(raw, normals, surfaces, nrdNormals);
        ID3D12Resource encoded = BindTarget(target);
        Camera camera = Camera.Create(orbit, distance);
        if (_reset) { _previousCamera = camera; _previousJitter = jitter; }
        Constants data = new()
        {
            Origin = new(camera.Origin, 0), Forward = new(camera.Forward, 0), Right = new(camera.Right, 0), Up = new(camera.Up, 0),
            PreviousOrigin = new(_previousCamera.Origin, 0), PreviousForward = new(_previousCamera.Forward, 0),
            PreviousRight = new(_previousCamera.Right, 0), PreviousUp = new(_previousCamera.Up, 0),
            Size = new(InputWidth, InputHeight, target.Width, target.Height),
            Jitter = new(jitter.X, jitter.Y, _previousJitter.X, _previousJitter.Y),
            Control = new(_reset ? 1 : 0, (int)denoiser, Math.Min(_frame, 31), 0),
            Output = new(hdr.IsHdrEnabled ? 1 : 0, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits, 0)
        };
        *(Constants*)_constantPointer = data;
        for (int i = 0; i < 5; i++)
        {
            data.Control.W = i;
            *(Constants*)(_constantPointer + (i + 1) * 256) = data;
        }
        int historySlot = (int)(_frame % 2);
        Srv(0, 0, _raw!); Srv(0, 1, _normals!); Srv(0, 2, _surfaces!); Srv(0, 3, _history[1 - historySlot]);
        Uav(0, 0, _color); Uav(0, 1, _depth); Uav(0, 2, _motion); Uav(0, 3, _reactive); Uav(0, 4, _history[historySlot]);
        Uav(6, 5, encoded);
        try
        {
            Begin();
            Transition(_raw!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_normals!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_surfaces!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_color, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_depth, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_motion, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_reactive, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_history[historySlot], ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Bind(0, _prepare, 0);
            _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
            Transition(_color, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_depth, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_motion, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_reactive, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_history[historySlot], ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            ID3D12Resource input = _color;
            if (_nrdContext != 0)
            {
                if (_nrdNormals == null) throw new InvalidOperationException("NRD normal/roughness guide missing");
                Srv(7, 0, _raw!); Srv(7, 1, _normals!); Srv(7, 2, _surfaces!);
                Uav(7, 0, _color); Uav(7, 1, _viewZ);
                Transition(_color, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                Transition(_viewZ, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                Transition(_nrdNormals, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                Bind(7, _prepareNrd, 0);
                _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
                Transition(_color, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                Transition(_viewZ, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                // .NET 10: 值类型矩阵直接写入栈上的 ABI 帧，复用 NRD 资源，不在渲染循环分配数组。
                NativeNrd.Frame nrdFrame = new()
                {
                    Color = _color.NativePointer, NormalRoughness = _nrdNormals.NativePointer,
                    ViewZ = _viewZ.NativePointer, Motion = _motion.NativePointer, Output = _nrdOutput.NativePointer,
                    WorldToView = Matrix4x4.CreateLookAt(camera.Origin, camera.Origin + camera.Forward, camera.Up),
                    WorldToViewPrevious = Matrix4x4.CreateLookAt(_previousCamera.Origin, _previousCamera.Origin + _previousCamera.Forward, _previousCamera.Up),
                    ViewToClip = Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.Atan(0.5f), InputWidth / (float)InputHeight, 0.01f, 1000),
                    // NRD asks for ray sample offsets; SR SDKs below ask for projection offsets.
                    Jitter = new(jitter.X, jitter.Y), PreviousJitter = new(_previousJitter.X, _previousJitter.Y),
                    Milliseconds = (float)Math.Clamp(seconds * 1000, 1, 250), FrameIndex = _frame, Reset = _reset ? 1u : 0u
                };
                if (NativeNrd.Execute(_nrdContext, _gpu.CommandList.NativePointer, &nrdFrame) != 0)
                    throw new InvalidOperationException(Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? "NRD 执行失败");
                Srv(8, 0, _raw!); Srv(8, 1, _normals!); Srv(8, 2, _surfaces!); Srv(8, 5, _nrdOutput);
                Uav(8, 0, _color);
                Transition(_color, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                Bind(8, _composeNrd, 0);
                _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
                Transition(_color, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                Transition(_nrdNormals, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            }
            else if (denoiser == RayTraceDenoiserMode.Relax)
            {
                for (int i = 0; i < 5; i++)
                {
                    ID3D12Resource output = _filters[i % 2];
                    Srv(i + 1, 1, _normals!); Srv(i + 1, 2, _surfaces!); Srv(i + 1, 4, input); Uav(i + 1, 0, output);
                    Transition(output, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Bind(i + 1, _filter, i + 1);
                    _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
                    Transition(output, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    input = output;
                }
            }
            if (_context != 0)
            {
                NativeReconstruction.Frame frame = new()
                {
                    Color = input.NativePointer, Depth = _depth.NativePointer, Motion = _motion.NativePointer,
                    Reactive = _reactive.NativePointer, Output = _output.NativePointer,
                    Width = (uint)InputWidth, Height = (uint)InputHeight,
                    // SDKs receive projection offsets; path-traced primary rays use inverse projection.
                    JitterX = -jitter.X, JitterY = -jitter.Y, Milliseconds = (float)Math.Clamp(seconds * 1000, 1, 250),
                    Reset = _reset ? 1u : 0u, NearPlane = 0.01f, FarPlane = 1000, VerticalFieldOfView = 2 * MathF.Atan(0.5f)
                };
                int result = NativeReconstruction.Execute(_context, _gpu.CommandList.NativePointer, &frame);
                if (result != 0) throw new InvalidOperationException($"{Active} 执行失败 ({result})");
                Transition(_output, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                input = _output;
            }
            Srv(6, 5, input);
            Bind(6, _encode, 0); // Vendor dispatch changes root bindings and descriptor heaps.
            _gpu.CommandList.Dispatch((uint)(target.Width + 7) / 8, (uint)(target.Height + 7) / 8, 1);
            _gpu.CommandList.ResourceBarrierUnorderedAccessView(encoded);
            if (_context != 0) Transition(_output, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_raw!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_normals!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_surfaces!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            End(); // Complete before ComputeSharp HUD or presentation consumes the shared target.
            _previousCamera = camera; _previousJitter = jitter; _frame++; _reset = false;
            return true;
        }
        catch (Exception e)
        {
            Discard();
            ReleaseSizeResources();
            if (_configuration.Nrd) _nrdMessage = e.Message;
            Publish(ReconstructionMode.Off, target.Width, target.Height, $"{e.Message}；使用原生分辨率。");
            return false;
        }
    }

    private void Begin()
    {
        _gpu.CommandAllocator.Reset();
        _gpu.CommandList.Reset(_gpu.CommandAllocator);
        _recording = true;
    }

    private void End()
    {
        _gpu.CommandList.Close();
        _recording = false;
        _gpu.CommandQueue.ExecuteCommandList(_gpu.CommandList);
        _gpu.SignalAndWait();
        _gpu.Device.DeviceRemovedReason.CheckError();
    }

    private void Discard()
    {
        if (!_recording) return;
        _gpu.CommandList.Close();
        _recording = false;
    }

    private void ReleasePipelines()
    {
        if (_constantPointer != 0) { _constants.Unmap(0); _constantPointer = 0; }
        for (int i = _pipelineResources.Count - 1; i >= 0; i--) _pipelineResources[i].Dispose();
        _pipelineResources.Clear();
        _pipelinesReady = false;
    }

    private void ReleaseSizeResources()
    {
        _gpu.SignalAndWait();
        if (_context != 0) { NativeReconstruction.Destroy(_context); _context = 0; }
        if (_nrdContext != 0) { NativeNrd.Destroy(_nrdContext); _nrdContext = 0; }
        _raw?.Dispose(); _normals?.Dispose(); _surfaces?.Dispose(); _nrdNormals?.Dispose();
        _nrdNormals = null;
        _raw = _normals = _surfaces = null; _boundRaw = null;
        for (int i = 0; i < 2; i++)
        {
            _targetResources[i]?.Dispose(); _targetResources[i] = null; _targets[i] = null;
        }
        for (int i = _sizeResources.Count - 1; i >= 0; i--) _sizeResources[i].Dispose();
        _sizeResources.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseSizeResources();
        ReleasePipelines();
        _gpu.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        internal Vector4 Origin, Forward, Right, Up, PreviousOrigin, PreviousForward, PreviousRight, PreviousUp;
        internal Vector4 Size, Jitter, Control, Output;
    }

    private readonly record struct Camera(Vector3 Origin, Vector3 Forward, Vector3 Right, Vector3 Up)
    {
        internal static Camera Create(Float2 orbit, float distance)
        {
            Vector3 origin = new Vector3(MathF.Sin(orbit.X) * MathF.Cos(orbit.Y), MathF.Sin(orbit.Y), MathF.Cos(orbit.X) * MathF.Cos(orbit.Y)) * distance + new Vector3(0, 0.9f, 0);
            Vector3 forward = Vector3.Normalize(new Vector3(0, 0.9f, 0) - origin);
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            return new(origin, forward, right, Vector3.Cross(right, forward));
        }
    }
}
