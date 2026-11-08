using System.Drawing;
using System.Drawing.Imaging;
using Oplus.Core;

namespace Oplus;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string? shot = null;
        bool hide = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--selftest" && i + 1 < args.Length) return SelfTest.Run(Path.GetFullPath(args[i + 1]));
            if (args[i] == "--screenshot" && i + 1 < args.Length) shot = args[++i];
            else if (args[i].Equals("/hide", StringComparison.OrdinalIgnoreCase) || args[i] == "--hide") hide = true;
        }

        ApplicationConfiguration.Initialize();

        if (shot != null) return Screenshot(Path.GetFullPath(shot));

        // One copy at a time.
        using var mutex = new Mutex(true, "Oplus Print Share UI", out bool first);
        if (!first)
        {
            MessageBox.Show("Oplus is already running. Look for its icon next to the clock.", "Oplus", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        using var service = new ShareService();
        service.Start();
        var form = new MainForm(service, screenshot: false) { StartHidden = hide };
        Application.Run(form);
        return 0;
    }

    /// <summary>--screenshot: start normally (without touching the network or settings), draw ~120 frames, save the window, exit.</summary>
    static int Screenshot(string file)
    {
        SettingsStore.ReadOnly = true;
        var form = new MainForm(null, screenshot: true) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), ShowInTaskbar = false };
        int frames = 0, code = 0;
        var t = new System.Windows.Forms.Timer { Interval = 16 };
        t.Tick += (_, _) =>
        {
            form.Invalidate(true);
            if (++frames < 120) return;
            t.Stop();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(file, ImageFormat.Png);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            form.Close();
        };
        form.Shown += (_, _) => t.Start();
        Application.Run(form);
        return code;
    }
}
