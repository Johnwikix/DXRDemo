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
    public bool? NeuralCacheEnabled { get; set; }
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
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DXRDemo", "settings.json");

    public static AppSettings? Load() => Load(FilePath);

    // 路径重载供文件回归探针使用；不访问用户的真实设置文件。
    internal static AppSettings? Load(string filePath) => LoadFile(filePath) ?? LoadFile(filePath + ".bak");

    private static AppSettings? LoadFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize(stream, AppSettingsContext.Default.AppSettings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to load {filePath}: {ex.Message}");
            return null;
        }
    }

    public static void Save(AppSettings settings) => Save(settings, FilePath);

    internal static void Save(AppSettings settings, string filePath)
    {
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

            // .NET 10 的流式源生成序列化避免中间 JSON 字符串；文件操作仅发生在保存时。
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, AppSettingsContext.Default.AppSettings);
                stream.Flush(flushToDisk: true);
            }

            // 同目录原子替换：写入失败保留原文件，并留上一份完整设置供损坏时恢复。
            if (File.Exists(filePath))
                File.Replace(temporaryPath, filePath, filePath + ".bak");
            else
                File.Move(temporaryPath, filePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to save {filePath}: {ex.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) { Debug.WriteLine($"[Settings] Failed to remove temporary file: {ex.Message}"); }
            }
        }
    }
}
