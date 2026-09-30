using ComputeSharp;

namespace DXRDemo.Scene;

/// <summary>Owns noisy diffuse/specular lobes, albedo, and unfiltered emission/direct lighting for NRD composition.</summary>
internal sealed class PbrSignals : IDisposable
{
    internal readonly ReadWriteTexture2D<Float4> Diffuse, Specular, Albedo, Unfiltered, SpecularGuide;
    internal readonly ReadWriteTexture2D<Float4> DiffuseSh, SpecularSh, GlassSurface, DiffuseFactor, SpecularFactor;
    internal PbrSignals(GraphicsDevice device, int width, int height)
    {
        Diffuse = device.AllocateReadWriteTexture2D<Float4>(width, height);
        Specular = device.AllocateReadWriteTexture2D<Float4>(width, height);
        Albedo = device.AllocateReadWriteTexture2D<Float4>(width, height);
        Unfiltered = device.AllocateReadWriteTexture2D<Float4>(width, height);
        SpecularGuide = device.AllocateReadWriteTexture2D<Float4>(width, height);
        DiffuseSh = device.AllocateReadWriteTexture2D<Float4>(width, height);
        SpecularSh = device.AllocateReadWriteTexture2D<Float4>(width, height);
        GlassSurface = device.AllocateReadWriteTexture2D<Float4>(width, height);
        DiffuseFactor = device.AllocateReadWriteTexture2D<Float4>(width, height);
        SpecularFactor = device.AllocateReadWriteTexture2D<Float4>(width, height);
    }
    public void Dispose()
    {
        Diffuse.Dispose(); Specular.Dispose(); Albedo.Dispose(); Unfiltered.Dispose(); SpecularGuide.Dispose();
        DiffuseSh.Dispose(); SpecularSh.Dispose(); GlassSurface.Dispose(); DiffuseFactor.Dispose(); SpecularFactor.Dispose();
    }
}
