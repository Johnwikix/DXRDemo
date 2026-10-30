using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.Scene;

namespace DXRDemo.Rendering;

internal sealed unsafe partial class RasterBackend
{
    internal static Matrix4x4 Projection(in CameraFrame camera, float aspect)
        => ReversePerspective(camera.VerticalFov, aspect, camera.Near, camera.Far);

    private static Matrix4x4 ReversePerspective(float fov, float aspect, float near, float far)
    {
        var matrix = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
        // 不用 1-forwardZ 相减构造矩阵，避免 .001/1000 等 near/far 比例损失尾数。
        matrix.M33 = near / (far - near);
        matrix.M43 = near * far / (far - near);
        return matrix;
    }
    private static Matrix4x4 ReverseDepth(Matrix4x4 matrix)
    {
        Matrix4x4 reverse = Matrix4x4.Identity;
        reverse.M33 = -1;
        reverse.M43 = 1;
        return matrix * reverse;
    }
    private Constants BuildFrame(in CameraFrame camera, in CameraFrame previous, Vector2 jitter, Vector2 previousJitter,
        bool reset, float environment, SunLightSettings sun, float lodBias)
    {
        var asset = _lease!.Scene.Asset;
        Constants result = new()
        {
            ViewProjection = Matrix4x4.CreateLookAt(camera.Origin, camera.Origin + camera.Forward, camera.Up) * Projection(camera, _width / (float)_height),
            PreviousViewProjection = Matrix4x4.CreateLookAt(previous.Origin, previous.Origin + previous.Forward, previous.Up) * Projection(previous, _width / (float)_height),
            Origin = new(camera.Origin, 0),
            Forward = new(camera.Forward, MathF.Tan(camera.VerticalFov * .5f)),
            Right = new(camera.Right, _width / (float)_height),
            Up = new(camera.Up, 0),
            Size = new(_width, _height, jitter.X, jitter.Y),
            Temporal = new(previousJitter, reset ? 1 : 0, 0),
            SunDirection = new(sun.Direction, 0),
            SunRadiance = new(sun.Radiance, 0),
            Limits = new(camera.Near, camera.Far, asset.Lights.Length, Math.Clamp(Vector3.Distance(asset.Minimum, asset.Maximum), 1, 200)),
            ShadowIds = new(-1, -1, -1, -1),
            PreviousOrigin = new(previous.Origin, 0),
            Environment = new(environment, _hardware != null ? 1 : 0, 0, 0),
            EnvironmentMap = asset.EnvironmentMap is { } map ? new(map.Width, map.Height, map.Rotation, lodBias) : new(0, 0, 0, lodBias)
        };
        int locals = 0;
        for (int i = 0; i < asset.Lights.Length; i++)
        {
            var light = asset.Lights[i];
            if (light.PositionType.W == 0)
            {
                if (!sun.Enabled && result.ShadowIds.Z < 0)
                    result.ShadowIds.Z = i;
            }
            else if (locals < 2)
            {
                if (locals == 0)
                    result.ShadowIds.X = i;
                else
                    result.ShadowIds.Y = i;
                locals++;
            }
        }
        ShadowedLocalLights = locals;
        _shadowViewMask = sun.Enabled || result.ShadowIds.Z >= 0 ? 15u : 0;
        Vector3 direction = result.ShadowIds.Z >= 0 ? -new Vector3(asset.Lights[(int)result.ShadowIds.Z].DirectionRange.X, asset.Lights[(int)result.ShadowIds.Z].DirectionRange.Y, asset.Lights[(int)result.ShadowIds.Z].DirectionRange.Z) : sun.Direction;
        float sceneRadius = MathF.Max(Vector3.Distance(asset.Minimum, asset.Maximum), .1f);
        float shadowFar = MathF.Min(camera.Far, MathF.Max(camera.Near * 2, sceneRadius * 2));
        float start = camera.Near;
        for (int cascade = 0; cascade < 4; cascade++)
        {
            float fraction = (cascade + 1) / 4f;
            float end = .7f * camera.Near * MathF.Pow(shadowFar / camera.Near, fraction) + .3f * (camera.Near + (shadowFar - camera.Near) * fraction);
            (&result.Splits.X)[cascade] = end;
            float mid = (start + end) * .5f;
            Vector3 center = camera.Origin + camera.Forward * mid;
            float farY = end * MathF.Tan(camera.VerticalFov * .5f), farX = farY * _width / _height;
            float radius = MathF.Ceiling(MathF.Sqrt(farY * farY + farX * farX + (end - mid) * (end - mid)) * 16) / 16;
            radius = MathF.Max(radius, .01f);
            Vector3 up = MathF.Abs(direction.Y) > .99f ? Vector3.UnitZ : Vector3.UnitY;
            var view = Matrix4x4.CreateLookAt(center + direction * (sceneRadius + shadowFar + radius + 1), center, up);
            float step = 2 * radius / ShadowSize;
            view.M41 = MathF.Round(view.M41 / step) * step;
            view.M42 = MathF.Round(view.M42 / step) * step;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3((corner & 1) == 0 ? asset.Minimum.X : asset.Maximum.X, (corner & 2) == 0 ? asset.Minimum.Y : asset.Maximum.Y, (corner & 4) == 0 ? asset.Minimum.Z : asset.Maximum.Z);
                float z = Vector3.Transform(point, view).Z;
                minZ = MathF.Min(minZ, z);
                maxZ = MathF.Max(maxZ, z);
            }
            float near = MathF.Max(.001f, -maxZ - sceneRadius * .05f), far = MathF.Max(near + .01f, -minZ + sceneRadius * .05f);
            var projection = ReverseDepth(Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, near, far));
            WriteShadow(ref result, cascade, view * projection);
            start = end;
        }
        for (int slot = 0; slot < locals; slot++)
        {
            var light = asset.Lights[(int)(slot == 0 ? result.ShadowIds.X : result.ShadowIds.Y)];
            _shadowViewMask |= (light.PositionType.W == 2 ? 1u : 63u) << (4 + slot * 6);
            Vector3 origin = new(light.PositionType.X, light.PositionType.Y, light.PositionType.Z);
            float range = light.DirectionRange.W > 0 ? light.DirectionRange.W : sceneRadius * 2;
            float near = MathF.Min(MathF.Max(range * .0001f, .001f), range * .1f);
            for (int face = 0; face < 6; face++)
            {
                Vector3 forward = face switch
                {
                    0 => Vector3.UnitX,
                    1 => -Vector3.UnitX,
                    2 => Vector3.UnitY,
                    3 => -Vector3.UnitY,
                    4 => Vector3.UnitZ,
                    _ => -Vector3.UnitZ
                };
                Vector3 up = face == 2 ? -Vector3.UnitZ : face == 3 ? Vector3.UnitZ : Vector3.UnitY;
                float fov = MathF.PI * .5f;
                if (light.PositionType.W == 2)
                {
                    forward = new(light.DirectionRange.X, light.DirectionRange.Y, light.DirectionRange.Z);
                    up = MathF.Abs(forward.Y) > .99f ? Vector3.UnitZ : Vector3.UnitY;
                    fov = Math.Clamp(2 * MathF.Acos(Math.Clamp(light.Spot.Y, -1, 1)), .02f, MathF.PI - .01f);
                }
                var view = Matrix4x4.CreateLookAt(origin, origin + forward, up);
                var projection = ReversePerspective(fov, 1, near, range);
                WriteShadow(ref result, 4 + slot * 6 + face, view * projection);
            }
        }
        return result;
    }
    private static void WriteShadow(ref Constants constants, int index, Matrix4x4 matrix)
    {
        fixed (float* destination = constants.ShadowViews)
            *(Matrix4x4*)(destination + index * 16) = matrix;
    }
}
