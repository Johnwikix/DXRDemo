// Optional OptiX AOV denoising through CUDA driver / D3D12 external resources.
// No CUDA runtime DLL, CPU image transfers, or feedback of denoised radiance.
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <string>
#include <stdexcept>
#include <mutex>
#include <memory>
#include <cstring>
#include <algorithm>
using Microsoft::WRL::ComPtr;
static thread_local std::string optixMessage;
#define EXPORT extern "C" __declspec(dllexport)

#if DXR_WITH_OPTIX
#include <cuda.h>
#include <optix.h>
#include <optix_function_table_definition.h>
#include <optix_stubs.h>

namespace {
struct Driver {
    HMODULE dll = nullptr;
#define CUDA_ENTRY(name) decltype(&name) name = nullptr
    CUDA_ENTRY(cuInit); CUDA_ENTRY(cuDeviceGetCount); CUDA_ENTRY(cuDeviceGet);
    CUDA_ENTRY(cuDeviceGetLuid); CUDA_ENTRY(cuCtxCreate); CUDA_ENTRY(cuCtxDestroy);
    CUDA_ENTRY(cuCtxPushCurrent); CUDA_ENTRY(cuCtxPopCurrent);
    CUDA_ENTRY(cuStreamCreate); CUDA_ENTRY(cuStreamDestroy); CUDA_ENTRY(cuStreamSynchronize);
    CUDA_ENTRY(cuMemAlloc); CUDA_ENTRY(cuMemFree); CUDA_ENTRY(cuGetErrorString);
    CUDA_ENTRY(cuImportExternalMemory); CUDA_ENTRY(cuExternalMemoryGetMappedBuffer); CUDA_ENTRY(cuDestroyExternalMemory);
    CUDA_ENTRY(cuImportExternalSemaphore); CUDA_ENTRY(cuDestroyExternalSemaphore);
    CUDA_ENTRY(cuWaitExternalSemaphoresAsync); CUDA_ENTRY(cuSignalExternalSemaphoresAsync);
    CUDA_ENTRY(cuEventCreate); CUDA_ENTRY(cuEventDestroy); CUDA_ENTRY(cuEventRecord); CUDA_ENTRY(cuEventElapsedTime);
#undef CUDA_ENTRY
    Driver() {
        dll = LoadLibraryExW(L"nvcuda.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!dll) throw std::runtime_error("NVIDIA CUDA driver not found");
#define LOAD(name, symbol) name = reinterpret_cast<decltype(name)>(GetProcAddress(dll, symbol)); if (!name) throw std::runtime_error("CUDA entry missing: " symbol)
        LOAD(cuInit,"cuInit"); LOAD(cuDeviceGetCount,"cuDeviceGetCount"); LOAD(cuDeviceGet,"cuDeviceGet");
        LOAD(cuDeviceGetLuid,"cuDeviceGetLuid"); LOAD(cuCtxCreate,"cuCtxCreate_v2"); LOAD(cuCtxDestroy,"cuCtxDestroy_v2");
        LOAD(cuCtxPushCurrent,"cuCtxPushCurrent_v2"); LOAD(cuCtxPopCurrent,"cuCtxPopCurrent_v2");
        LOAD(cuStreamCreate,"cuStreamCreate"); LOAD(cuStreamDestroy,"cuStreamDestroy_v2"); LOAD(cuStreamSynchronize,"cuStreamSynchronize");
        LOAD(cuMemAlloc,"cuMemAlloc_v2"); LOAD(cuMemFree,"cuMemFree_v2"); LOAD(cuGetErrorString,"cuGetErrorString");
        LOAD(cuImportExternalMemory,"cuImportExternalMemory"); LOAD(cuExternalMemoryGetMappedBuffer,"cuExternalMemoryGetMappedBuffer");
        LOAD(cuDestroyExternalMemory,"cuDestroyExternalMemory"); LOAD(cuImportExternalSemaphore,"cuImportExternalSemaphore");
        LOAD(cuDestroyExternalSemaphore,"cuDestroyExternalSemaphore"); LOAD(cuWaitExternalSemaphoresAsync,"cuWaitExternalSemaphoresAsync");
        LOAD(cuSignalExternalSemaphoresAsync,"cuSignalExternalSemaphoresAsync");
        LOAD(cuEventCreate,"cuEventCreate"); LOAD(cuEventDestroy,"cuEventDestroy_v2"); LOAD(cuEventRecord,"cuEventRecord");
        LOAD(cuEventElapsedTime,"cuEventElapsedTime");
#undef LOAD
        if (cuInit(0) != CUDA_SUCCESS) throw std::runtime_error("CUDA driver initialization failed");
        OptixResult result = optixInit();
        if (result != OPTIX_SUCCESS) throw std::runtime_error("OptiX 9.1 driver initialization failed: " + std::to_string(result));
    }
};
Driver& Api() { static Driver api; return api; }
void Cuda(CUresult result) {
    if (result == CUDA_SUCCESS) return;
    const char* message = nullptr; Api().cuGetErrorString(result, &message);
    throw std::runtime_error(std::string("CUDA: ") + (message ? message : std::to_string(result)));
}
void Optix(OptixResult result) {
    if (result != OPTIX_SUCCESS) throw std::runtime_error(std::string("OptiX: ") + optixGetErrorName(result));
}
void Hr(HRESULT hr) { if (FAILED(hr)) throw std::runtime_error("OptiX D3D12 HRESULT " + std::to_string(static_cast<unsigned long>(hr))); }
CUdevice Device(ID3D12Device* device) {
    wchar_t disabled[2];
    if (GetEnvironmentVariableW(L"DXR_DISABLE_OPTIX", disabled, 2)) throw std::runtime_error("OptiX disabled by DXR_DISABLE_OPTIX");
    auto& api = Api(); int count = 0; Cuda(api.cuDeviceGetCount(&count));
    LUID target = device->GetAdapterLuid();
    for (int i = 0; i < count; ++i) {
        CUdevice candidate; Cuda(api.cuDeviceGet(&candidate, i)); char luid[8]; unsigned mask;
        if (api.cuDeviceGetLuid(luid, &mask, candidate) == CUDA_SUCCESS && std::memcmp(&target, luid, 8) == 0) return candidate;
    }
    throw std::runtime_error("The D3D12 adapter has no matching NVIDIA CUDA device");
}
struct Scope {
    Scope(CUcontext context) { Cuda(Api().cuCtxPushCurrent(context)); }
    ~Scope() { CUcontext previous; Api().cuCtxPopCurrent(&previous); }
};
const char* shader = R"(
cbuffer Frame : register(b0) {
    uint Width, Height; float InvCount; uint Pbr;
    float4 Right, Up, Forward;
};
Texture2D<float4> Color : register(t0);
Texture2D<float4> Normal : register(t1);
Texture2D<float4> Albedo : register(t2);
Texture2D<float4> Specular : register(t3);
RWStructuredBuffer<float4> Shared : register(u0);
RWTexture2D<float4> Output : register(u1);
[numthreads(8,8,1)] void Pack(uint3 id : SV_DispatchThreadID) {
    if (id.x >= Width || id.y >= Height) return;
    uint index = id.y * Width + id.x, count = Width * Height;
    float3 color = Color[id.xy].rgb;
    Shared[index] = float4(all(isfinite(color)) ? max(color,0) : 0,1);
    float3 n = Normal[id.xy].xyz;
    if (Pbr == 0) n = n * 2 - 1;
    n = dot(n,n)>1e-10 ? normalize(n) : float3(0,0,1);
    n = float3(dot(n,Right.xyz), dot(n,Up.xyz), -dot(n,Forward.xyz));
    // The tracer supplies unlit reflectance for the visible (possibly reflected) surface.
    float3 a = Pbr != 0 ? saturate(Albedo[id.xy].rgb) : 1;
    float3 oldA = InvCount == 1 ? a : Shared[index+count].rgb;
    float3 oldN = InvCount == 1 ? n : Shared[index+count*2].rgb;
    Shared[index+count] = float4(lerp(oldA,a,InvCount),1);
    Shared[index+count*2] = float4(lerp(oldN,n,InvCount),1);
}
[numthreads(8,8,1)] void Unpack(uint3 id : SV_DispatchThreadID) {
    if (id.x >= Width || id.y >= Height) return;
    float3 color = Shared[id.y*Width+id.x+Width*Height*3].rgb;
    Output[id.xy] = float4(all(isfinite(color)) ? clamp(color,0,65000) : 0,1);
})";

struct Context {
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocators[2];
    ComPtr<ID3D12GraphicsCommandList> commands;
    ComPtr<ID3D12Fence> fence;
    ComPtr<ID3D12Resource> shared;
    ComPtr<ID3D12DescriptorHeap> heap;
    ComPtr<ID3D12RootSignature> root;
    ComPtr<ID3D12PipelineState> pack, unpack;
    ComPtr<ID3D12QueryHeap> timestamps;
    ComPtr<ID3D12Resource> timing;
    HANDLE event = nullptr;
    CUcontext cuda = nullptr;
    CUstream stream = nullptr;
    CUexternalMemory memory = nullptr;
    CUexternalSemaphore semaphore = nullptr;
    CUdeviceptr pixels = 0, state = 0, scratch = 0;
    CUevent start = nullptr, stop = nullptr;
    OptixDeviceContext optix = nullptr;
    OptixDenoiser denoiser = nullptr;
    size_t stateBytes = 0, scratchBytes = 0;
    unsigned width = 0, height = 0, samples = 0, dispatches = 0, stride = 0;
    UINT64 value = 0;
    float milliseconds = 0;
    float totalMilliseconds = 0;
    UINT64 frequency = 0;
    ~Context() {
        if (!cuda) return;
        auto& api = Api();
        if (api.cuCtxPushCurrent(cuda) != CUDA_SUCCESS) { if (event) CloseHandle(event); return; }
        if (stream) api.cuStreamSynchronize(stream);
        if (denoiser) optixDenoiserDestroy(denoiser);
        if (optix) optixDeviceContextDestroy(optix);
        if (state) api.cuMemFree(state);
        if (scratch) api.cuMemFree(scratch);
        if (pixels) api.cuMemFree(pixels);
        if (memory) api.cuDestroyExternalMemory(memory);
        if (semaphore) api.cuDestroyExternalSemaphore(semaphore);
        if (start) api.cuEventDestroy(start);
        if (stop) api.cuEventDestroy(stop);
        if (stream) api.cuStreamDestroy(stream);
        CUcontext previous; api.cuCtxPopCurrent(&previous);
        if (event) CloseHandle(event);
    }
    void Initialize(ID3D12Device* d3d, unsigned w, unsigned h) {
        device = d3d; width = w; height = h;
        if (!width || !height || width > 8192 || height > 8192) throw std::runtime_error("Invalid OptiX image dimensions");
        auto& api = Api(); Cuda(api.cuCtxCreate(&cuda, 0, Device(d3d))); CUcontext previous; Cuda(api.cuCtxPopCurrent(&previous));
        Scope scope(cuda); Cuda(api.cuStreamCreate(&stream, CU_STREAM_NON_BLOCKING));
        OptixDeviceContextOptions options = {}; Optix(optixDeviceContextCreate(cuda, &options, &optix));
        OptixDenoiserOptions guides = {}; guides.guideAlbedo = guides.guideNormal = 1;
        guides.denoiseAlpha = OPTIX_DENOISER_ALPHA_MODE_COPY;
        Optix(optixDenoiserCreate(optix, OPTIX_DENOISER_MODEL_KIND_AOV, &guides, &denoiser));
        OptixDenoiserSizes sizes = {}; Optix(optixDenoiserComputeMemoryResources(denoiser,width,height,&sizes));
        stateBytes = sizes.stateSizeInBytes; scratchBytes = sizes.withoutOverlapScratchSizeInBytes;
        Cuda(api.cuMemAlloc(&state,stateBytes)); Cuda(api.cuMemAlloc(&scratch,scratchBytes));
        Optix(optixDenoiserSetup(denoiser,stream,width,height,state,stateBytes,scratch,scratchBytes));
        Cuda(api.cuEventCreate(&start,0)); Cuda(api.cuEventCreate(&stop,0));
        D3D12_COMMAND_QUEUE_DESC q = {}; q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Hr(device->CreateCommandQueue(&q,IID_PPV_ARGS(&queue)));
        for (auto& allocator : allocators) Hr(device->CreateCommandAllocator(q.Type,IID_PPV_ARGS(&allocator)));
        Hr(queue->GetTimestampFrequency(&frequency));
        D3D12_QUERY_HEAP_DESC query = {}; query.Type=D3D12_QUERY_HEAP_TYPE_TIMESTAMP; query.Count=2;
        Hr(device->CreateQueryHeap(&query,IID_PPV_ARGS(&timestamps)));
        D3D12_HEAP_PROPERTIES readbackHeap = {}; readbackHeap.Type=D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC timingBuffer = {}; timingBuffer.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER;
        timingBuffer.Width=16; timingBuffer.Height=1; timingBuffer.DepthOrArraySize=1; timingBuffer.MipLevels=1;
        timingBuffer.SampleDesc.Count=1; timingBuffer.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Hr(device->CreateCommittedResource(&readbackHeap,D3D12_HEAP_FLAG_NONE,&timingBuffer,D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&timing)));
        Hr(device->CreateCommandList(0,q.Type,allocators[0].Get(),nullptr,IID_PPV_ARGS(&commands))); Hr(commands->Close());
        Hr(device->CreateFence(0,D3D12_FENCE_FLAG_SHARED,IID_PPV_ARGS(&fence)));
        event = CreateEventW(nullptr,FALSE,FALSE,nullptr); if (!event) throw std::runtime_error("CreateEvent failed");
        HANDLE handle = nullptr; Hr(device->CreateSharedHandle(fence.Get(),nullptr,GENERIC_ALL,nullptr,&handle));
        CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC semaphoreDesc = {};
        semaphoreDesc.type = CU_EXTERNAL_SEMAPHORE_HANDLE_TYPE_D3D12_FENCE; semaphoreDesc.handle.win32.handle = handle;
        CUresult imported = api.cuImportExternalSemaphore(&semaphore,&semaphoreDesc); CloseHandle(handle); Cuda(imported);
        D3D12_RESOURCE_DESC buffer = {}; buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        buffer.Width = UINT64(width)*height*16*4; buffer.Height=1; buffer.DepthOrArraySize=1; buffer.MipLevels=1;
        buffer.SampleDesc.Count=1; buffer.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; buffer.Flags=D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        D3D12_HEAP_PROPERTIES properties = {}; properties.Type=D3D12_HEAP_TYPE_DEFAULT;
        Hr(device->CreateCommittedResource(&properties,D3D12_HEAP_FLAG_SHARED,&buffer,D3D12_RESOURCE_STATE_COMMON,nullptr,IID_PPV_ARGS(&shared)));
        auto allocation = device->GetResourceAllocationInfo(0,1,&buffer);
        Hr(device->CreateSharedHandle(shared.Get(),nullptr,GENERIC_ALL,nullptr,&handle));
        CUDA_EXTERNAL_MEMORY_HANDLE_DESC memoryDesc = {}; memoryDesc.type=CU_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE;
        memoryDesc.handle.win32.handle=handle; memoryDesc.size=allocation.SizeInBytes; memoryDesc.flags=CUDA_EXTERNAL_MEMORY_DEDICATED;
        imported=api.cuImportExternalMemory(&memory,&memoryDesc); CloseHandle(handle); Cuda(imported);
        CUDA_EXTERNAL_MEMORY_BUFFER_DESC mapping = {}; mapping.size=buffer.Width;
        Cuda(api.cuExternalMemoryGetMappedBuffer(&pixels,memory,&mapping));
        D3D12_DESCRIPTOR_HEAP_DESC hd = {}; hd.Type=D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors=6; hd.Flags=D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        Hr(device->CreateDescriptorHeap(&hd,IID_PPV_ARGS(&heap))); stride=device->GetDescriptorHandleIncrementSize(hd.Type);
        D3D12_DESCRIPTOR_RANGE ranges[2] = {};
        ranges[0].RangeType=D3D12_DESCRIPTOR_RANGE_TYPE_SRV; ranges[0].NumDescriptors=4;
        ranges[1].RangeType=D3D12_DESCRIPTOR_RANGE_TYPE_UAV; ranges[1].NumDescriptors=2;
        D3D12_ROOT_PARAMETER parameters[3] = {};
        parameters[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; parameters[0].Constants.Num32BitValues=16;
        for(unsigned i=0;i<2;i++) { parameters[i+1].ParameterType=D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; parameters[i+1].DescriptorTable.NumDescriptorRanges=1; parameters[i+1].DescriptorTable.pDescriptorRanges=&ranges[i]; }
        D3D12_ROOT_SIGNATURE_DESC rd = {}; rd.NumParameters=3; rd.pParameters=parameters;
        ComPtr<ID3DBlob> bytes, errors; Hr(D3D12SerializeRootSignature(&rd,D3D_ROOT_SIGNATURE_VERSION_1,&bytes,&errors));
        Hr(device->CreateRootSignature(0,bytes->GetBufferPointer(),bytes->GetBufferSize(),IID_PPV_ARGS(&root)));
        for(unsigned i=0;i<2;i++) {
            HRESULT compiled=D3DCompile(shader,std::strlen(shader),"OptixInterop",nullptr,nullptr,i==0?"Pack":"Unpack","cs_5_1",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&bytes,&errors);
            if (FAILED(compiled)) throw std::runtime_error(errors ? static_cast<const char*>(errors->GetBufferPointer()) : "OptiX interop shader failed");
            D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {}; pd.pRootSignature=root.Get(); pd.CS={bytes->GetBufferPointer(),bytes->GetBufferSize()};
            Hr(device->CreateComputePipelineState(&pd,IID_PPV_ARGS(i==0?&pack:&unpack)));
        }
        Cuda(api.cuStreamSynchronize(stream));
    }
    D3D12_CPU_DESCRIPTOR_HANDLE Cpu(unsigned i) { auto h=heap->GetCPUDescriptorHandleForHeapStart(); h.ptr+=SIZE_T(i)*stride; return h; }
    void Barrier(ID3D12Resource* resource,D3D12_RESOURCE_STATES before,D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER b = {}; b.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition={resource,D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,before,after}; commands->ResourceBarrier(1,&b);
    }
    OptixImage2D Image(unsigned plane) {
        OptixImage2D image = {}; image.data=pixels+UINT64(plane)*width*height*16; image.width=width; image.height=height;
        image.rowStrideInBytes=width*16; image.pixelStrideInBytes=16; image.format=OPTIX_PIXEL_FORMAT_FLOAT4; return image;
    }
    void Execute(ID3D12Resource* color,ID3D12Resource* normal,ID3D12Resource* albedo,ID3D12Resource* specular,
        ID3D12Resource* output,const float* camera,unsigned pbr,unsigned reset) {
        Scope scope(cuda); auto& api=Api();
        ID3D12Resource* inputs[]={color,normal,albedo,specular};
        D3D12_SHADER_RESOURCE_VIEW_DESC srv = {}; srv.Format=DXGI_FORMAT_R32G32B32A32_FLOAT; srv.ViewDimension=D3D12_SRV_DIMENSION_TEXTURE2D;
        srv.Shader4ComponentMapping=D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; srv.Texture2D.MipLevels=1;
        for(unsigned i=0;i<4;i++) { srv.Format=inputs[i] ? inputs[i]->GetDesc().Format : DXGI_FORMAT_R32G32B32A32_FLOAT; device->CreateShaderResourceView(inputs[i],&srv,Cpu(i)); }
        D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {}; uav.ViewDimension=D3D12_UAV_DIMENSION_BUFFER;
        uav.Buffer.NumElements=width*height*4; uav.Buffer.StructureByteStride=16; device->CreateUnorderedAccessView(shared.Get(),nullptr,&uav,Cpu(4));
        uav={}; uav.Format=output->GetDesc().Format; uav.ViewDimension=D3D12_UAV_DIMENSION_TEXTURE2D; device->CreateUnorderedAccessView(output,nullptr,&uav,Cpu(5));
        samples = reset ? 1 : std::min(samples+1,0xffffffu);
        struct Constants { unsigned width,height; float inverse; unsigned pbr; float camera[12]; } data={width,height,1.0f/samples,pbr,{}};
        std::memcpy(data.camera,camera,sizeof(data.camera));
        auto begin=[&](ID3D12PipelineState* pipeline,unsigned phase) {
            Hr(allocators[phase]->Reset()); Hr(commands->Reset(allocators[phase].Get(),pipeline)); ID3D12DescriptorHeap* heaps[]={heap.Get()}; commands->SetDescriptorHeaps(1,heaps);
            commands->SetComputeRootSignature(root.Get()); commands->SetComputeRoot32BitConstants(0,16,&data,0);
            auto gpu=heap->GetGPUDescriptorHandleForHeapStart(); commands->SetComputeRootDescriptorTable(1,gpu); gpu.ptr+=UINT64(stride)*4; commands->SetComputeRootDescriptorTable(2,gpu);
        };
        begin(pack.Get(),0); Barrier(shared.Get(),D3D12_RESOURCE_STATE_COMMON,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        commands->EndQuery(timestamps.Get(),D3D12_QUERY_TYPE_TIMESTAMP,0);
        commands->Dispatch((width+7)/8,(height+7)/8,1); Barrier(shared.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_COMMON);
        Hr(commands->Close()); ID3D12CommandList* lists[]={commands.Get()}; queue->ExecuteCommandLists(1,lists); Hr(queue->Signal(fence.Get(),++value));
        CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS wait = {}; wait.params.fence.value=value; Cuda(api.cuWaitExternalSemaphoresAsync(&semaphore,&wait,1,stream));
        OptixDenoiserGuideLayer guides = {}; guides.albedo=Image(1); guides.normal=Image(2);
        OptixDenoiserLayer layer = {}; layer.input=Image(0); layer.output=Image(3); layer.type=OPTIX_DENOISER_AOV_TYPE_BEAUTY;
        OptixDenoiserParams parameters = {};
        Cuda(api.cuEventRecord(start,stream));
        Optix(optixDenoiserInvoke(denoiser,stream,&parameters,state,stateBytes,&guides,&layer,1,0,0,scratch,scratchBytes));
        Cuda(api.cuEventRecord(stop,stream));
        CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS signal = {}; signal.params.fence.value=++value; Cuda(api.cuSignalExternalSemaphoresAsync(&semaphore,&signal,1,stream));
        // A second allocator permits recording unpack before pack finishes. GPU queues
        // exchange ownership through the shared fence; the CPU waits only for final output.
        Hr(queue->Wait(fence.Get(),value));
        begin(unpack.Get(),1); Barrier(shared.Get(),D3D12_RESOURCE_STATE_COMMON,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        Barrier(output,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        commands->Dispatch((width+7)/8,(height+7)/8,1);
        Barrier(output,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        Barrier(shared.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_COMMON);
        commands->EndQuery(timestamps.Get(),D3D12_QUERY_TYPE_TIMESTAMP,1);
        commands->ResolveQueryData(timestamps.Get(),D3D12_QUERY_TYPE_TIMESTAMP,0,2,timing.Get(),0);
        Hr(commands->Close()); queue->ExecuteCommandLists(1,lists); Hr(queue->Signal(fence.Get(),++value));
        Hr(fence->SetEventOnCompletion(value,event)); if(WaitForSingleObject(event,30000)!=WAIT_OBJECT_0) throw std::runtime_error("OptiX output synchronization timed out");
        Cuda(api.cuEventElapsedTime(&milliseconds,start,stop)); dispatches++;
        D3D12_RANGE readRange={0,16}; void* mapped=nullptr; Hr(timing->Map(0,&readRange,&mapped));
        const auto* ticks=static_cast<const UINT64*>(mapped);
        totalMilliseconds=static_cast<float>((ticks[1]-ticks[0])*1000.0/frequency);
        D3D12_RANGE written={0,0}; timing->Unmap(0,&written);
    }
};
void Destroy(Context* c) { if (!c) return; CUcontext context=c->cuda; delete c; if(context) Api().cuCtxDestroy(context); }
}
EXPORT int OptixAvailable(ID3D12Device* device) { try { Device(device); return 1; } catch(const std::exception& e) { optixMessage=e.what(); return 0; } }
EXPORT int OptixCreate(ID3D12Device* device,unsigned width,unsigned height,void** result) {
    if (!result) return -1; *result=nullptr; Context* context=new Context();
    try { context->Initialize(device,width,height); *result=context; optixMessage.clear(); return 0; }
    catch(const std::exception& e) { optixMessage=e.what(); Destroy(context); return -1; }
}
EXPORT int OptixExecute(void* context,ID3D12Resource* color,ID3D12Resource* normal,ID3D12Resource* albedo,ID3D12Resource* specular,
    ID3D12Resource* output,const float* camera,unsigned pbr,unsigned reset) {
    try { static_cast<Context*>(context)->Execute(color,normal,albedo,specular,output,camera,pbr,reset); return 0; }
    catch(const std::exception& e) { optixMessage=e.what(); return -1; }
}
EXPORT void OptixDestroy(void* context) { Destroy(static_cast<Context*>(context)); }
EXPORT unsigned OptixDispatchCount(void* context) { return static_cast<Context*>(context)->dispatches; }
EXPORT float OptixGpuMilliseconds(void* context) { return static_cast<Context*>(context)->milliseconds; }
EXPORT float OptixTotalGpuMilliseconds(void* context) { return static_cast<Context*>(context)->totalMilliseconds; }
#else
EXPORT int OptixAvailable(ID3D12Device*) { optixMessage="OptiX support was built without CUDA development headers"; return 0; }
EXPORT int OptixCreate(ID3D12Device*,unsigned,unsigned,void** result) { *result=nullptr; return -1; }
EXPORT int OptixExecute(void*,void*,void*,void*,void*,void*,const float*,unsigned,unsigned) { return -1; }
EXPORT void OptixDestroy(void*) {}
EXPORT unsigned OptixDispatchCount(void*) { return 0; }
EXPORT float OptixGpuMilliseconds(void*) { return 0; }
EXPORT float OptixTotalGpuMilliseconds(void*) { return 0; }
#endif
EXPORT const char* OptixDiagnostic() { return optixMessage.c_str(); }
