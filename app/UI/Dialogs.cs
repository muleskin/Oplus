using System.Drawing;
using Oplus.Core;

namespace Oplus;

public static class Dialogs
{
    static (Button ok, Button cancel) OkCancel(Form f, int y, string okText = "OK")
    {
        var ok = Ui.Btn(okText, f.ClientSize.Width - 170, y, 76, 28);
        var cancel = Ui.Btn("Cancel", f.ClientSize.Width - 88, y, 76, 28);
        cancel.DialogResult = DialogResult.Cancel;
        f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return (ok, cancel);
    }

    // ------------------------------------------------------------------ security
    public static bool Security(IWin32Window owner, PrinterEntry p)
    {
        using var f = Ui.Dialog("Security - " + p.Alias, 360, 200);
        f.Controls.Add(new PictureBox { Left = 14, Top = 14, Width = 32, Height = 32, Image = Icons.Lock(32) });
        f.Controls.Add(Ui.Lbl("There is only one setting for one printer.", 56, 18, 290, 24));
        f.Controls.Add(Ui.Lbl("User name", 30, 62, 90, 24));
        f.Controls.Add(Ui.Lbl("Password", 30, 96, 90, 24));
        var user = Ui.Box(124, 62, 210, Defaults.SecurityFieldMax); user.Text = p.UserName;
        var pass = Ui.Box(124, 96, 210, Defaults.SecurityFieldMax, password: true); pass.Text = p.Password;
        f.Controls.Add(user); f.Controls.Add(pass);
        var none = Ui.Btn("None", 30, 130, 76, 26, (_, _) => { user.Text = ""; pass.Text = ""; });
        f.Controls.Add(none);
        f.Controls.Add(Ui.Lbl("iPhone and iPad will ask for these before printing.", 112, 130, 236, 26));
        var (ok, _) = OkCancel(f, 162);
        ok.Click += (_, _) =>
        {
            string u = user.Text, pw = pass.Text;
            if (u.Length == 0 && pw.Length == 0) { f.DialogResult = DialogResult.OK; return; }
            if (u.Length < 1 || u.Length > Defaults.SecurityFieldMax) { Ui.Warn(f, $"User name: 1~{Defaults.SecurityFieldMax} characters."); return; }
            if (pw.Length < 1 || pw.Length > Defaults.SecurityFieldMax) { Ui.Warn(f, $"Password: 1~{Defaults.SecurityFieldMax} characters."); return; }
            if (u.Contains(':')) { Ui.Warn(f, "Invalid character \":\"."); return; }
            f.DialogResult = DialogResult.OK;
        };
        if (f.ShowDialog(owner) != DialogResult.OK) return false;
        p.UserName = user.Text; p.Password = user.Text.Length == 0 ? "" : pass.Text;
        return true;
    }

