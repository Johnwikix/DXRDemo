using System;
using System.Diagnostics;
using System.IO;
using Vortice.Dxc;

namespace DXRDemo.DXR;

public sealed class DxrShaderCompiler : IDisposable
{
    public byte[] DxilBytes { get; private set; } = null!;

    public void CompileLibrary(string entryHlsl)
    {
        if (!File.Exists(entryHlsl))
            throw new FileNotFoundException("HLSL not found.", entryHlsl);

        string dir = Path.GetDirectoryName(entryHlsl) ?? string.Empty;
        string outFile = Path.Combine(Path.GetTempPath(), "dxr_" + Guid.NewGuid().ToString("N") + ".dxil");

        try
        {
            // Use dxc.exe from Windows SDK
            string dxcPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe");
            if (!File.Exists(dxcPath))
                dxcPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Windows Kits\10\bin\10.0.19041.0\x64\dxc.exe");
            if (!File.Exists(dxcPath))
                throw new FileNotFoundException("dxc.exe not found. Install Windows SDK or add DXC to PATH.");

            var psi = new ProcessStartInfo(dxcPath)
            {
                Arguments = $"\"-T\" \"lib_6_5\" \"-Fo\" \"{outFile}\" \"-I\" \"{dir}\" \"{entryHlsl}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start dxc.exe. Is DXC installed?");

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