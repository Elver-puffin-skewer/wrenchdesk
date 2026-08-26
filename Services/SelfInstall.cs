using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

namespace WrenchDesk.Services;

/// <summary>
/// Lets the single .exe install itself, so the shop downloads one file and double-clicks it.
///
/// Run from anywhere that is not the install folder, it copies itself to the user's own
/// application folder, makes a desktop icon and a Start menu entry, and carries on from there.
/// No separate installer, no .NET runtime to fetch, no administrator prompt.
/// </summary>
public static class SelfInstall
{
    public const string AppName = "WrenchDesk";
    public const string ShortcutName = "WrenchDesk - Walt's Small Engines";

    /// <summary>Switches this class owns, stripped before the host reads the command line.</summary>
    public static readonly HashSet<string> SetupFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "install", "uninstall", "portable", "no-install", "noinstall"
    };

    /// <summary>Where an installed copy lives. Per-user, so installing never needs admin rights.</summary>
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string InstalledExePath => Path.Combine(InstallDirectory, "WrenchDesk.exe");

    /// <summary>The running program's own file. Empty for a non-single-file build.</summary>
    public static string CurrentExePath => Environment.ProcessPath ?? "";

    public static bool IsInstalled => File.Exists(InstalledExePath);

    /// <summary>True when this process is the installed copy rather than one run from Downloads.</summary>
    public static bool RunningFromInstallDirectory =>
        !string.IsNullOrEmpty(CurrentExePath) &&
        string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(CurrentExePath) ?? ""),
            Path.GetFullPath(InstallDirectory),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Handles the setup command line before the web host starts.
    /// Returns true when the caller should stop — the work is done, or another copy took over.
    /// </summary>
    public static bool HandleStartup(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var flags = new HashSet<string>(
            args.Select(a => a.TrimStart('-', '/')), StringComparer.OrdinalIgnoreCase);

        if (flags.Contains("uninstall"))
        {
            Uninstall();
            Pause();
            return true;
        }

        if (flags.Contains("install"))
        {
            var installed = Install(out var target);
            if (installed) Launch(target);
            Pause();
            return true;
        }

        // Portable mode: run right here, touch nothing.
        if (flags.Contains("portable") || flags.Contains("no-install") || flags.Contains("noinstall"))
            return false;

        if (RunningFromInstallDirectory) return false;

        // Being run from Downloads or a USB stick, and not set up yet — offer to do it.
        return OfferInstall();
    }

    private static bool OfferInstall()
    {
        Console.WriteLine();
        Console.WriteLine("  WrenchDesk is not set up on this PC yet.");
        Console.WriteLine();
        Console.WriteLine("  Setting up will:");
        Console.WriteLine("    - copy the program to your account so it stays put");
        Console.WriteLine($"      ({InstallDirectory})");
        Console.WriteLine("    - put a WrenchDesk icon on your desktop");
        Console.WriteLine("    - add it to the Start menu");
        Console.WriteLine();
        Console.WriteLine("  Nothing else on the PC is changed, and no shop data is touched.");
        Console.WriteLine();

        if (!Confirm("  Set up WrenchDesk now? [Y/n] ", defaultYes: true))
        {
            Console.WriteLine();
            Console.WriteLine("  Skipped. Running from where it is.");
            Console.WriteLine("  (Run it with --portable to skip this question in future.)");
            Console.WriteLine();
            return false;
        }

        if (!Install(out var target)) return false;

        Console.WriteLine();
        Console.WriteLine("  Starting the installed copy...");
        Console.WriteLine();

        // Hand over to the copy in the install folder so this file can be deleted freely.
        Launch(target);
        return true;
    }

    /// <summary>Copies the program into place and creates the shortcuts. Returns false on failure.</summary>
    public static bool Install(out string installedExe)
    {
        installedExe = InstalledExePath;

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("  Setting up shortcuts is only supported on Windows.");
            return false;
        }

        var source = CurrentExePath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            Console.WriteLine("  Could not work out where this program is running from, so it cannot set itself up.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(InstallDirectory);

            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
            {
                StopInstalledCopy();
                File.Copy(source, installedExe, overwrite: true);
            }

            // appsettings.json is optional — defaults live in code — but carry it if it is alongside.
            var sourceDir = Path.GetDirectoryName(source) ?? "";
            foreach (var name in new[] { "appsettings.json" })
            {
                var from = Path.Combine(sourceDir, name);
                var to = Path.Combine(InstallDirectory, name);
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            }

            Console.WriteLine($"  Copied to {InstallDirectory}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not copy the program: {ex.Message}");
            return false;
        }

        CreateShortcut(DesktopLink, installedExe, "Desktop icon");
        CreateShortcut(StartMenuLink, installedExe, "Start menu entry");

        if (Confirm("  Start WrenchDesk automatically when Windows starts? [y/N] ", defaultYes: false))
            CreateShortcut(StartupLink, installedExe, "Start with Windows");
        else if (File.Exists(StartupLink))
            TryDelete(StartupLink);

        ReportPinning();
        return true;
    }

    public static void Uninstall()
    {
        Console.WriteLine();
        Console.WriteLine("  Removing WrenchDesk from this PC.");
        Console.WriteLine();

        StopInstalledCopy();

        foreach (var link in new[] { DesktopLink, StartMenuLink, StartupLink })
        {
            if (File.Exists(link) && TryDelete(link))
                Console.WriteLine($"  Removed {Path.GetFileNameWithoutExtension(link)}");
        }

        // Deleting the folder we are running from would fail, so leave that to Windows.
        if (Directory.Exists(InstallDirectory) && !RunningFromInstallDirectory)
        {
            try
            {
                Directory.Delete(InstallDirectory, recursive: true);
                Console.WriteLine($"  Removed {InstallDirectory}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Could not remove {InstallDirectory}: {ex.Message}");
            }
        }
        else if (RunningFromInstallDirectory)
        {
            // A program cannot delete the file it is running from, so hand the last step to a
            // detached shell that waits for this process to exit and then clears the folder.
            if (ScheduleFolderRemoval(InstallDirectory))
                Console.WriteLine($"  Removed {InstallDirectory}");
            else
                Console.WriteLine($"  Shortcuts removed. Delete this folder to finish: {InstallDirectory}");
        }

        Console.WriteLine();
        Console.WriteLine("  Your shop data was left alone:");
        Console.WriteLine($"    {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppName)}");
        Console.WriteLine();
    }

    // ---- Shortcut plumbing ----

    private static string DesktopLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"{ShortcutName}.lnk");

    private static string StartMenuLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", $"{ShortcutName}.lnk");

    private static string StartupLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), $"{ShortcutName}.lnk");

    /// <summary>
    /// Writes a .lnk through the Windows scripting host. Late-bound on purpose: it needs no
    /// interop assembly, which keeps the single-file build to one file with nothing beside it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string linkPath, string target, string label)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                Console.WriteLine($"  Could not create the {label} (Windows Script Host is unavailable).");
                return;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { linkPath });
            if (shortcut is null) return;

            var t = shortcut.GetType();
            void Set(string property, object value) =>
                t.InvokeMember(property, BindingFlags.SetProperty, null, shortcut, new[] { value });

            Set("TargetPath", target);
            Set("WorkingDirectory", Path.GetDirectoryName(target)!);
            Set("Description", "Open the shop app");
            Set("IconLocation", $"{target},0");

            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            Console.WriteLine($"  {label} created.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not create the {label}: {ex.Message}");
        }
    }

    /// <summary>
    /// Windows does not let a program pin itself to the taskbar — the verb is refused to scripts
    /// and installers on Windows 10 and 11. Say so plainly rather than failing quietly.
    /// </summary>
    private static void ReportPinning()
    {
        Console.WriteLine();
        Console.WriteLine("  To put WrenchDesk on the taskbar, Windows needs you to do it:");
        Console.WriteLine("    right-click the desktop icon  ->  Show more options  ->  Pin to taskbar");
    }

    // ---- Helpers ----

    private static void StopInstalledCopy()
    {
        foreach (var process in Process.GetProcessesByName("WrenchDesk"))
        {
            if (process.Id == Environment.ProcessId) continue;

            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
                // Already gone, or not ours to stop.
            }
        }
    }

    /// <summary>
    /// Queues a folder deletion for a few seconds' time, once this process has let go of its own
    /// .exe. Returns false if the shell could not be started, so the caller can say so instead.
    /// </summary>
    private static bool ScheduleFolderRemoval(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{directory}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Launch(string exePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not start the installed copy: {ex.Message}");
            Console.WriteLine($"  Start it from the desktop icon instead.");
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Asks a yes/no question, falling back to the default when there is no console to read.</summary>
    private static bool Confirm(string prompt, bool defaultYes)
    {
        Console.Write(prompt);

        try
        {
            if (Console.IsInputRedirected)
            {
                Console.WriteLine(defaultYes ? "yes" : "no");
                return defaultYes;
            }

            var answer = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(answer)) return defaultYes;

            return answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            Console.WriteLine();
            return defaultYes;
        }
    }

    private static void Pause()
    {
        if (Console.IsInputRedirected) return;

        Console.WriteLine("  Press Enter to close.");
        try
        {
            Console.ReadLine();
        }
        catch (IOException)
        {
            // No console to wait on.
        }
    }
}
