using System.Drawing.Printing;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Oplus.Core;

public enum PrinterKind { Printer = 0, Folder = 1, Dropbox = 2 }

/// <summary>One shareable printer: a real Windows printer or a "save to folder" printer.</summary>
public sealed class PrinterEntry
{
    public string Key = "";              // registry sub key (unique id)
    public string RealPrinterName = "";  // Windows printer name (empty for folder printers)
    public string Alias = "";            // name shown on the iPhone / iPad
    public PrinterKind Kind;
    public string FolderPath = "";
    public string Location = "";
    public bool Share;
    public bool Color;
    public bool Duplex;
    public int Dpi = Defaults.FallbackDpi;
    public string Port = "";             // Windows port name (PRINTER_INFO_2.pPortName)
    public string FormName = Defaults.FallbackForm;
    public string UserName = "";
    public string Password = "";
    public bool Installed = true;        // the Windows printer still exists

    public bool IsVirtual => Kind != PrinterKind.Printer;
    public bool Secured => UserName.Length > 0;

    /// <summary>URL-safe resource name (rp in the TXT record).</summary>
    public string Resource => "printers/" + Slug(Alias.Length > 0 ? Alias : Key);

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else if (c == ' ' || c == '.') sb.Append('_');
        }
        return sb.Length == 0 ? "printer" : sb.ToString();
    }
}

public sealed class EmailSettings
{
    public string MailServer = "", UserName = "", Password = "", SenderName = "", SendTo = "", Subject = "";
    public bool Enable;
    public bool IsEmpty => MailServer == "" && UserName == "" && Password == "" && SenderName == "" && SendTo == "" && Subject == "";
}

/// <summary>Reads and writes settings under HKCU\Software\Oplus.</summary>
public static class SettingsStore
{
    public const string Root = @"Software\Oplus";
    const string ListKey = Root + @"\PrinterList";
    const string EmailKey = Root + @"\Email";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Oplus";

    /// <summary>When true nothing is written (used for --screenshot).</summary>
    public static bool ReadOnly;

