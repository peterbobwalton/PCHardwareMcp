using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using PCHardwareMcp.Core;

namespace PCHardwareMcp.Service;

/// <summary>
/// Self-install for the elevated helper. The binary is copied to Program Files first: a LocalSystem service must
/// never run from a user-writable folder such as %APPDATA%, or any user process could swap the exe and gain SYSTEM.
/// </summary>
public static class ServiceInstaller
{
    private const string DisplayName = "PC Hardware MCP sensor service";
    private const string Description = "Reads CPU, GPU, memory, motherboard and drive sensors (LibreHardwareMonitor) for the PCHardwareMcp MCP server. Read-only; local pipe access only.";

    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PCHardwareMcp");
    private static string InstalledExe => Path.Combine(InstallDir, "PCHardwareMcp.Service.exe");
    private static string LogFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCHardwareMcp", "install.log");

    public static int Install(bool withPawnIo)
    {
        if (!IsAdmin())
        {
            Log("ERROR: --install must run as administrator.");
            return 5;
        }
        try
        {
            if (withPawnIo)
            {
                InstallPawnIo();
            }

            StopService();

            var sourceDir = AppContext.BaseDirectory.TrimEnd('\\');
            if (!string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(sourceDir, file);
                    if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var dest = Path.Combine(InstallDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    CopyWithRetry(file, dest);
                }
                Log($"Copied service files to {InstallDir}");
            }

            var binPath = $"\"{InstalledExe}\"";
            if (Exists())
            {
                Sc("config", PipeProtocol.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", DisplayName);
            }
            else
            {
                Sc("create", PipeProtocol.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", DisplayName);
            }
            Sc("description", PipeProtocol.ServiceName, Description);
            Sc("failure", PipeProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");

            using var sc = new ServiceController(PipeProtocol.ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Log("Service installed and running.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            return 1;
        }
    }

    public static int Uninstall()
    {
        if (!IsAdmin())
        {
            Log("ERROR: --uninstall must run as administrator.");
            return 5;
        }
        try
        {
            StopService();
            if (Exists())
            {
                Sc("delete", PipeProtocol.ServiceName);
            }
            // The running exe may be the installed copy (uninstall launched from Program Files): delete what we can.
            if (Directory.Exists(InstallDir))
            {
                foreach (var file in Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log($"Could not delete {file}: {ex.Message}");
                    }
                }
                try
                {
                    Directory.Delete(InstallDir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // left for the next reboot / manual cleanup
                }
            }
            Log("Service removed. The PawnIO driver (shared with other tools) was left installed.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void InstallPawnIo()
    {
        if (LocalHardwareSource.PawnIoInstalled() == true)
        {
            Log("PawnIO driver already installed.");
            return;
        }
        Log("Installing the PawnIO driver with winget...");
        try
        {
            var code = Run("winget", "install", "--id", "namazso.PawnIO", "--exact", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity");
            Log(code == 0 ? "PawnIO installed." : $"winget exited with {code}; install PawnIO manually from https://pawnio.eu");
        }
        catch (Exception ex)
        {
            Log($"winget not available ({ex.Message}); install PawnIO manually from https://pawnio.eu");
        }
    }

    private static bool Exists() =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, PipeProtocol.ServiceName, StringComparison.OrdinalIgnoreCase));

    private static void StopService()
    {
        if (!Exists())
        {
            return;
        }
        using var sc = new ServiceController(PipeProtocol.ServiceName);
        if (sc.Status is ServiceControllerStatus.Stopped)
        {
            return;
        }
        Log("Stopping the running service...");
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        // The SCM reports Stopped slightly before the process releases its exe.
        Thread.Sleep(500);
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void Sc(params string[] args)
    {
        var code = Run("sc.exe", args);
        if (code != 0)
        {
            throw new InvalidOperationException($"sc.exe {args[0]} failed with exit code {code}.");
        }
    }

    private static int Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            p.Kill(entireProcessTree: true);
            throw new System.TimeoutException($"{file} did not finish within 5 minutes.");
        }
        var output = (stdout.Result + stderr.Result).Trim();
        if (output.Length > 0)
        {
            Log($"{file} {args.FirstOrDefault()}: {output}");
        }
        return p.ExitCode;
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging is best effort
        }
    }
}
