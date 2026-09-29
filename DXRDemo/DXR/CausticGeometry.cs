using System.Numerics;
using DXRDemo.Scene;

namespace DXRDemo.DXR;

/// <summary>Separates parallel glazing from geometry that needs the refractive caustic estimator.</summary>
internal static class CausticGeometry
{
    // This is an explicit real-time approximation: dominant parallel faces retain straight-line
    // transmission. Curved lenses, prisms and normal-mapped glass remain photon casters.
    internal static bool IsCaster(SceneMesh mesh, in SceneMaterial material, in Matrix4x4 world)
    {
        if (material.Transmission.X <= 0 || material.Transmission.Z <= 0 || material.Transmission.Y <= 1.001f) return false;
        if (material.NormalMap.Info.X >= 0) return true;
        // Area-weighted normal covariance: choosing the largest triangle would incorrectly classify
        // a densely tessellated window whose side wall happens to have the largest single triangle.
        Vector3 rowX = default, rowY = default, rowZ = default; float totalArea = 0;
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            Vector3 cross = WorldCross(mesh, i, world); float area = cross.Length();
            totalArea += area;
            if (area > 1e-20f)
            {
                Vector3 normal = cross / area;
                rowX += cross * normal.X; rowY += cross * normal.Y; rowZ += cross * normal.Z;
            }
        }
        if (totalArea <= 1e-12f) return false;
        Vector3 dominant = rowX.X >= rowY.Y && rowX.X >= rowZ.Z ? Vector3.UnitX : rowY.Y >= rowZ.Z ? Vector3.UnitY : Vector3.UnitZ;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            Vector3 next = new(Vector3.Dot(rowX, dominant), Vector3.Dot(rowY, dominant), Vector3.Dot(rowZ, dominant));
            if (next.LengthSquared() <= 1e-30f) return true;
            dominant = Vector3.Normalize(next);
        }
        float parallelArea = 0;
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            Vector3 cross = WorldCross(mesh, i, world); float area = cross.Length();
            if (MathF.Abs(Vector3.Dot(cross, dominant)) >= area * .9995f) parallelArea += area;
        }
        return parallelArea < totalArea * .95f;
    }

    private static Vector3 WorldCross(SceneMesh mesh, int triangle, in Matrix4x4 world)
    {
        Vector4 a = mesh.Vertices[mesh.Indices[triangle]].Position;
        Vector4 b = mesh.Vertices[mesh.Indices[triangle + 1]].Position;
        Vector4 c = mesh.Vertices[mesh.Indices[triangle + 2]].Position;
        Vector3 e1 = Vector3.TransformNormal(new(b.X-a.X,b.Y-a.Y,b.Z-a.Z), world);
        Vector3 e2 = Vector3.TransformNormal(new(c.X-a.X,c.Y-a.Y,c.Z-a.Z), world);
        return Vector3.Cross(e1, e2);
    }
}
