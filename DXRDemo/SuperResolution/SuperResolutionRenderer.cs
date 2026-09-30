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
using DXRDemo.Camera;
using DXRDemo.Scene;

namespace DXRDemo.SuperResolution;

/// <summary>Owns linear denoising, vendor reconstruction and encoding on a synchronized render-thread queue.</summary>
internal sealed unsafe class SuperResolutionRenderer : IDisposable
{
    private const int SrvCount = 20, UavCount = 10, SetCount = 6, DescriptorsPerSet = SrvCount + UavCount;
    private readonly DxrDevice _gpu = new();
    private readonly List<IDisposable> _pipelineResources = [];
    private readonly List<IDisposable> _sizeResources = [];
    private readonly uint _capabilities;
    private readonly bool _nrdAvailable;
    private string _nrdMessage = "";
    private readonly string _availabilityMessage = "";
    private ID3D12RootSignature _root = null!;
    private ID3D12PipelineState _prepare = null!, _prepareGuides = null!, _encode = null!;
    private ID3D12PipelineState _prepareNrd = null!, _composeNrd = null!;
    private ID3D12PipelineState _prepareRr = null!;
    private ID3D12DescriptorHeap _heap = null!;
    private ID3D12Resource _constants = null!;
    private nint _constantPointer, _context;
    private nint _nrdContext;
    private int _descriptorStride;
    private ID3D12Resource _color = null!, _depth = null!, _motion = null!, _reactive = null!, _output = null!;
    private ID3D12Resource _viewZ = null!, _nrdOutput = null!;
    private ID3D12Resource _nrdSpecInput = null!, _nrdSpecOutput = null!;
    private ID3D12Resource _nrdDiffShInput = null!, _nrdSpecShInput = null!, _nrdDiffShOutput = null!, _nrdSpecShOutput = null!;
    private ID3D12Resource _glassColor = null!;
    private readonly ID3D12Resource?[] _pbrInputs = new ID3D12Resource?[10];
    private ID3D12Resource _rrNormals = null!, _rrHitDistance = null!;
    private readonly ID3D12Resource[] _history = new ID3D12Resource[2];
    private ID3D12Resource? _raw, _normals, _surfaces;
    private ID3D12Resource? _nrdNormals;
    private ReadWriteTexture2D<Float4>? _boundRaw;
    private readonly ReadWriteTexture2D<Rgba64, Float4>?[] _targets = new ReadWriteTexture2D<Rgba64, Float4>?[2];
    private readonly ID3D12Resource?[] _targetResources = new ID3D12Resource?[2];
    private int _targetSlot;
    private (ReconstructionMode Mode, int Scale, int Width, int Height, bool Nrd, bool Pbr) _configuration;
    private bool _linearReady;
    private bool _glassComposed;
    private bool _configured, _disposed, _recording, _pipelinesReady, _reset = true;
    private uint _frame;
    private Float2 _previousJitter;
    private CameraFrame _previousCamera;
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
    internal bool LinearPipelineActive => _linearReady;
    internal uint NrdDispatchCount => _nrdContext == 0 ? 0 : NativeNrd.DispatchCount(_nrdContext);
    internal int InputWidth => Status.InputWidth;
    internal int InputHeight => Status.InputHeight;
    internal bool HistoryReset => _reset;
    // Exposed internally for the offscreen resource-contract probe, never used by the UI.
    internal ID3D12Resource Depth => _depth;
    internal ID3D12Resource Motion => _motion;
    internal ID3D12Resource LinearOutput => _output;
    internal ID3D12Resource DenoisedColor => _glassComposed ? _glassColor : _color;
    internal ID3D12Resource NrdViewZ => _viewZ;
    /// <summary>Gets the most recently written linear denoiser history for offscreen regression checks.</summary>
    internal ID3D12Resource DenoiserHistory => _history[(int)((_frame - 1) % 2)];

    private T Keep<T>(T value) where T : IDisposable { _pipelineResources.Add(value); return value; }
    private T Sized<T>(T value) where T : IDisposable { _sizeResources.Add(value); return value; }

