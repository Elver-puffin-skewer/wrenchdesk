using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace WrenchDesk.Services;

/// <summary>
/// Keeps one copy of WrenchDesk running at a time.
///
/// The program lives in the notification area, so the shop screen being put away looks exactly
/// like the program being closed. Double-clicking the desktop icon again is then the obvious
/// thing to do — and without this, that second copy starts up, fails to take the port the first
/// one is already serving on, and dies with an error nobody can act on. To the counter that
/// reads as "I clicked it and it broke".
///
/// The claim is keyed on the port because the port is what the two copies actually fight over.
/// A portable copy on a USB stick set to a different port is a separate program as far as this
/// is concerned, and is left alone.
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// Held for the life of the process. Windows keeps the named object alive while any handle
    /// is open, so this is what a later copy finds; the handle goes when the process does,
    /// including when it is killed, so a crash cannot lock the shop out of its own program.
    /// </summary>
    private static Mutex? _claim;

    /// <summary>The port <see cref="_claim"/> was taken for, so asking about a different one is answered honestly.</summary>
    private static int _claimedPort;

    /// <summary>
    /// True when this copy is the one that should run. False means another is already serving
    /// the same port and this one should hand over and stop.
    /// </summary>
    public static bool TryClaim(int port)
    {
        // Local\ scopes this to the signed-in user, which matches an install that is per-user
        // and data that sits in that user's Documents.
        //
        // Ownership is deliberately not taken: nothing here ever waits on the mutex, and the
        // existence of the named object is the whole signal. That keeps abandoned-mutex
        // handling out of a path that runs before anything else in the program.
        // Already claimed by this process. Building a second handle over the top would drop the
        // first one on the floor and quietly give the claim away.
        if (_claim is not null) return _claimedPort == port;

        var claim = new Mutex(initiallyOwned: false, $@"Local\WrenchDesk-{port}", out var createdNew);

        if (!createdNew)
        {
            claim.Dispose();
            return false;
        }

        _claim = claim;
        _claimedPort = port;
        return true;
    }

    /// <summary>
    /// Does what the person who double-clicked the icon meant: puts the shop screen back in
    /// front of them, using the copy that is already running.
    ///
    /// A shop that has switched OpenBrowser off has said it does not want this program opening
    /// browser windows, and that answer holds whoever started it. They are told where it went
    /// instead, so the click still does something they can see.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void HandOver(string url, bool openBrowser = true)
    {
        if (!openBrowser)
        {
            SayWhereItWent();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser to open means the click produced nothing at all, which is the very
            // thing this class exists to avoid. Say where the program went instead.
            SayWhereItWent();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SayWhereItWent() =>
        MessageBox.Show(
            "WrenchDesk is already running." + Environment.NewLine + Environment.NewLine
          + "It sits down by the clock, in the notification area. Click the small arrow "
          + "next to the clock if you cannot see its icon, then double-click it to open "
          + "the shop screen.",
            "WrenchDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
