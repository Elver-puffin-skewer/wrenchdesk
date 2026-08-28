using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows.Forms;
using WrenchDesk.Data;

namespace WrenchDesk.Services;

/// <summary>
/// The tray icon that replaces the console window.
///
/// A black window the shop is told not to close is a bad way to run a business tool — it gets
/// closed, or minimised and forgotten, and it says nothing useful. This puts the shop badge in
/// the notification area instead, with the things someone actually needs: open it, get the
/// address to type into a phone, find the records, and stop it properly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShopTray : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly string _localUrl;
    private readonly IReadOnlyList<string> _lanUrls;
    private readonly string _dataDirectory;
    private readonly Action _stopRequested;

    public ShopTray(string shopName, string localUrl, IReadOnlyList<string> lanUrls,
        string dataDirectory, Action stopRequested)
    {
        _localUrl = localUrl;
        _lanUrls = lanUrls;
        _dataDirectory = dataDirectory;
        _stopRequested = stopRequested;

        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("Open WrenchDesk", null, (_, _) => OpenApp())
        {
            Font = new System.Drawing.Font(SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold)
        });

        menu.Items.Add(new ToolStripSeparator());

        if (_lanUrls.Count > 0)
        {
            // The number one thing someone needs and cannot guess: the address for the tablet.
            var phoneMenu = new ToolStripMenuItem("Phone / tablet address");

            foreach (var url in _lanUrls)
            {
                var address = url;
                phoneMenu.DropDownItems.Add(new ToolStripMenuItem($"Copy  {address}", null,
                    (_, _) => CopyToClipboard(address)));
            }

            menu.Items.Add(phoneMenu);
        }

        menu.Items.Add(new ToolStripMenuItem("Open the records folder", null, (_, _) => OpenFolder()));
        menu.Items.Add(new ToolStripMenuItem("Help", null, (_, _) => Open($"{_localUrl}/help")));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Stop WrenchDesk", null, (_, _) => Stop()));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            // Windows truncates this at 63 characters, so keep it to what matters.
            Text = Trim($"{shopName} — running"),
            ContextMenuStrip = menu,
            Visible = true
        };

        // Double-click is what people try first.
        _icon.DoubleClick += (_, _) => OpenApp();
    }

    /// <summary>Tells the shop it is still running after they close the browser tab.</summary>
    public void ShowStartedNotice()
    {
        try
        {
            _icon.BalloonTipTitle = "WrenchDesk is running";
            _icon.BalloonTipText = "It stays down here. Double-click this icon to open it again.";
            _icon.ShowBalloonTip(5000);
        }
        catch (Exception)
        {
            // Notifications can be switched off at the OS level; the icon is enough on its own.
        }
    }

    private void OpenApp() => Open(_localUrl);

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the browser.\n\n{ex.Message}",
                "WrenchDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            Process.Start(new ProcessStartInfo(_dataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {_dataDirectory}.\n\n{ex.Message}",
                "WrenchDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // The clipboard can be locked by another program; showing the address is the fallback.
            MessageBox.Show(text, "Phone / tablet address", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void Stop()
    {
        var answer = MessageBox.Show(
            "Stop WrenchDesk?\n\nThe shop will not be able to use it until it is started again "
          + "from the desktop icon. Nothing is lost.",
            "WrenchDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        _icon.Visible = false;
        _stopRequested();
        ExitThread();
    }

    /// <summary>The shop badge, taken from the .exe itself so there is nothing extra to ship.</summary>
    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception)
        {
            // Fall through to the stock icon rather than failing to start over a picture.
        }

        return SystemIcons.Application;
    }

    private static string Trim(string text) => text.Length <= 63 ? text : text[..60] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