    internal void Configure(ReconstructionMode mode, int scale, int outputWidth, int outputHeight, bool nrd = false, bool pbr = false)
    {
        var configuration = (mode, scale, outputWidth, outputHeight, nrd, pbr);
        if (_configured && _configuration == configuration) return;
        _configured = true;
        _configuration = configuration;
        ReleaseSizeResources();
        Reset();
        bool useNrd = nrd && _nrdAvailable;
        string message = "原生分辨率";
        if (mode == ReconstructionMode.DlssRayReconstruction && (!pbr || (_capabilities & (1u << (int)mode)) == 0))
        {
            mode = (_capabilities & (1u << (int)ReconstructionMode.Dlss)) != 0 ? ReconstructionMode.Dlss : ReconstructionMode.Off;
            message = pbr ? "DLSSD 在当前设备不可用；" : "DLSSD 需要 glTF/GLB PBR 场景；";
            message += mode == ReconstructionMode.Off ? "使用原生分辨率与常规降噪。" : "回退到 DLSS 超分与常规降噪。";
        }
        if (mode != ReconstructionMode.Off && (_capabilities & (1u << (int)mode)) == 0)
        {
            message = $"{mode} 在当前设备不可用，使用原生分辨率。" + (mode == ReconstructionMode.Fsr ? _availabilityMessage : "");
            mode = ReconstructionMode.Off;
        }
        if (mode == ReconstructionMode.DlssRayReconstruction) useNrd = false;
        if (mode == ReconstructionMode.Off && !useNrd && !pbr)
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
                if (result != 0) throw new InvalidOperationException($"{mode} 初始化失败 ({result}) " +
                    (mode == ReconstructionMode.DlssRayReconstruction ? Marshal.PtrToStringUTF8(NativeReconstruction.NgxDiagnostic()) : ""));
                End(); // NGX creation can record initialization commands.
                message = (_configuration.Mode != mode ? message + " " : "") + $"{mode}: {width} × {height} → {outputWidth} × {outputHeight}";
            }
            if (mode == ReconstructionMode.DlssRayReconstruction)
            {
                _rrNormals = Texture(width, height, Format.R16G16B16A16_Float);
                _rrHitDistance = Texture(width, height, Format.R32_Float);
            }
            if (useNrd)
            {
                int result = pbr ? NativeNrd.CreatePbr(_gpu.Device.NativePointer, (uint)width, (uint)height, out _nrdContext)
                    : NativeNrd.Create(_gpu.Device.NativePointer, (uint)width, (uint)height, out _nrdContext);
                if (result != 0)
                {
                    _nrdMessage = Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? $"NRD 初始化失败 ({result})";
                    if (mode == ReconstructionMode.Off && !pbr) { Publish(mode, width, height, message); return; }
                }
                else
                {
                    _viewZ = Texture(width, height, Format.R32_Float);
                    _nrdOutput = Texture(width, height, Format.R16G16B16A16_Float);
                    if (pbr)
                    {
                        _nrdSpecInput = Texture(width, height, Format.R16G16B16A16_Float); _nrdSpecOutput = Texture(width, height, Format.R16G16B16A16_Float);
                        _nrdDiffShInput = Texture(width, height, Format.R16G16B16A16_Float); _nrdSpecShInput = Texture(width, height, Format.R16G16B16A16_Float);
                        _nrdDiffShOutput = Texture(width, height, Format.R16G16B16A16_Float); _nrdSpecShOutput = Texture(width, height, Format.R16G16B16A16_Float);
                        _glassColor = Texture(width, height, Format.R16G16B16A16_Float);
                    }
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
            }
            _linearReady = true;
            Publish(mode, width, height, message);
        }
        catch (Exception e)
        {
            Discard(); // Discard partially recorded work; no resource state has changed on the GPU.
            ReleaseSizeResources();
            if (configuration.mode == ReconstructionMode.DlssRayReconstruction)
            {
                Configure((_capabilities & (1u << (int)ReconstructionMode.Dlss)) != 0 ? ReconstructionMode.Dlss : ReconstructionMode.Off,
                    scale, outputWidth, outputHeight, nrd, pbr);
                _configuration = configuration;
                Publish(Active, InputWidth, InputHeight, e.Message + "；光线重建已回退到常规管线。");
                return;
            }
            if (pbr && (configuration.mode != ReconstructionMode.Off || configuration.nrd))
            {
                Configure(ReconstructionMode.Off, 100, outputWidth, outputHeight, false, true);
                _configuration = configuration;
            }
            Publish(ReconstructionMode.Off, outputWidth, outputHeight, $"{e.Message}；使用原生分辨率。");
        }
    }

    private void Publish(ReconstructionMode active, int width, int height, string message)
        => Volatile.Write(ref _status, new(_capabilities, _configuration.Mode, active, width, height,
            _configuration.Width, _configuration.Height, message + (active == ReconstructionMode.DlssRayReconstruction ? " · DLSSD（已旁路常规降噪）" : _nrdContext != 0 ? " · NVIDIA NRD RELAX" :
                _configuration.Nrd ? $" · NRD 不可用，使用时间累积：{_nrdMessage}" : ""), _nrdAvailable, _nrdContext != 0));

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
                new DescriptorRange1(DescriptorRangeType.ShaderResourceView, SrvCount, 0, 0),
                 new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, UavCount, 0, 0)), ShaderVisibility.All)])));
        string file = Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", "Reconstruction.hlsl");
        _prepare = Compile(file, "Prepare");
        _prepareGuides = Compile(file, "PrepareGuides");
        _encode = Compile(file, "Encode");
        _prepareNrd = Compile(file, "PrepareNrd");
        _composeNrd = Compile(file, "ComposeNrd");
        _prepareRr = Compile(file, "PrepareRr");
        _heap = Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, DescriptorsPerSet * SetCount, DescriptorHeapFlags.ShaderVisible)));
        _descriptorStride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        // .NET 10: 常量持久映射，帧内只写入值类型，描述符与资源复用。
        _constants = Keep(DxrResources.CreateUploadBuffer(_gpu.Device, 256 * 2));
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
        for (int set = 0; set < SetCount; set++)
        {
            var start = _heap.GetCPUDescriptorHandleForHeapStart() + set * DescriptorsPerSet * _descriptorStride;
            for (int i = 0; i < SrvCount; i++) _gpu.Device.CreateShaderResourceView(null, nullSrv, start + i * _descriptorStride);
            for (int i = 0; i < UavCount; i++) _gpu.Device.CreateUnorderedAccessView(null, null, nullUav, start + (SrvCount + i) * _descriptorStride);
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
        ReadWriteTexture2D<Float4> surfaces, ReadWriteTexture2D<Float4>? nrdNormals, PbrSignals? pbr, ReadWriteTexture2D<Float4>? directLight)
    {
        if (ReferenceEquals(raw, _boundRaw)) return;
        _raw?.Dispose(); _normals?.Dispose(); _surfaces?.Dispose(); _nrdNormals?.Dispose();
        _raw = Resource(raw); _normals = Resource(normals); _surfaces = Resource(surfaces);
        _nrdNormals = nrdNormals == null ? null : Resource(nrdNormals);
        for (int i = 0; i < _pbrInputs.Length; i++) { _pbrInputs[i]?.Dispose(); _pbrInputs[i] = null; }
        if (pbr != null)
        {
            _pbrInputs[0] = Resource(pbr.Diffuse); _pbrInputs[1] = Resource(pbr.Specular); _pbrInputs[2] = Resource(pbr.Albedo); _pbrInputs[3] = Resource(pbr.Unfiltered); _pbrInputs[4] = Resource(pbr.SpecularGuide);
            _pbrInputs[5] = Resource(pbr.DiffuseSh); _pbrInputs[6] = Resource(pbr.SpecularSh); _pbrInputs[7] = Resource(pbr.GlassSurface);
            _pbrInputs[8] = Resource(pbr.DiffuseFactor); _pbrInputs[9] = Resource(pbr.SpecularFactor);
        }
        else if (directLight != null) _pbrInputs[3] = Resource(directLight);
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
        => _gpu.Device.CreateUnorderedAccessView(resource, null, null, _heap.GetCPUDescriptorHandleForHeapStart() + (set * DescriptorsPerSet + SrvCount + index) * _descriptorStride);

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
        ReadWriteTexture2D<Float4>? nrdNormals = null, CameraFrame? cameraFrame = null, PbrSignals? pbr = null, float exposure = 0,
        ReadWriteTexture2D<Float4>? directLight = null, DxrSceneBackend? transparentBackend = null)
    {
        if (seconds > 0.25 || seconds <= 0) Reset();
        _glassComposed = false;
        bool rayReconstruction = Active == ReconstructionMode.DlssRayReconstruction;
        if (rayReconstruction) denoiser = RayTraceDenoiserMode.None;
        BindInputs(raw, normals, surfaces, nrdNormals, pbr, directLight);
        ID3D12Resource encoded = BindTarget(target);
        CameraFrame camera = cameraFrame ?? CameraFrame.FromOrbit(orbit.X, orbit.Y, distance);
        if (_reset) { _previousCamera = camera; _previousJitter = jitter; }
        Constants data = new()
        {
            Origin = new(camera.Origin, 0), Forward = new(camera.Forward, 0), Right = new(camera.Right, 0), Up = new(camera.Up, 0),
            PreviousOrigin = new(_previousCamera.Origin, 0), PreviousForward = new(_previousCamera.Forward, 0),
            PreviousRight = new(_previousCamera.Right, 0), PreviousUp = new(_previousCamera.Up, 0),
            Size = new(InputWidth, InputHeight, target.Width, target.Height),
            Jitter = new(jitter.X, jitter.Y, _previousJitter.X, _previousJitter.Y),
            Control = new(_reset ? 1 : 0, (int)denoiser, Math.Min(_frame, 31), 0),
            Output = new(hdr.IsHdrEnabled ? 1 : 0, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits, exposure),
            Projection = new(MathF.Tan(camera.VerticalFov * .5f), camera.Near, camera.Far, pbr != null ? 1 : 0),
            Lighting = new(directLight != null ? 1 : 0, 0, Active != ReconstructionMode.Off ? 1 : 0, 0)
        };
        *(Constants*)_constantPointer = data;
        data.Lighting.Y = transparentBackend != null ? 1 : 0;
        *(Constants*)(_constantPointer + 256) = data;
        int historySlot = (int)(_frame % 2);
        Srv(0, 0, _raw!); Srv(0, 1, _normals!); Srv(0, 2, _surfaces!); Srv(0, 3, _history[1 - historySlot]);
        if (pbr != null)
            for (int set = 0; set < SetCount; set++) for (int i = 0; i < _pbrInputs.Length; i++) Srv(set, i < 4 ? 6 + i : i == 4 ? 11 : 8 + i, _pbrInputs[i]!);
        else if (directLight != null) { Srv(1, 9, _pbrInputs[3]!); Srv(2, 9, _pbrInputs[3]!); }
        Uav(0, 0, _color); Uav(0, 1, _depth); Uav(0, 2, _motion); Uav(0, 3, _reactive); Uav(0, 4, _history[historySlot]);
        Uav(4, 5, encoded);
        try
        {
            Begin();
            Transition(_raw!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_normals!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            Transition(_surfaces!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            if (pbr != null) foreach (var inputResource in _pbrInputs) Transition(inputResource!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            else if (directLight != null) Transition(_pbrInputs[3]!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
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
                Srv(1, 0, _raw!); Srv(1, 1, _normals!); Srv(1, 2, _surfaces!);
                Uav(1, 0, _color); Uav(1, 1, _viewZ);
                if (pbr != null)
                {
                    Uav(1, 6, _nrdSpecInput); Uav(1, 8, _nrdDiffShInput); Uav(1, 9, _nrdSpecShInput);
                    Transition(_nrdSpecInput, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Transition(_nrdDiffShInput, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Transition(_nrdSpecShInput, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                }
                Transition(_color, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                Transition(_viewZ, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                Transition(_nrdNormals, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                Bind(1, _prepareNrd, 0);
                _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
                Transition(_color, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                Transition(_viewZ, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                if (pbr != null)
                {
                    Transition(_nrdSpecInput, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    Transition(_nrdDiffShInput, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    Transition(_nrdSpecShInput, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                }
                // .NET 10: 值类型矩阵直接写入栈上的 ABI 帧，复用 NRD 资源，不在渲染循环分配数组。
                NativeNrd.Frame nrdFrame = new()
                {
                    Color = _color.NativePointer, NormalRoughness = _nrdNormals.NativePointer,
                    ViewZ = _viewZ.NativePointer, Motion = _motion.NativePointer, Output = _nrdOutput.NativePointer,
                    WorldToView = Matrix4x4.CreateLookAt(camera.Origin, camera.Origin + camera.Forward, camera.Up),
                    WorldToViewPrevious = Matrix4x4.CreateLookAt(_previousCamera.Origin, _previousCamera.Origin + _previousCamera.Forward, _previousCamera.Up),
                    ViewToClip = Matrix4x4.CreatePerspectiveFieldOfView(camera.VerticalFov, InputWidth / (float)InputHeight, camera.Near, camera.Far),
                    // NRD asks for ray sample offsets; SR SDKs below ask for projection offsets.
                    Jitter = new(jitter.X, jitter.Y), PreviousJitter = new(_previousJitter.X, _previousJitter.Y),
                    Milliseconds = (float)Math.Clamp(seconds * 1000, 1, 250), FrameIndex = _frame, Reset = _reset ? 1u : 0u,
                    Specular = pbr != null ? _nrdSpecInput.NativePointer : 0, SpecularOutput = pbr != null ? _nrdSpecOutput.NativePointer : 0,
                    DenoisingRange = camera.Far,
                    DiffuseSh = pbr != null ? _nrdDiffShInput.NativePointer : 0, SpecularSh = pbr != null ? _nrdSpecShInput.NativePointer : 0,
                    DiffuseShOutput = pbr != null ? _nrdDiffShOutput.NativePointer : 0, SpecularShOutput = pbr != null ? _nrdSpecShOutput.NativePointer : 0
                };
                if (NativeNrd.Execute(_nrdContext, _gpu.CommandList.NativePointer, &nrdFrame) != 0)
                    throw new InvalidOperationException(Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? "NRD 执行失败");
                Srv(2, 0, _raw!); Srv(2, 1, _normals!); Srv(2, 2, _surfaces!); Srv(2, 5, _nrdOutput);
                if (pbr != null)
                {
                    Srv(2, 10, _nrdSpecOutput); Srv(2, 12, _nrdNormals); Srv(2, 18, _nrdDiffShOutput); Srv(2, 19, _nrdSpecShOutput); Srv(2, 3, _viewZ);
                }
                Uav(2, 0, _color);
                Transition(_color, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                Bind(2, _composeNrd, 0);
                _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
                Transition(_color, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                if (pbr != null && transparentBackend != null)
                {
                    Transition(_pbrInputs[7]!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Transition(_glassColor, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    End(); // The glass queue reads the completed current-frame NRD result.
                    transparentBackend.ComposeTransparent(_color, _glassColor);
                    Begin();
                    Transition(_pbrInputs[7]!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    Transition(_glassColor, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    input = _glassColor;
                    _glassComposed = true;
                    Srv(5, 2, _surfaces!); Uav(5, 2, _motion); Uav(5, 3, _reactive);
                    Transition(_motion, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Transition(_reactive, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Bind(5, _prepareGuides, 1);
                    _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
                    Transition(_motion, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    Transition(_reactive, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                }
                Transition(_nrdNormals, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
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
                    Reset = _reset ? 1u : 0u, NearPlane = camera.Near, FarPlane = camera.Far, VerticalFieldOfView = camera.VerticalFov
                };
                int result;
                if (rayReconstruction)
                {
                    if (pbr == null || _nrdNormals == null) throw new InvalidOperationException("DLSSD PBR guides missing");
                    Srv(3, 12, _nrdNormals); Uav(3, 6, _rrNormals); Uav(3, 7, _rrHitDistance);
                    Transition(_nrdNormals, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    Transition(_rrNormals, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Transition(_rrHitDistance, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    Bind(3, _prepareRr, 0);
                    _gpu.CommandList.Dispatch((uint)(InputWidth + 7) / 8, (uint)(InputHeight + 7) / 8, 1);
                    Transition(_rrNormals, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    Transition(_rrHitDistance, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                    Transition(_nrdNormals, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                    // DLSSD consumes the raw noisy signal, with no temporal/custom/NRD prefilter.
                    frame.Color = _raw!.NativePointer;
                    NativeReconstruction.RayReconstructionFrame rrFrame = new()
                    {
                        Common = frame, DiffuseAlbedo = _pbrInputs[2]!.NativePointer, SpecularAlbedo = _pbrInputs[4]!.NativePointer,
                        NormalRoughness = _rrNormals.NativePointer, SpecularHitDistance = _rrHitDistance.NativePointer,
                        WorldToView = Matrix4x4.CreateLookAt(camera.Origin, camera.Origin + camera.Forward, camera.Up),
                        ViewToClip = Matrix4x4.CreatePerspectiveFieldOfView(camera.VerticalFov, InputWidth / (float)InputHeight, camera.Near, camera.Far)
                    };
                    result = NativeReconstruction.ExecuteDlssd(_context, _gpu.CommandList.NativePointer, &rrFrame);
                }
                else result = NativeReconstruction.Execute(_context, _gpu.CommandList.NativePointer, &frame);
                if (result != 0) throw new InvalidOperationException($"{Active} 执行失败 ({result}) " +
                    (rayReconstruction ? Marshal.PtrToStringUTF8(NativeReconstruction.NgxDiagnostic()) : ""));
                Transition(_output, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
                input = _output;
            }
            Srv(4, 5, input);
            Bind(4, _encode, 0); // Vendor dispatch changes root bindings and descriptor heaps.
            _gpu.CommandList.Dispatch((uint)(target.Width + 7) / 8, (uint)(target.Height + 7) / 8, 1);
            _gpu.CommandList.ResourceBarrierUnorderedAccessView(encoded);
            if (_context != 0) Transition(_output, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_raw!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_normals!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_surfaces!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            if (pbr != null) foreach (var inputResource in _pbrInputs) Transition(inputResource!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            else if (directLight != null) Transition(_pbrInputs[3]!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            End(); // Complete before ComputeSharp HUD or presentation consumes the shared target.
            _previousCamera = camera; _previousJitter = jitter; _frame++; _reset = false;
            return true;
        }
        catch (Exception e)
        {
            Discard();
            var failed = _configuration;
            ReleaseSizeResources();
            if (failed.Mode == ReconstructionMode.DlssRayReconstruction)
            {
                Configure((_capabilities & (1u << (int)ReconstructionMode.Dlss)) != 0 ? ReconstructionMode.Dlss : ReconstructionMode.Off,
                    failed.Scale, target.Width, target.Height, failed.Nrd, failed.Pbr);
                _configuration = failed;
                Publish(Active, InputWidth, InputHeight, e.Message + "；光线重建已回退到常规管线。");
                return false;
            }
            if (_configuration.Nrd) _nrdMessage = e.Message;
            if (failed.Pbr && (failed.Mode != ReconstructionMode.Off || failed.Nrd))
            {
                Configure(ReconstructionMode.Off, 100, target.Width, target.Height, false, true);
                _configuration = failed;
            }
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
        _linearReady = false;
        _glassComposed = false;
        _gpu.SignalAndWait();
        if (_context != 0) { NativeReconstruction.Destroy(_context); _context = 0; }
        if (_nrdContext != 0) { NativeNrd.Destroy(_nrdContext); _nrdContext = 0; }
        _raw?.Dispose(); _normals?.Dispose(); _surfaces?.Dispose(); _nrdNormals?.Dispose();
        _nrdNormals = null;
        _raw = _normals = _surfaces = null; _boundRaw = null;
        for (int i = 0; i < _pbrInputs.Length; i++) { _pbrInputs[i]?.Dispose(); _pbrInputs[i] = null; }
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
        internal Vector4 Projection, Lighting;
    }

}
