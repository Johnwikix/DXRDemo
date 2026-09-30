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