    // ------------------------------------------------------------------ add virtual printer
    public static PrinterEntry? AddVirtualPrinter(IWin32Window owner, PrinterKind kind, List<PrinterEntry> existing)
    {
        string title = kind == PrinterKind.Dropbox ? "Save to Dropbox" : "Save to Folder";
        using var f = Ui.Dialog(title, 380, 170);
        f.Controls.Add(Ui.Lbl("Printer name", 16, 16, 100, 24));
        f.Controls.Add(Ui.Lbl("Folder location", 16, 48, 100, 24));
        var name = Ui.Box(120, 16, 244, Defaults.NameMax);
        var folder = Ui.Box(120, 48, 244); folder.ReadOnly = true;
        string start = kind == PrinterKind.Dropbox ? SettingsStore.DropboxFolder
                                                   : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Oplus Prints");
        folder.Text = start;
        bool nameTouched = false;
        name.TextChanged += (_, _) => { if (name.Focused) nameTouched = true; };
        void SuggestName()
        {
            if (nameTouched) return;
            var leaf = Path.GetFileName(folder.Text.TrimEnd('\\'));
            name.Text = $"Save to {(leaf.Length > 0 ? leaf : folder.Text)}";
        }
        SuggestName();
        f.Controls.Add(name); f.Controls.Add(folder);
        f.Controls.Add(new PictureBox { Left = 16, Top = 82, Width = 64, Height = 64, Image = Icons.Folder(64, kind == PrinterKind.Dropbox) });
        var browse = Ui.Btn("Browse", 288, 80, 76, 26, (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Browse folders", UseDescriptionForTitle = true, ShowNewFolderButton = true, InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : "" };
            if (dlg.ShowDialog(f) == DialogResult.OK) { folder.Text = dlg.SelectedPath; SuggestName(); }
        });
        f.Controls.Add(browse);
        var (ok, _) = OkCancel(f, 130);
        ok.Click += (_, _) =>
        {
            string n = name.Text.Trim(), path = folder.Text.Trim();
            if (n.Length < 1 || n.Length > Defaults.NameMax) { Ui.Warn(f, $"Printer name: 1~{Defaults.NameMax} characters."); return; }
            if (n.Contains('\\')) { Ui.Warn(f, "Input error! Printer name without \"\\\"."); return; }
            if (path.Length < 1 || path.Length > 248) { Ui.Warn(f, "Folder location: 1~248 characters, please select again!"); return; }
            if (existing.Any(e => string.Equals(e.Alias, n, StringComparison.OrdinalIgnoreCase)))
            { Ui.Warn(f, $"The printer name \"{n}\" already exists.\nPlease enter another printer name."); return; }
            var used = existing.FirstOrDefault(e => e.IsVirtual && string.Equals(e.FolderPath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (used != null) { Ui.Warn(f, $"The folder \"{path}\"\nis used by \"{used.Alias}\".\nPlease select another folder."); return; }
            try { Directory.CreateDirectory(path); }
            catch { Ui.Warn(f, "Invalid path, please select again!"); return; }
            try { var probe = Path.Combine(path, ".oplus-probe"); File.WriteAllText(probe, ""); File.Delete(probe); }
            catch { Ui.Warn(f, "Authority limit, please select again!"); return; }
            f.DialogResult = DialogResult.OK;
        };
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        string key = "virtual-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
        return new PrinterEntry
        {
            Key = key, Alias = name.Text.Trim(), Kind = kind, FolderPath = folder.Text.Trim(), Share = true,
            Color = true, Duplex = true, Dpi = Defaults.FolderPrinterDpi, Location = Defaults.DefaultLocation,
        };
    }

    // ------------------------------------------------------------------ settings
    public static bool Settings(IWin32Window owner)
    {
        using var f = Ui.Dialog("Settings", 470, 150);
        f.Controls.Add(new PictureBox { Left = 14, Top = 16, Width = 100, Height = 100, Image = Icons.AppMark(96), SizeMode = PictureBoxSizeMode.CenterImage });
        var grp = new GroupBox { Text = "Settings", Left = 128, Top = 6, Width = 330, Height = 52, Font = Ui.Body };
        var auto = new CheckBox { Text = "Launch Oplus on startup.", Left = 14, Top = 20, Width = 220, Checked = SettingsStore.AutoStart };
        grp.Controls.Add(auto);
        f.Controls.Add(grp);
        var showAdv = Ui.Btn("Show Advance", 360, 64, 98, 26);
        f.Controls.Add(showAdv);

        var adv = new GroupBox { Text = "Advance", Left = 128, Top = 96, Width = 330, Height = 140, Visible = false, Font = Ui.Body };
        adv.Controls.Add(new Label { Text = "This is a setting for print port. Make sure it allows access when you set up a custom port.", Left = 12, Top = 20, Width = 306, Height = 34 });
        var (useDefault, custom) = SettingsStore.LoadPort();
        var rDef = new RadioButton { Text = "Default port :", Left = 12, Top = 58, Width = 110, Checked = useDefault };
        var rCus = new RadioButton { Text = "Custom port :", Left = 12, Top = 88, Width = 110, Checked = !useDefault };
        adv.Controls.Add(rDef); adv.Controls.Add(rCus);
        adv.Controls.Add(new Label { Text = $"{Defaults.FirstPort} or {Defaults.SecondPort}", Left = 126, Top = 62, Width = 120 });
        var port = Ui.Box(126, 88, 80, 5); port.Text = custom.ToString(); port.Enabled = !useDefault;
        rCus.CheckedChanged += (_, _) => port.Enabled = rCus.Checked;
        adv.Controls.Add(port);
        adv.Controls.Add(new Label { Text = $"({Defaults.MinPort} - {Defaults.MaxPort})", Left = 212, Top = 92, Width = 110 });
        f.Controls.Add(adv);

        var (ok, cancel) = OkCancel(f, 112, "Confirm");
        void Layout(bool open)
        {
            adv.Visible = open;
            showAdv.Text = open ? "Hide Advance" : "Show Advance";
            f.ClientSize = new Size(470, open ? 290 : 150);
            ok.Top = cancel.Top = open ? 252 : 112;
        }
        showAdv.Click += (_, _) => Layout(!adv.Visible);
        int newPort = custom;
        ok.Click += (_, _) =>
        {
            if (rCus.Checked)
            {
                if (!int.TryParse(port.Text, out newPort) || newPort < Defaults.MinPort || newPort > Defaults.MaxPort)
                { Ui.Warn(f, $"Range of custom port must be between {Defaults.MinPort} and {Defaults.MaxPort}."); return; }
            }
            f.DialogResult = DialogResult.OK;
        };
        if (f.ShowDialog(owner) != DialogResult.OK) return false;
        try { SettingsStore.AutoStart = auto.Checked; } catch (Exception ex) { Ui.Warn(owner, "WARNING! Setting value error! Please try again!\n" + ex.Message); }
        bool portChanged = rDef.Checked != useDefault || (rCus.Checked && newPort != custom);
        SettingsStore.SavePort(rDef.Checked, rCus.Checked ? newPort : custom);
        return portChanged;
    }

    // ------------------------------------------------------------------ about
    public static void About(IWin32Window owner)
    {
        using var f = Ui.Dialog("About Oplus", 340, 230);
        f.Controls.Add(new PictureBox { Left = 14, Top = 16, Width = 96, Height = 96, Image = Icons.AppMark(96) });
        f.Controls.Add(Ui.Lbl("Oplus Print Share", 124, 16, 200, 26, ContentAlignment.MiddleLeft, Ui.Title));
        f.Controls.Add(Ui.Lbl($"Version {Application.ProductVersion.Split('+')[0]}", 124, 44, 200, 20));
        var text = new Label
        {
            Left = 124, Top = 68, Width = 204, Height = 90, ForeColor = Ui.SubInk,
            Text = "Shares your Windows printers and folders with iPhone and iPad.\n\nFree to use: no licence key or activation needed.",
        };
        f.Controls.Add(text);
        var ok = Ui.Btn("OK", 132, 186, 76, 28); ok.DialogResult = DialogResult.OK;
        f.Controls.Add(ok); f.AcceptButton = ok; f.CancelButton = ok;
        f.ShowDialog(owner);
    }

    // ------------------------------------------------------------------ log
    /// <summary>
    /// Copies newlog.csv to %TEMP%\Oplus\YYYYMMDD-hhmmss-log.csv, writes a web page table of it next to
    /// it (YYYYMMDD-hhmmss-log.htm, with a Save link to the CSV) and opens the page in the browser.
    /// </summary>
    public static void Log(IWin32Window owner)
    {
        var src = JobLog.CurrentPath;
        if (!File.Exists(src)) { MessageBox.Show(owner, "No Log file is available.", "Oplus"); return; }
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Oplus");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string csvPath = Path.Combine(dir, $"{stamp}-log.csv"), htmPath = Path.Combine(dir, $"{stamp}-log.htm");
            File.Copy(src, csvPath, true);
            File.WriteAllText(htmPath, LogPage(File.ReadAllLines(csvPath), Path.GetFileName(csvPath)), System.Text.Encoding.UTF8);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(htmPath) { UseShellExecute = true });
        }
        catch (Exception ex) { Ui.Warn(owner, "Unable to create a temporary file.\n" + ex.Message); }
    }

