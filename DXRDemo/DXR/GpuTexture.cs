using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.DXR;

public readonly record struct RenderSize(int Width, int Height);

[StructLayout(LayoutKind.Sequential)]
public struct Rgba32
{
    public byte R, G, B, A;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rgba64
{
    public ushort R, G, B, A;
}

public abstract class GpuTexture : IDisposable
{
    private bool _disposed;

    internal GpuTexture(DxrDevice device, ID3D12Resource resource, int width, int height, Format format)
    {
        Device = device;
        Resource = resource;
        Width = width;
        Height = height;
        Format = format;
    }

    internal DxrDevice Device { get; }
    public ID3D12Resource Resource { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Resource.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class GpuTexture<T> : GpuTexture where T : unmanaged
{
    internal GpuTexture(DxrDevice device, ID3D12Resource resource, int width, int height, Format format)
        : base(device, resource, width, height, format)
    {
    }

    public void CopyFrom(T[,] source)
    {
        if (source.GetLength(0) != Height || source.GetLength(1) != Width)
            throw new ArgumentException("Source dimensions do not match the texture.", nameof(source));
        Device.UploadTexture2D(this, MemoryMarshal.CreateReadOnlySpan(ref source[0, 0], source.Length));
    }

    public T[,] ToArray() => Device.ReadTexture2D<T>(this);

    internal void CopyFrom(ReadOnlySpan<T> source)
    {
        if (source.Length != Width * Height)
            throw new ArgumentException("Source element count does not match the texture.", nameof(source));
        Device.UploadTexture2D(this, source);
    }
}

public sealed partial class DxrDevice
{
    public GpuTexture<T> CreateTexture2D<T>(int width, int height) where T : unmanaged
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Format format = typeof(T) == typeof(Vector4) ? Format.R32G32B32A32_Float
            : typeof(T) == typeof(Rgba32) ? Format.R8G8B8A8_UNorm
            : typeof(T) == typeof(Rgba64) ? Format.R16G16B16A16_UNorm
            : throw new NotSupportedException($"Unsupported texture pixel type: {typeof(T)}");
        var resource = DxrResources.CreateUnorderedAccessTexture2D(Device, (uint)width, (uint)height, format, ResourceStates.UnorderedAccess);
        return new GpuTexture<T>(this, resource, width, height, format);
    }

    public void ExecuteAndWait(Action<ID3D12GraphicsCommandList4> record)
    {
        CommandAllocator.Reset();
        CommandList.Reset(CommandAllocator);
        try
        {
            record(CommandList);
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
        }
        catch
        {
            CommandList.Close();
            throw;
        }
        SignalAndWait();
    }

    internal unsafe void UploadTexture2D<T>(GpuTexture<T> target, ReadOnlySpan<T> source) where T : unmanaged
    {
        int rowBytes = checked(target.Width * Unsafe.SizeOf<T>());
        uint pitch = (uint)((rowBytes + 255) & ~255);
        ulong size = (ulong)pitch * (uint)target.Height;
        using var upload = DxrResources.CreateUploadBuffer(Device, size, "Texture upload");
        byte* address = upload.Map<byte>(0);
        ReadOnlySpan<byte> sourceBytes = MemoryMarshal.AsBytes(source);
        for (int y = 0; y < target.Height; y++)
            sourceBytes.Slice(y * rowBytes, rowBytes).CopyTo(new Span<byte>(address + y * pitch, rowBytes));
        upload.Unmap(0);

        ExecuteAndWait(commands =>
        {
            commands.ResourceBarrierTransition(target.Resource, ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
            commands.CopyTextureRegion(new TextureCopyLocation(target.Resource, 0), 0, 0, 0,
                new TextureCopyLocation(upload, new PlacedSubresourceFootPrint
                {
                    Footprint = new SubresourceFootPrint(target.Format, (uint)target.Width, (uint)target.Height, 1, pitch)
                }));
            commands.ResourceBarrierTransition(target.Resource, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        });
    }

    internal unsafe T[,] ReadTexture2D<T>(GpuTexture texture) where T : unmanaged
    {
        int rowBytes = checked(texture.Width * Unsafe.SizeOf<T>());
        uint pitch = (uint)((rowBytes + 255) & ~255);
        using var readback = Device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
            ResourceDescription.Buffer((ulong)pitch * (uint)texture.Height), ResourceStates.CopyDest);
        ExecuteAndWait(commands =>
        {
            commands.ResourceBarrierTransition(texture.Resource, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            commands.CopyTextureRegion(new TextureCopyLocation(readback, new PlacedSubresourceFootPrint
            {
                Footprint = new SubresourceFootPrint(texture.Format, (uint)texture.Width, (uint)texture.Height, 1, pitch)
            }), 0, 0, 0, new TextureCopyLocation(texture.Resource, 0));
            commands.ResourceBarrierTransition(texture.Resource, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        });

        byte* address = readback.Map<byte>(0);
        var result = new T[texture.Height, texture.Width];
        Span<byte> resultBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result[0, 0], result.Length));
        for (int y = 0; y < texture.Height; y++)
            new ReadOnlySpan<byte>(address + y * pitch, rowBytes).CopyTo(resultBytes.Slice(y * rowBytes, rowBytes));
        readback.Unmap(0);
        return result;
    }
}
