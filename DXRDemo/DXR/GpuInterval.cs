using Vortice.Direct3D12;

namespace DXRDemo.DXR;

/// <summary>GPU timestamps. Read only after the owning queue's existing completion fence.</summary>
internal sealed unsafe class GpuInterval : IDisposable
{
    private readonly ID3D12QueryHeap _queries;
    private readonly ID3D12Resource _readback;
    private readonly ulong _frequency;
    internal GpuInterval(DxrDevice gpu)
    {
        _queries = gpu.Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, 2));
        _readback = gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
            ResourceDescription.Buffer(16), ResourceStates.CopyDest);
        gpu.CommandQueue.GetTimestampFrequency(out _frequency).CheckError();
    }
    internal void Begin(ID3D12GraphicsCommandList command) => command.EndQuery(_queries, QueryType.Timestamp, 0);
    internal void End(ID3D12GraphicsCommandList command)
    {
        command.EndQuery(_queries, QueryType.Timestamp, 1);
        command.ResolveQueryData(_queries, QueryType.Timestamp, 0, 2, _readback, 0);
    }
    internal double ReadMilliseconds()
    {
        void* data; _readback.Map(0, null, &data).CheckError();
        ulong* ticks = (ulong*)data;
        double result = (ticks[1] - ticks[0]) * 1000.0 / _frequency;
        _readback.Unmap(0); return result;
    }
    public void Dispose() { _readback.Dispose(); _queries.Dispose(); }
}
