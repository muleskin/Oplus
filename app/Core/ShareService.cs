using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;

namespace Oplus.Core;

/// <summary>Runs the print server and the network announcer for the shared printers.</summary>
public sealed class ShareService : IDisposable
{
    readonly MdnsResponder _mdns = new();
    IppServer? _ipp;
    List<PrinterEntry> _shared = new();
    readonly Dictionary<string, string> _lastProblem = new();
    System.Threading.Timer? _monitor;

    public string Status { get; private set; } = "Not started";
    public bool Running => _ipp != null;
    public int Port => _ipp?.Port ?? 0;
    public IppServer? Server => _ipp;
    public event Action? Changed;
    public event Action? JobsChanged;

    public void Start()
    {
        try { _mdns.Start(); }
        catch (Exception ex) { Status = "Network announcer could not start: " + ex.Message; }
        StartServer();
        _monitor = new System.Threading.Timer(_ => CheckPrinters(), null, 10000, 10000);
    }

    void StartServer()
    {
        _ipp?.Dispose();
        _ipp = null;
        var (useDefault, custom) = SettingsStore.LoadPort();
        var ports = useDefault ? new[] { Defaults.FirstPort, Defaults.SecondPort } : new[] { custom };
        var srv = new IppServer(_mdns.HostName);
        srv.JobsChanged += () => JobsChanged?.Invoke();
        srv.JobFailed += (job, why) => SendAlert(job.Printer.Alias, $"Job \"{job.Name}\" from {job.User} failed: {why}");
        try
        {
            srv.Start(ports);
            _ipp = srv;
            Status = $"Sharing on port {srv.Port}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        Apply(_shared);
        Changed?.Invoke();
    }

    /// <summary>Port settings changed: reopen the server on the new port and re-announce.</summary>
    public void RestartServer() { StartServer(); }

    public void Apply(IEnumerable<PrinterEntry> all)
    {
        _shared = all.Where(p => p.Share && (p.IsVirtual || p.Installed)).ToList();
        if (_ipp == null) { _mdns.SetServices(Array.Empty<MdnsService>()); return; }
        _ipp.SetPrinters(_shared);
        _mdns.SetServices(_shared.Select(BuildService).ToList());
        Changed?.Invoke();
    }

    /// <summary>The DNS-SD record of one printer.</summary>
    MdnsService BuildService(PrinterEntry p)
    {
        string formats = p.IsVirtual ? Defaults.FolderFormats : Defaults.RealFormats;
        var txt = new List<KeyValuePair<string, string>>
        {
            new("txtvers", "1"),
            new("qtotal", "1"),
            new("rp", p.Resource),
            new("ty", $"{Defaults.Maker} {Defaults.Model}"),
            new("adminurl", $"http://{_mdns.HostName}:{Port}/"),
            new("note", p.Location),
            new("priority", Defaults.TxtPriority.ToString()),
            new("product", $"({Defaults.Model})"),
            new("pdl", formats),
            new("Color", p.Color ? "T" : "F"),
            new("Duplex", p.Duplex ? "T" : "F"),
            new("usb_MFG", Defaults.Maker),
            new("usb_MDL", Defaults.Model),
        };
        if (p.Secured) txt.Add(new("air", "username,password"));
        txt.Add(new("URF", string.Join(",", IppServer.UrfKeywords(p))));
        txt.Add(new("Transparent", "T"));
        txt.Add(new("Binary", "T"));
        txt.Add(new("Bind", "T"));
        txt.Add(new("PaperCustom", "F"));
        txt.Add(new("Punch", "0"));
        txt.Add(new("UUID", _ipp!.UuidFor(p).ToString()));
        txt.Add(new("kind", "document,photo"));
        txt.Add(new("printer-state", "3"));
        // Names may be up to 256 characters; a network name label holds 63 bytes.
        return new MdnsService { Instance = DnsWriter.Fit(p.Alias, 63), Port = Port, Txt = txt };
    }

    // ------------------------------------------------------------------ printer problems
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool OpenPrinter(string name, out IntPtr h, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetPrinter(IntPtr h, int level, IntPtr buf, int size, out int needed);

    /// <summary>Problem text for a Windows printer, or "" when it is fine.</summary>
    public static string ProblemOf(string printerName)
    {
        uint st = 0;
        try
        {
            if (!OpenPrinter(printerName, out var h, IntPtr.Zero)) return "";
            try
            {
                var buf = Marshal.AllocHGlobal(4);
                try { if (GetPrinter(h, 6, buf, 4, out _)) st = (uint)Marshal.ReadInt32(buf); }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { ClosePrinter(h); }
        }
        catch { return ""; }
        if ((st & 0x00000008) != 0) return "PAPER JAM";
        if ((st & 0x00000010) != 0) return "PAPER OUT";
        if ((st & 0x00040000) != 0) return "TONER EMPTY";
        if ((st & 0x00020000) != 0) return "LOW TONER";
        if ((st & 0x00400000) != 0) return "COVER OPEN";
        if ((st & 0x00000001) != 0) return "PAUSED";
        if ((st & 0x00000080) != 0) return "OFFLINE";
        if ((st & 0x00000002) != 0) return "OTHER ERROR";
        return "";
    }

    void CheckPrinters()
    {
        foreach (var p in _shared.Where(p => !p.IsVirtual).ToList())
        {
            var problem = ProblemOf(p.RealPrinterName);
            _lastProblem.TryGetValue(p.Key, out var before);
            if (problem != (before ?? ""))
            {
                _lastProblem[p.Key] = problem;
                if (problem.Length > 0) SendAlert(p.Alias, $"Printer \"{p.Alias}\" reports: {problem}");
            }
        }
    }

    /// <summary>Sends the email alert when it is turned on.</summary>
    public static void SendAlert(string printer, string message)
    {
        var e = SettingsStore.LoadEmail();
        if (!e.Enable || e.IsEmpty) return;
        ThreadPool.QueueUserWorkItem(_ => { try { SendMail(e, $"{e.Subject} - {printer}", message); } catch { } });
    }

    /// <summary>Server may be "host" or "host:port"; port 25 sends plain, any other port uses STARTTLS.</summary>
    public static void SendMail(EmailSettings e, string subject, string body)
    {
        string host = e.MailServer; int port = 587;
        int i = host.LastIndexOf(':');
        if (i > 0 && int.TryParse(host[(i + 1)..], out var p)) { port = p; host = host[..i]; }
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = port != 25,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20000,
        };
        if (e.UserName.Length > 0) client.Credentials = new NetworkCredential(e.UserName, e.Password);
        string fromAddr = e.UserName.Contains('@') ? e.UserName : e.SendTo;
        using var msg = new MailMessage(new MailAddress(fromAddr, e.SenderName), new MailAddress(e.SendTo))
        {
            Subject = subject,
            Body = $"{body}\r\n\r\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\nComputer: {Environment.MachineName}",
        };
        client.Send(msg);
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        _ipp?.Dispose();
        _mdns.Dispose();
    }
}
