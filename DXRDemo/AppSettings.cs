using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DXRDemo;

/// <summary>
/// User-visible renderer settings persisted across sessions.
/// Stored as JSON under %LOCALAPPDATA%\DXRDemo\settings.json (the app runs unpackaged,
/// so WinRT ApplicationData storage is not available).
/// </summary>
internal sealed class AppSettings
{
    public string? ShaderId { get; set; }
    public int? SceneIndex { get; set; }
    public int? DenoiserMode { get; set; }
    public int? ReconstructionMode { get; set; }
    public int? RenderScalePercent { get; set; }
    public double? Samples { get; set; }
    public double? MaxBounces { get; set; }
    public double? CameraSpeed { get; set; }
    public double? EnvironmentIntensity { get; set; }
    public double? Exposure { get; set; }
    public bool? SunEnabled { get; set; }
    public double? SunAzimuth { get; set; }
    public double? SunElevation { get; set; }
    public double? SunIntensity { get; set; }
    public bool? ModelLightingOnly { get; set; }
    public bool? HdrEnabled { get; set; }
    public bool? ShowDiagnostics { get; set; }
    public int? CameraMode { get; set; }
}

// Source-generated serializer: no reflection at (de)serialization time, trimming/AOT safe.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext;

internal static class AppSettingsStore
{
    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DXRDemo");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsContext.Default.AppSettings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to load {FilePath}: {ex.Message}");
            return null;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, AppSettingsContext.Default.AppSettings));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to save {FilePath}: {ex.Message}");
        }
    }
}
