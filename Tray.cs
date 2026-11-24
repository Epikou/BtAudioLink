using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace BtAudioLink;

/// <summary>Icône près de l'heure : couleur selon l'état de la liaison, clic pour ouvrir, menu au clic droit.</summary>
public sealed class Tray : IDisposable
{
    readonly WinForms.NotifyIcon icon;
    readonly MainWindow window;
    readonly Dictionary<LinkState, Icon> icons = new();
    string menuKey = "";

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    public Tray(MainWindow window)
    {
        this.window = window;
        icons[LinkState.Stopped] = Draw(Color.FromArgb(0x9A, 0x9A, 0x9A));
        icons[LinkState.Waiting] = Draw(Color.FromArgb(0xE6, 0xAA, 0x28));
        icons[LinkState.Connected] = Draw(Color.FromArgb(0x3C, 0xB4, 0x64));
        icons[LinkState.Error] = Draw(Color.FromArgb(0xDC, 0x50, 0x50));

        icon = new WinForms.NotifyIcon { Icon = icons[LinkState.Stopped], Text = "BtAudioLink", Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) window.ToggleVisible(); };
    }

    /// <summary>Le logo (deux appareils reliés par une onde), d'une seule couleur pour rester lisible en 16 px.</summary>
    static Icon Draw(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.FillEllipse(brush, 0.5f, 11f, 10f, 10f);
            g.FillEllipse(brush, 21.5f, 11f, 10f, 10f);
            g.DrawBeziers(pen, new[]
            {
                new PointF(8f, 16f), new PointF(11f, 5f), new PointF(13.5f, 5f), new PointF(16f, 16f),
                new PointF(18.5f, 27f), new PointF(21f, 27f), new PointF(24f, 16f),
            });
        }
        IntPtr handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Update(LinkState state, string tooltip, bool running, Role role)
    {
        icon.Icon = icons[state];
        icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;

        // Le menu n'est reconstruit que si son contenu change (langue, rôle, marche/arrêt).
        string key = $"{Loc.Language}|{running}|{role}";
        if (key == menuKey) return;
        menuKey = key;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Loc.T("TrayOpen"), null, (_, _) => window.ShowFromTray());
        menu.Items.Add(Loc.T(running ? "Stop" : "Start"), null, (_, _) => window.ToggleRun());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem(Loc.T("TraySender"), null, (_, _) => window.SetRole(Role.Sender)) { Checked = role == Role.Sender });
        menu.Items.Add(new WinForms.ToolStripMenuItem(Loc.T("TrayReceiver"), null, (_, _) => window.SetRole(Role.Receiver)) { Checked = role == Role.Receiver });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("TrayQuit"), null, (_, _) => window.Quit());

        var old = icon.ContextMenuStrip;
        icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    /// <summary>Petit message la première fois qu'on réduit la fenêtre, pour montrer où l'appli est passée.</summary>
    public void ShowHint()
    {
        try { icon.ShowBalloonTip(3000, "BtAudioLink", Loc.T("TrayHint"), WinForms.ToolTipIcon.None); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
