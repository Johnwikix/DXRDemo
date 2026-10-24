using System.Numerics;

namespace DXRDemo.Scene;

/// <summary>Defines a distant solar disk using a Y-up world and angles in degrees.</summary>
/// <param name="Enabled">Whether the additional sun contributes light.</param>
/// <param name="Azimuth">Horizontal direction toward the sun: 0 is +Z, 90 is +X.</param>
/// <param name="Elevation">Angle above the horizon, from -90 to 90 degrees.</param>
/// <param name="Intensity">Linear irradiance on a surface perpendicular to the central direction.</param>
/// <param name="AngularDiameter">Apparent solar diameter in degrees; zero selects a delta direction.</param>
public readonly record struct SunLightSettings(bool Enabled, float Azimuth, float Elevation, float Intensity, float AngularDiameter = 0.526f)
{
    /// <summary>Gets 1-cos(angular radius), evaluated without cancellation for small solar angles.</summary>
    public float ConeHeight
    {
        get
        {
            float sine = MathF.Sin(Math.Clamp(AngularDiameter, 0, 20) * (MathF.PI / 720));
            return 2 * sine * sine;
        }
    }
    /// <summary>Gets the initial controls for imported scenes; embedded lights remain independent.</summary>
    public static SunLightSettings Default => new(false, 315, 45, 3);
    /// <summary>Gets the original packaged mesh lighting for callers that do not supply settings.</summary>
    public static SunLightSettings Legacy => new(true, 315, 54.73561f, 3);
    /// <summary>Gets the unit vector pointing from a surface toward the sun.</summary>
    public Vector3 Direction
    {
        get
        {
            float a = Azimuth * (MathF.PI / 180), e = Elevation * (MathF.PI / 180);
            return new(MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), MathF.Cos(e) * MathF.Cos(a));
        }
    }
    /// <summary>Gets linear warm-white perpendicular irradiance, or zero when the sun is disabled.</summary>
    public Vector3 Radiance => Enabled ? new Vector3(1, 2.8f / 3, 2.5f / 3) * Intensity : Vector3.Zero;
}
