using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WrenchDesk.Services;

/// <summary>
/// Gets a console for a program that normally has none.
///
/// WrenchDesk is built as a windowed program so the shop never sees a black box, but a console is
/// still the quickest way to see what is happening when something is wrong — hence --console.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConsoleWindow
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>True when this process already has somewhere to write.</summary>
    public static bool IsAttached => GetConsoleWindow() != IntPtr.Zero;

    /// <summary>
    /// Writes into the terminal that launched us where there is one, so output lands in the
    /// window someone is already looking at; otherwise opens a console of our own.
    /// </summary>
    public static void Attach()
    {
        if (IsAttached) return;

        if (!AttachConsole(AttachParentProcess))
            AllocConsole();

        RebindStandardStreams();
    }

    /// <summary>
    /// Console.Out was bound to nothing when the process started without a console, so it has to
    /// be pointed at the new handles or every write disappears silently.
    /// </summary>
    private static void RebindStandardStreams()
    {
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);

            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);

            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }
        catch (IOException)
        {
            // No usable handles. Callers fall back to dialogs.
        }
    }
}
