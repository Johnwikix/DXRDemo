using DXRDemo;

// .NET 10 文件回归探针；所有夹具位于自己的构建输出，绝不覆盖用户配置。
string directory = Path.Combine(AppContext.BaseDirectory, "output", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "settings.json");
int checks = 0;

void Check(bool condition, ReadOnlySpan<char> message)
{
    if (!condition) throw new InvalidOperationException(message.ToString());
    checks++;
}

Check(AppSettingsStore.Load(path) is null, "Missing settings must use defaults.");
Check(AppSettingsStore.DenoiserSelection(new() { DenoiserMode = 3 }) == 2, "Legacy NRD index 3 must not become OptiX.");
Check(AppSettingsStore.DenoiserSelection(new() { DenoiserMode = 3, DenoiserCatalogVersion = 1 }) == 3, "New OptiX selection must survive restore.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 1, SceneIndex = 0 }).Index == 1, "Legacy mesh index 0 must fall back to Kitchen.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 1, SceneIndex = 1 }).Index == 1, "Legacy mesh index 1 must fall back to Kitchen.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 1, SceneIndex = 2 }).Index == 1, "Legacy mesh index 2 must fall back to Kitchen.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 1, SceneIndex = 3 }).Index == 0, "V1 Rainy Corner must map to the new catalog.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 1, SceneIndex = 4 }).Index == 1, "V1 Kitchen must retain its scene.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 1, SceneIndex = 5 }).Index == 2, "V1 Convergence must map to the new catalog.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 1, SceneIndex = 6 }).Index == 3, "V1 Bistro must map to the new catalog.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 0, SceneIndex = 3 }) is { Index: 1, UseKitchenDefaults: true }, "The original Rainy default must migrate to Kitchen defaults.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 2, SceneIndex = 3 }).Index == 3, "V2 Bistro must round-trip.");
Check(AppSettingsStore.SceneSelection(new() { SceneCatalogVersion = 2, SceneIndex = 4 }).Index is null, "V2 imported or removed indices must not persist.");
AppSettingsStore.Save(new() { DenoiserMode = 3, DenoiserCatalogVersion = 1 }, path);
Check(AppSettingsStore.Load(path) is { DenoiserMode: 3, DenoiserCatalogVersion: 1 }, "OptiX catalog version must round-trip.");
AppSettingsStore.Save(new() { SunAngularDiameter = .526 }, path);
Check(AppSettingsStore.Load(path) is { SunAngularDiameter: .526 }, "Solar angular diameter must round-trip.");

var first = new AppSettings { ShaderId = "ray-trace", SceneIndex = 2, Samples = 2 };
AppSettingsStore.Save(first, path);
Check(AppSettingsStore.Load(path) is { SceneIndex: 2, Samples: 2 }, "First save must round-trip.");

var second = new AppSettings { ShaderId = "ray-trace", SceneIndex = 3, Samples = 4 };
AppSettingsStore.Save(second, path);
Check(AppSettingsStore.Load(path) is { SceneIndex: 3, Samples: 4 }, "Replacement must load the latest complete settings.");
Check(File.Exists(path + ".bak"), "Replacement must preserve a backup.");

string complete = File.ReadAllText(path);
string backup = File.ReadAllText(path + ".bak");
AppSettingsStore.Save(new AppSettings { Samples = double.NaN }, path);
Check(File.ReadAllText(path) == complete && File.ReadAllText(path + ".bak") == backup,
    "Serialization failure must leave both complete files unchanged.");
Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Failed writes must not leave temporary files.");

File.WriteAllText(path, "{\"SceneIndex\":");
Check(AppSettingsStore.Load(path) is { SceneIndex: 2, Samples: 2 }, "Truncated JSON must recover the previous settings.");

File.WriteAllText(path, "{\"SceneIndex\":\"bad\"}");
Check(AppSettingsStore.Load(path) is { SceneIndex: 2, Samples: 2 }, "Incorrect field types must recover the previous settings.");

File.WriteAllText(path + ".bak", "{");
Check(AppSettingsStore.Load(path) is null, "Two invalid files must safely use defaults.");

File.WriteAllText(path, "{\"SceneIndex\":1,\"FutureSetting\":true}");
Check(AppSettingsStore.Load(path) is { SceneIndex: 1, Samples: null }, "Partial files and unknown fields must remain compatible.");

string unavailablePath = Path.Combine(path, "child.json");
AppSettingsStore.Save(first, unavailablePath);
Check(AppSettingsStore.Load(path) is { SceneIndex: 1 }, "An I/O failure must not corrupt existing settings or throw.");

Console.WriteLine($"PASS: {checks} persistence checks. Fixtures: {directory}");