    // ---------------- printers ----------------
    public static List<PrinterEntry> LoadPrinters()
    {
        var list = new List<PrinterEntry>();
        using (var k = Registry.CurrentUser.OpenSubKey(ListKey))
        {
            if (k != null)
                foreach (var name in k.GetSubKeyNames())
                {
                    using var sk = k.OpenSubKey(name);
                    if (sk == null) continue;
                    var e = new PrinterEntry
                    {
                        Key = name,
                        RealPrinterName = sk.GetValue("RealPrinterName") as string ?? "",
                        Alias = sk.GetValue("Alias") as string ?? name,
                        Kind = (PrinterKind)(sk.GetValue("IsFolder") is int f ? f : 0),
                        FolderPath = sk.GetValue("FolderPath") as string ?? "",
                        Location = sk.GetValue("Location") as string ?? "",
                        Share = sk.GetValue("Share") is int s && s != 0,
                        // Missing values read as no colour, no duplex, 600 dpi.
                        Color = sk.GetValue("Color") is int c && c != 0,
                        Duplex = sk.GetValue("Duplex") is int d && d != 0,
                        Dpi = sk.GetValue("Dpi") is int dpi && dpi > 0 ? dpi : Defaults.FallbackDpi,
                        Port = sk.GetValue("Port") as string ?? "",
                        FormName = sk.GetValue("FormName") as string ?? Defaults.FallbackForm,
                    };
                    using (var sec = sk.OpenSubKey("Security"))
                    {
                        if (sec != null)
                        {
                            e.UserName = sec.GetValue("UserName") as string ?? "";
                            e.Password = Unprotect(sec.GetValue("Password") as string ?? "");
                        }
                    }
                    list.Add(e);
                }
        }

        // Merge in Windows printers that are not stored yet; mark stored ones that vanished.
        var installed = new List<string>();
        // Network printers (\\server\name) are not listed (normal mode).
        try { foreach (string p in PrinterSettings.InstalledPrinters) if (!p.StartsWith(@"\\")) installed.Add(p); } catch { }
        foreach (var e in list.Where(e => !e.IsVirtual))
        {
            e.Installed = installed.Contains(e.RealPrinterName, StringComparer.OrdinalIgnoreCase);
            if (!e.Installed && installed.Count > 0) { DeletePrinter(e); continue; } // removed from Windows
            if (!e.Installed) continue;
            // Refresh Port, FormName, Color, Duplex and Dpi from the driver; save when one changed.
            var (port, form, color, duplex, dpi) = (e.Port, e.FormName, e.Color, e.Duplex, e.Dpi);
            ReadCapabilities(e);
            if (port != e.Port || form != e.FormName || color != e.Color || duplex != e.Duplex || dpi != e.Dpi) SavePrinter(e);
        }
        foreach (var p in installed)
        {
            if (list.Any(e => !e.IsVirtual && string.Equals(e.RealPrinterName, p, StringComparison.OrdinalIgnoreCase))) continue;
            // New Windows printers start shared with the location "<app>@<computer>".
            var e = new PrinterEntry
            {
                Key = p.Replace('\\', '_'), RealPrinterName = p, Alias = p, Kind = PrinterKind.Printer,
                Share = Defaults.ShareNewPrinters, Location = Defaults.DefaultLocation,
            };
            ReadCapabilities(e);
            SavePrinter(e);
            list.Add(e);
        }
        // Folder printers always offer colour, duplex and 600 dpi.
        foreach (var e in list.Where(e => e.IsVirtual)) { e.Color = true; e.Duplex = true; e.Dpi = 600; }
        return list.Where(e => e.IsVirtual || e.Installed)
                   .OrderBy(e => e.IsVirtual).ThenBy(e => e.Alias, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Colour, duplex, resolution, port and paper from the Windows driver:
    /// colour from the default DEVMODE's dmColor (2 = colour), duplex from DeviceCapabilities(DC_DUPLEX),
    /// resolution from dmPrintQuality when it is 300/600/1200, else the device's LOGPIXELSX; then a
    /// value above 600, below 300 or not a multiple of 10 becomes 600. Failure: no colour, no duplex,
    /// 600 dpi, paper "A4".
    /// </summary>
    public static void ReadCapabilities(PrinterEntry e)
    {
        e.Color = false; e.Duplex = false; e.Dpi = Defaults.FallbackDpi; e.FormName = Defaults.FallbackForm;
        try
        {
            var ps = new PrinterSettings { PrinterName = e.RealPrinterName };
            if (!ps.IsValid) return;
            e.Port = PortOf(e.RealPrinterName);
            var page = ps.DefaultPageSettings;
            e.Color = page.Color;
            e.Duplex = ps.CanDuplex;
            e.FormName = FormNameOf(page.PaperSize);
            int dpi = page.PrinterResolution.Kind == PrinterResolutionKind.Custom ? page.PrinterResolution.X : 0;
            if (dpi != 300 && dpi != 600 && dpi != 1200)
            {
                using var g = ps.CreateMeasurementGraphics();
                dpi = (int)Math.Round(g.DpiX);
            }
            e.Dpi = NormaliseDpi(dpi);
        }
        catch { }
    }

    public static int NormaliseDpi(int dpi) =>
        dpi > Defaults.MaxDpi || dpi < Defaults.MinDpi || dpi % 10 != 0 ? Defaults.FallbackDpi : dpi;

    /// <summary>Paper names by DEVMODE paper number, else the driver's form name.</summary>
    static string FormNameOf(PaperSize p) => p.RawKind switch
    {
        1 => "Letter", 4 => "Ledger", 5 => "Legal", 8 => "A3", 9 => "A4", 11 => "A5",
        12 => "B4", 13 => "B5", 70 => "A6", 88 => "B6",
        _ => string.IsNullOrEmpty(p.PaperName) ? Defaults.FallbackForm : (p.PaperName.Length > 31 ? p.PaperName[..31] : p.PaperName),
    };

    [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool OpenPrinter(string name, out IntPtr h, IntPtr defaults);
    [System.Runtime.InteropServices.DllImport("winspool.drv")]
    static extern bool ClosePrinter(IntPtr h);
    [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool GetPrinter(IntPtr h, int level, IntPtr buf, int size, out int needed);

    /// <summary>The printer's port name from PRINTER_INFO_2 (fourth pointer).</summary>
    static string PortOf(string printer)
    {
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) return "";
        try
        {
            GetPrinter(h, 2, IntPtr.Zero, 0, out int need);
            if (need <= 0) return "";
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(need);
            try
            {
                if (!GetPrinter(h, 2, buf, need, out _)) return "";
                var p = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf, 3 * IntPtr.Size);
                return System.Runtime.InteropServices.Marshal.PtrToStringUni(p) ?? "";
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
    }

    public static void SavePrinter(PrinterEntry e)
    {
        if (ReadOnly) return;
        using var sk = Registry.CurrentUser.CreateSubKey(ListKey + "\\" + e.Key);
        sk.SetValue("RealPrinterName", e.RealPrinterName);
        // Values written per printer.
        sk.SetValue("Alias", e.Alias);
        sk.SetValue("AliasLength", e.Alias.Length, RegistryValueKind.DWord);
        sk.SetValue("Port", e.Port);
        sk.SetValue("FormName", e.FormName);
        sk.SetValue("IsFolder", (int)e.Kind, RegistryValueKind.DWord);
        sk.SetValue("FolderPath", e.FolderPath);
        sk.SetValue("Location", e.Location);
        sk.SetValue("Share", e.Share ? 1 : 0, RegistryValueKind.DWord);
        sk.SetValue("Color", e.Color ? 1 : 0, RegistryValueKind.DWord);
        sk.SetValue("Duplex", e.Duplex ? 1 : 0, RegistryValueKind.DWord);
        sk.SetValue("Dpi", e.Dpi, RegistryValueKind.DWord);
        if (e.Secured)
        {
            using var sec = sk.CreateSubKey("Security");
            sec.SetValue("UserName", e.UserName);
            sec.SetValue("Password", Protect(e.Password));
        }
        else sk.DeleteSubKeyTree("Security", false);
    }

    public static void DeletePrinter(PrinterEntry e)
    {
        if (ReadOnly) return;
        using var k = Registry.CurrentUser.OpenSubKey(ListKey, true);
        k?.DeleteSubKeyTree(e.Key, false);
    }

    // ---------------- port ----------------
    /// <summary>connect_port: flag=1 uses the default ports 631 then 12631; flag=0 uses the custom port.</summary>
    public static (bool useDefault, int custom) LoadPort()
    {
        bool useDefault = true; int custom = Defaults.SecondPort;
        using var k = Registry.CurrentUser.OpenSubKey(Root);
        var raw = k?.GetValue("connect_port");
        string? s = raw as string ?? (raw is string[] arr ? string.Join(",", arr) : null);
        if (s != null)
        {
            foreach (var part in s.Split(','))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || !int.TryParse(kv[1], out var v)) continue;
                if (kv[0] == "flag") useDefault = v != 0;
                if (kv[0] == "new") custom = v;
            }
        }
        if (custom < Defaults.MinPort || custom > Defaults.MaxPort) custom = Defaults.SecondPort;
        return (useDefault, custom);
    }

    public static void SavePort(bool useDefault, int custom)
    {
        if (ReadOnly) return;
        using var k = Registry.CurrentUser.CreateSubKey(Root);
        // REG_MULTI_SZ "flag=%d,def_fir=%d,def_sec=%d,new=%d,".
        k.SetValue("connect_port", new[] { $"flag={(useDefault ? 1 : 0)},def_fir={Defaults.FirstPort},def_sec={Defaults.SecondPort},new={custom}," }, RegistryValueKind.MultiString);
    }

    // ---------------- auto start ----------------
    public static bool AutoStart
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) != null; }
        set
        {
            if (ReadOnly) return;
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" /hide");
            else k.DeleteValue(RunName, false);
        }
    }

    // ---------------- email ----------------
    public static EmailSettings LoadEmail()
    {
        var e = new EmailSettings();
        using var k = Registry.CurrentUser.OpenSubKey(EmailKey);
        if (k == null) return e;
        e.MailServer = k.GetValue("MailServer") as string ?? "";
        e.UserName = k.GetValue("UserName") as string ?? "";
        e.Password = Unprotect(k.GetValue("Password") as string ?? "");
        e.SenderName = k.GetValue("SenderName") as string ?? "";
        e.SendTo = k.GetValue("SendTo") as string ?? "";
        e.Subject = k.GetValue("Subject") as string ?? "";
        e.Enable = k.GetValue("Enable") is int en && en != 0;
        return e;
    }

    public static void SaveEmail(EmailSettings e)
    {
        if (ReadOnly) return;
        if (e.IsEmpty) { Registry.CurrentUser.DeleteSubKeyTree(EmailKey, false); return; }
        using var k = Registry.CurrentUser.CreateSubKey(EmailKey);
        k.SetValue("MailServer", e.MailServer);
        k.SetValue("UserName", e.UserName);
        k.SetValue("Password", Protect(e.Password));
        k.SetValue("SenderName", e.SenderName);
        k.SetValue("SendTo", e.SendTo);
        k.SetValue("Subject", e.Subject);
        k.SetValue("Enable", e.Enable ? 1 : 0, RegistryValueKind.DWord);
    }

    // ---------------- helpers ----------------
    static string Protect(string s)
    {
        if (s.Length == 0) return "";
        var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(b);
    }

    static string Unprotect(string s)
    {
        if (s.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static string DataFolder
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oplus");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>The Dropbox folder: %USERPROFILE%\Dropbox, else My Documents\Dropbox.</summary>
    public static string DropboxFolder
    {
        get
        {
            // %userprofile%\Dropbox, then %userprofile%\My Documents\Dropbox, else empty.
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var a = Path.Combine(profile, "Dropbox");
            if (Directory.Exists(a)) return a;
            var b = Path.Combine(profile, "My Documents", "Dropbox");
            if (Directory.Exists(b)) return b;
            var c = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Dropbox");
            return Directory.Exists(c) ? c : "";
        }
    }
}

/// <summary>Fixed numbers (see MECHANICS.md / mechanics.json).</summary>
public static class Defaults
{
    public const int FirstPort = 631;
    public const int SecondPort = 12631;
    public const int MinPort = 1024, MaxPort = 65535;
    // Real printers: the driver value from ReadCapabilities; 600 when it cannot be read.
    public const int FolderPrinterDpi = 600;
    public const int TxtPriority = 40;
    public const string UrfCaps = "CP1,IS1,OB10,PQ3-4-5,MT1-2-3-4-5-6,DM3";
    public const string RealFormats = "image/urf,image/jpeg";
    public const string FolderFormats = "image/urf,application/pdf,image/jpeg";
    public const string ServiceType = "_ipp._tcp";
    public const string SubType = "_universal";
    public const long LogRollBytes = 1073741824; // 1 GiB
    public const int EmailFieldMax = 32, EmailSubjectMax = 128;
    public const bool ShareNewPrinters = true;
    public const int FallbackDpi = 600, MinDpi = 300, MaxDpi = 600;
    public const string FallbackForm = "A4";
    public static string DefaultLocation => Maker + "@" + Environment.MachineName;
    public const int NameMax = 256, LocationMax = 256;
    public const int SecurityFieldMax = 32;
    public const string LogHeaderColor = "#BBE094", LogTitleColor = "#232584", LogCellColor = "#9E9E9F";
    public const string Maker = "Oplus";
    public const string Model = "Mobile Printer";
}
