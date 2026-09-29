using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DXRDemo.DXR;

public sealed class DxrShaderCompiler : IDisposable
{
    public byte[] DxilBytes { get; private set; } = null!;

    private static string? _dxcPath;
    private static readonly object _lock = new();

    private static string FindDxcPath()
    {
        if (_dxcPath != null)
            return _dxcPath;

        lock (_lock)
        {
            if (_dxcPath != null)
                return _dxcPath;

            var searched = new List<string>();

            // 1. Environment override
            string? env = Environment.GetEnvironmentVariable("DXR_DXC_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                return _dxcPath = env;

            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86   => "x86",
                _                  => "x64",
            };

            // 2. Installed Windows SDKs (registry root + default Program Files roots)
            var roots = new[]
            {
                ReadSdkRootFromRegistry(),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            }.Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).Distinct();

            var candidates = new List<(Version Version, string Path)>();
            foreach (string root in roots)
            {
                string binDir = Path.Combine(root, "Windows Kits", "10", "bin");
                if (!Directory.Exists(binDir))
                    continue;

                foreach (string verDir in Directory.EnumerateDirectories(binDir))
                {
                    if (!Version.TryParse(Path.GetFileName(verDir), out Version? ver))
                        continue;

                    string candidate = Path.Combine(verDir, arch, "dxc.exe");
                    searched.Add(candidate);
                    if (File.Exists(candidate))
                        candidates.Add((ver, candidate));
                }
            }

            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => b.Version.CompareTo(a.Version));
                return _dxcPath = candidates[0].Path;
            }

            // 3. Bundled fallback (Microsoft.Direct3D.DXC copied to app output)
            string bundled = Path.Combine(AppContext.BaseDirectory, "dxc.exe");
            searched.Add(bundled);
            if (File.Exists(bundled))
                return _dxcPath = bundled;

            // 4. PATH fallback (CreateProcess resolves "dxc" via PATH)
            string? pathResolved = ResolveFromPath("dxc.exe");
            if (pathResolved != null)
                return _dxcPath = pathResolved;

            throw new FileNotFoundException(
                "dxc.exe not found. Install Windows SDK, add DXC to PATH, " +
                $"or set DXR_DXC_PATH.\nSearched:\n  {string.Join("\n  ", searched)}");
        }
    }

    private static string? ReadSdkRootFromRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            return key?.GetValue("KitsRoot10") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveFromPath(string fileName)
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
                return null;

            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
        }
        return null;
    }

    public void CompileLibrary(string entryHlsl)
        => Compile(entryHlsl, "lib_6_5", null);

    /// <summary>Compiles a compute entry point for reconstruction preparation or output encoding.</summary>
    /// <param name="entryHlsl">The HLSL source path.</param>
    /// <param name="entryPoint">The compute shader entry point.</param>
    public void CompileCompute(string entryHlsl, string entryPoint)
        => Compile(entryHlsl, "cs_6_0", entryPoint);

    private void Compile(string entryHlsl, string profile, string? entryPoint)
    {
        if (!File.Exists(entryHlsl))
            throw new FileNotFoundException("HLSL not found.", entryHlsl);

        string dxcPath = FindDxcPath();

        string dir = Path.GetDirectoryName(entryHlsl) ?? string.Empty;
        string outFile = Path.Combine(Path.GetTempPath(), "dxr_" + Guid.NewGuid().ToString("N") + ".dxil");

        try
        {
            var psi = new ProcessStartInfo(dxcPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-T"); psi.ArgumentList.Add(profile);
            psi.ArgumentList.Add("-Fo"); psi.ArgumentList.Add(outFile);
            psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(dir);
            if (entryPoint != null) { psi.ArgumentList.Add("-E"); psi.ArgumentList.Add(entryPoint); }
            psi.ArgumentList.Add(entryHlsl);

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException($"Failed to start {dxcPath}. Is DXC installed?");

            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"dxc.exe failed (exit {proc.ExitCode}):\n{stderr}");

            DxilBytes = File.ReadAllBytes(outFile);
        }
        finally
        {
            if (File.Exists(outFile))
                File.Delete(outFile);
        }
    }

    public void Dispose() { }
}
