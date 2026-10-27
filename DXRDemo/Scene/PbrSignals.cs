using DXRDemo.DXR;
using Float4 = System.Numerics.Vector4;

namespace DXRDemo.Scene;

internal sealed class PbrSignals : IDisposable
{
    internal readonly GpuTexture<Float4> Diffuse, Specular, Albedo, Unfiltered, SpecularGuide;
    internal readonly GpuTexture<Float4> DiffuseSh, SpecularSh, GlassSurface, DiffuseFactor, SpecularFactor;
    internal readonly GpuTexture<Float4> GlassFallback, GlassNormal;
    internal readonly GpuTexture<Float4> StableReflection, StableReflectionSurface, StableReflectionNormal;
    internal readonly GpuTexture<Float4> StableTransmission, StableTransmissionSurface, StableTransmissionNormal;

    internal PbrSignals(DxrDevice device, int width, int height)
    {
        Diffuse = device.CreateTexture2D<Float4>(width, height);
        Specular = device.CreateTexture2D<Float4>(width, height);
        Albedo = device.CreateTexture2D<Float4>(width, height);
        Unfiltered = device.CreateTexture2D<Float4>(width, height);
        SpecularGuide = device.CreateTexture2D<Float4>(width, height);
        DiffuseSh = device.CreateTexture2D<Float4>(width, height);
        SpecularSh = device.CreateTexture2D<Float4>(width, height);
        GlassSurface = device.CreateTexture2D<Float4>(width, height);
        DiffuseFactor = device.CreateTexture2D<Float4>(width, height);
        SpecularFactor = device.CreateTexture2D<Float4>(width, height);
        GlassFallback = device.CreateTexture2D<Float4>(width, height);
        GlassNormal = device.CreateTexture2D<Float4>(width, height);
        StableReflection = device.CreateTexture2D<Float4>(width, height);
        StableReflectionSurface = device.CreateTexture2D<Float4>(width, height);
        StableReflectionNormal = device.CreateTexture2D<Float4>(width, height);
        StableTransmission = device.CreateTexture2D<Float4>(width, height);
        StableTransmissionSurface = device.CreateTexture2D<Float4>(width, height);
        StableTransmissionNormal = device.CreateTexture2D<Float4>(width, height);
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
