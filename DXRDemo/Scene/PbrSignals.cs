using ComputeSharp;

namespace DXRDemo.Scene;

/// <summary>Owns noisy diffuse/specular lobes, albedo, and unfiltered emission/direct lighting for NRD composition.</summary>
internal sealed class PbrSignals : IDisposable
{
    internal readonly ReadWriteTexture2D<Float4> Diffuse, Specular, Albedo, Unfiltered, SpecularGuide;
    internal readonly ReadWriteTexture2D<Float4> DiffuseSh, SpecularSh, GlassSurface, DiffuseFactor, SpecularFactor;
    internal readonly ReadWriteTexture2D<Float4> GlassFallback, GlassNormal;
    internal readonly ReadWriteTexture2D<Float4> StableReflection, StableReflectionSurface, StableReflectionNormal;
    internal readonly ReadWriteTexture2D<Float4> StableTransmission, StableTransmissionSurface, StableTransmissionNormal;
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
        GlassFallback = device.AllocateReadWriteTexture2D<Float4>(width, height);
        GlassNormal = device.AllocateReadWriteTexture2D<Float4>(width, height);
        StableReflection = device.AllocateReadWriteTexture2D<Float4>(width, height);
        StableReflectionSurface = device.AllocateReadWriteTexture2D<Float4>(width, height);
        StableReflectionNormal = device.AllocateReadWriteTexture2D<Float4>(width, height);
        StableTransmission = device.AllocateReadWriteTexture2D<Float4>(width, height);
        StableTransmissionSurface = device.AllocateReadWriteTexture2D<Float4>(width, height);
        StableTransmissionNormal = device.AllocateReadWriteTexture2D<Float4>(width, height);
    }
    public void Dispose()
    {
        Diffuse.Dispose(); Specular.Dispose(); Albedo.Dispose(); Unfiltered.Dispose(); SpecularGuide.Dispose();
        DiffuseSh.Dispose(); SpecularSh.Dispose(); GlassSurface.Dispose(); DiffuseFactor.Dispose(); SpecularFactor.Dispose();
        GlassFallback.Dispose(); GlassNormal.Dispose();
        StableReflection.Dispose(); StableReflectionSurface.Dispose(); StableReflectionNormal.Dispose();
        StableTransmission.Dispose(); StableTransmissionSurface.Dispose(); StableTransmissionNormal.Dispose();
    }
}