    static string LogPage(string[] lines, string csvName)
    {
        static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Oplus log</title>")
          .Append("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:0}td,th{padding:5px;font-size:13px}</style></head><body>")
          .Append($"<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr bgcolor=\"{Defaults.LogHeaderColor}\"><td style=\"padding:12px 24px;font-size:22px;font-weight:bold\">Oplus log</td>")
          .Append($"<td align=\"right\" style=\"padding:12px 24px\"><a href=\"{H(csvName)}\" download style=\"background:#1e9fdc;color:#fff;padding:8px 14px;border-radius:4px;text-decoration:none;font-weight:bold\">Save Log File</a></td></tr>")
          .Append($"<tr bgcolor=\"{Defaults.LogHeaderColor}\" height=\"15\"><td colspan=\"2\"></td></tr></table><br>")
          .Append($"<table width=\"100%\" border=\"1\" cellpadding=\"5\" style=\"border-collapse:collapse\"><tr bgcolor=\"{Defaults.LogTitleColor}\">");
        var rows = lines.Where(l => l.Length > 0).Select(l => l.Split(',')).ToList();
        int cols = rows.Count > 0 ? rows.Max(r => r.Length) : 0;
        foreach (var head in rows.FirstOrDefault() ?? Array.Empty<string>()) sb.Append($"<th><font color=\"#FFFFFF\">{H(head)}</font></th>");
        sb.Append("</tr>");
        foreach (var r in rows.Skip(1))
        {
            sb.Append("<tr>");
            for (int i = 0; i < cols; i++) sb.Append($"<td bgcolor=\"{Defaults.LogCellColor}\">{(i < r.Length && r[i].Trim().Length > 0 ? H(r[i]) : "&nbsp;")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ email alert
    public static void Email(IWin32Window owner)
    {
        var e = SettingsStore.LoadEmail();
        using var f = Ui.Dialog("Email Alert", 500, 300);
        f.Controls.Add(Ui.Lbl("Email Alert", 16, 14, 120, 22, ContentAlignment.MiddleLeft, Ui.Title));
        bool enabled = e.Enable;
        var toggle = Ui.Btn("", 30, 50, 72, 72);
        toggle.Image = Icons.Mail(56, enabled);
        var status = Ui.Lbl("", 16, 128, 110, 40, ContentAlignment.TopCenter);
        void Show() { toggle.Image?.Dispose(); toggle.Image = Icons.Mail(56, enabled); status.Text = "Status:\n" + (enabled ? "Enable" : "Disable"); }
        Show();
        toggle.Click += (_, _) => { enabled = !enabled; Show(); };
        f.Controls.Add(toggle); f.Controls.Add(status);

        f.Controls.Add(Ui.Lbl("There is only one setting for all printers !", 160, 14, 320, 22));
        string[] labels = { "Mail server (SMTP)", "User name", "Password", "Sender name", "Send to", "Subject" };
        string[] values = { e.MailServer, e.UserName, e.Password, e.SenderName, e.SendTo, e.Subject };
        var boxes = new TextBox[6];
        for (int i = 0; i < 6; i++)
        {
            f.Controls.Add(Ui.Lbl(labels[i], 150, 46 + i * 30, 120, 24));
            boxes[i] = Ui.Box(274, 46 + i * 30, 210, i == 5 ? Defaults.EmailSubjectMax : Defaults.EmailFieldMax, password: i == 2);
            boxes[i].Text = values[i];
            f.Controls.Add(boxes[i]);
        }
        new ToolTip().SetToolTip(boxes[0], "IP or Domain name (add :port if needed, e.g. smtp.example.com:587)");

        EmailSettings Read() => new()
        {
            MailServer = boxes[0].Text.Trim(), UserName = boxes[1].Text.Trim(), Password = boxes[2].Text,
            SenderName = boxes[3].Text.Trim(), SendTo = boxes[4].Text.Trim(), Subject = boxes[5].Text.Trim(), Enable = enabled,
        };
        string? Check(EmailSettings s)
        {
            if (s.IsEmpty) return null;
            string[] names = { "Mail server address", "User name", "Password", "Sender name", "Send to" };
            string[] v = { s.MailServer, s.UserName, s.Password, s.SenderName, s.SendTo };
            for (int i = 0; i < 5; i++)
                if (v[i].Length < 1 || v[i].Length > Defaults.EmailFieldMax) return $"{names[i]}: 1~{Defaults.EmailFieldMax} characters";
            if (s.Subject.Length < 1 || s.Subject.Length > Defaults.EmailSubjectMax) return $"Subject: 1~{Defaults.EmailSubjectMax} characters";
            return null;
        }

        var test = Ui.Btn("Send test", 16, 260, 90, 28, (_, _) =>
        {
            var s = Read();
            if (Check(s) is string err || s.IsEmpty) { Ui.Warn(f, Check(s) ?? "Please fill in the mail settings."); return; }
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                ShareService.SendMail(s, s.Subject + " - test", "This is a test message from Oplus.");
                MessageBox.Show(f, "The test message was sent.", "Email Alert", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Ui.Warn(f, "The test message could not be sent:\n" + ex.Message); }
            finally { Cursor.Current = Cursors.Default; }
        });
        f.Controls.Add(test);
        var (ok, _) = OkCancel(f, 260);
        ok.Click += (_, _) =>
        {
            var s = Read();
            if (Check(s) is string err) { Ui.Warn(f, err); return; }
            SettingsStore.SaveEmail(s);
            f.DialogResult = DialogResult.OK;
        };
        f.ShowDialog(owner);
    }
}
