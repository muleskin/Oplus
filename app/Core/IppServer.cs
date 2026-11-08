using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Oplus.Core;

/// <summary>
/// The print server: an IPP/1.1 server over HTTP that iPhones and iPads send jobs to. One port
/// serves every shared printer; each printer lives at its own resource path (rp in the TXT record).
/// </summary>
public sealed class IppServer : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<string, PrinterEntry> _printers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Guid> _uuids = new();
    readonly List<PrintJob> _jobs = new();
    readonly BlockingCollection<PrintJob> _queue = new();
    readonly DateTime _startTime = DateTime.UtcNow;
    readonly List<TcpListener> _listeners = new();
    int _nextJobId = 1;
    volatile bool _running;
    Thread? _worker;

    public int Port { get; private set; }
    public string HostName { get; }
    public event Action<string>? Log;
    public event Action? JobsChanged;
    /// <summary>Raised when a job fails (for the email alert).</summary>
    public event Action<PrintJob, string>? JobFailed;

    public IppServer(string hostName) { HostName = hostName; }

    public PrintJob? CurrentJob { get { lock (_gate) return _jobs.FirstOrDefault(j => j.State == JobState.Processing); } }
    public List<PrintJob> Jobs { get { lock (_gate) return _jobs.ToList(); } }

    /// <summary>Starts listening on the first port that can be opened.</summary>
    public void Start(IEnumerable<int> ports, IPAddress? bind = null)
    {
        Exception? last = null;
        foreach (var port in ports)
        {
            try
            {
                var l4 = new TcpListener(bind ?? IPAddress.Any, port);
                l4.Start();
                _listeners.Add(l4);
                if (bind == null)
                try { var l6 = new TcpListener(IPAddress.IPv6Any, port); l6.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true); l6.Start(); _listeners.Add(l6); }
                catch { /* IPv6 optional */ }
                Port = ((IPEndPoint)l4.LocalEndpoint).Port;
                break;
            }
            catch (Exception ex) { last = ex; foreach (var l in _listeners) l.Stop(); _listeners.Clear(); }
        }
        if (_listeners.Count == 0) throw new IOException("Cannot open the print port: " + last?.Message, last);
        _running = true;
        foreach (var l in _listeners) { var t = new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "IPP accept" }; t.Start(); }
        _worker = new Thread(WorkLoop) { IsBackground = true, Name = "IPP jobs" };
        _worker.Start();
        Log?.Invoke($"Listening on port {Port}.");
    }

    public void SetPrinters(IEnumerable<PrinterEntry> shared)
    {
        lock (_gate)
        {
            _printers.Clear();
            foreach (var p in shared)
            {
                _printers[p.Resource] = p;
                if (!_uuids.ContainsKey(p.Key)) _uuids[p.Key] = StableGuid(p.Key);
            }
        }
    }

    public Guid UuidFor(PrinterEntry p)
    {
        lock (_gate) { if (!_uuids.TryGetValue(p.Key, out var g)) _uuids[p.Key] = g = StableGuid(p.Key); return g; }
    }

    static Guid StableGuid(string s)
    {
        var h = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "/" + s));
        h[6] = (byte)((h[6] & 0x0F) | 0x30); h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h);
    }

    public bool CancelJob(int id)
    {
        PrintJob? j; lock (_gate) j = _jobs.FirstOrDefault(x => x.Id == id);
        if (j == null || j.IsDone) return false;
        j.Cancel.Cancel();
        if (j.State == JobState.Pending) { j.State = JobState.Canceled; j.StateReason = "job-canceled-by-user"; j.Finished = DateTime.Now; }
        JobsChanged?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ connections
    void AcceptLoop(TcpListener l)
    {
        while (_running)
        {
            try
            {
                var c = l.AcceptTcpClient();
                var t = new Thread(() => Serve(c)) { IsBackground = true, Name = "IPP client" };
                t.Start();
            }
            catch { if (!_running) return; }
        }
    }

    void Serve(TcpClient client)
    {
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "";
        try
        {
            client.ReceiveTimeout = 30000; client.SendTimeout = 30000;
            using var ns = client.GetStream();
            var reader = new HttpReader(ns);
            while (_running)
            {
                var req = reader.ReadRequest();
                if (req == null) break;
                bool keep = HandleHttp(req, ns, remote, reader);
                if (!keep) break;
            }
        }
        catch (IOException) { }
        catch (Exception ex) { Log?.Invoke($"Client {remote}: {ex.Message}"); }
        finally { client.Close(); }
    }

    bool HandleHttp(HttpRequest req, Stream ns, string remote, HttpReader reader)
    {
        bool keepAlive = !string.Equals(req.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
        string path = req.Path.Split('?')[0].TrimStart('/');

        if (req.Method == "OPTIONS")
        {
            WriteResponse(ns, 200, "OK", null, Array.Empty<byte>(), keepAlive, "Allow: GET, HEAD, OPTIONS, POST");
            return keepAlive;
        }
        if (req.Method == "GET" || req.Method == "HEAD")
        {
            if (path.Equals("icon.png", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/icon.png", StringComparison.OrdinalIgnoreCase))
                WriteResponse(ns, 200, "OK", "image/png", req.Method == "HEAD" ? Array.Empty<byte>() : PrinterIcon.Png, keepAlive);
            else
                WriteResponse(ns, 200, "OK", "text/html; charset=utf-8", req.Method == "HEAD" ? Array.Empty<byte>() : StatusPage(), keepAlive);
            return keepAlive;
        }
        if (req.Method != "POST")
        {
            WriteResponse(ns, 405, "Method Not Allowed", "text/plain", Encoding.ASCII.GetBytes("Bad method"), false);
            return false;
        }
        if (!string.Equals(req.Header("Content-Type")?.Split(';')[0].Trim(), "application/ipp", StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(ns, 400, "Bad Request", "text/plain", Encoding.ASCII.GetBytes("Expected application/ipp"), false);
            return false;
        }

        // Find the printer before reading the body, so a secured printer can ask for a password first.
        PrinterEntry? printer;
        lock (_gate)
        {
            _printers.TryGetValue(path, out printer);
            if (printer == null && _printers.Count == 1 && (path == "" || path == "ipp/print")) printer = _printers.Values.First();
        }

        if (string.Equals(req.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
        {
            if (printer != null && printer.Secured && !Authorized(req, printer) && req.ContentLength > 0)
            {
                // A Print-Job without credentials: ask for them before the document is sent.
                WriteResponse(ns, 401, "Unauthorized", "text/plain", Array.Empty<byte>(), false, $"WWW-Authenticate: Basic realm=\"{Defaults.Maker}\"");
                return false;
            }
            var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            ns.Write(cont); ns.Flush();
        }

        byte[] body = reader.ReadBody(req);
        IppMessage ipp;
        int dataOffset;
        try { ipp = IppMessage.Parse(body, out dataOffset); }
        catch (Exception ex)
        {
            Log?.Invoke($"Bad IPP request from {remote}: {ex.Message}");
            WriteResponse(ns, 400, "Bad Request", "text/plain", Array.Empty<byte>(), false);
            return false;
        }

        if (printer == null)
        {
            // try the printer-uri attribute
            var uri = ipp.Find("printer-uri")?.FirstString;
            if (uri != null && Uri.TryCreate(uri, UriKind.Absolute, out var u))
                lock (_gate) _printers.TryGetValue(u.AbsolutePath.TrimStart('/'), out printer);
        }

        bool needsAuth = ipp.Code is IppOp.PrintJob or IppOp.ValidateJob or IppOp.CreateJob or IppOp.SendDocument or IppOp.CancelJob;
        if (printer != null && printer.Secured && needsAuth && !Authorized(req, printer))
        {
            WriteResponse(ns, 401, "Unauthorized", "text/plain", Array.Empty<byte>(), false, $"WWW-Authenticate: Basic realm=\"{Defaults.Maker}\"");
            return false;
        }

        var resp = Dispatch(ipp, body, dataOffset, printer, req, remote);
        WriteResponse(ns, 200, "OK", "application/ipp", resp.ToBytes(), keepAlive);
        return keepAlive;
    }

    static bool Authorized(HttpRequest req, PrinterEntry p)
    {
        var h = req.Header("Authorization");
        if (h == null || !h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var s = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..].Trim()));
            int i = s.IndexOf(':');
            return i > 0 && s[..i] == p.UserName && s[(i + 1)..] == p.Password;
        }
        catch { return false; }
    }

    static void WriteResponse(Stream ns, int code, string reason, string? type, byte[] body, bool keepAlive, string? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\n");
        sb.Append($"Date: {DateTime.UtcNow:R}\r\n");
        sb.Append("Server: Oplus/1.0 IPP/1.1\r\n");
        if (type != null) sb.Append($"Content-Type: {type}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append(keepAlive ? "Connection: Keep-Alive\r\nKeep-Alive: timeout=10\r\n" : "Connection: close\r\n");
        if (extra != null) sb.Append(extra).Append("\r\n");
        sb.Append("\r\n");
        ns.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        ns.Write(body);
        ns.Flush();
    }

    byte[] StatusPage()
    {
        var sb = new StringBuilder("<!DOCTYPE html><html><head><title>Oplus</title></head><body style=\"font-family:Segoe UI,Arial\"><h1>Oplus Print Share</h1><ul>");
        lock (_gate) foreach (var p in _printers.Values) sb.Append($"<li>{WebUtility.HtmlEncode(p.Alias)} &ndash; {WebUtility.HtmlEncode(p.Location)}</li>");
        sb.Append("</ul></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------ IPP operations
    IppMessage Dispatch(IppMessage req, byte[] body, int dataOffset, PrinterEntry? printer, HttpRequest http, string remote)
    {
        var resp = new IppMessage { Major = req.Major == 2 ? (byte)2 : (byte)1, Minor = req.Major == 2 ? (byte)0 : (byte)1, RequestId = req.RequestId };
        var op = resp.NewGroup(IppTag.OperationGroup);
        op.Add("attributes-charset", IppValue.Str(IppTag.Charset, "utf-8"));
        op.Add("attributes-natural-language", IppValue.Str(IppTag.Language, "en-us"));

        void Status(short code, string message) { resp.Code = code; op.Text("status-message", message); }

        if (req.Major != 1 && req.Major != 2) { Status(IppStatus.VersionNotSupported, $"Bad request version number {req.Major}.{req.Minor}."); return resp; }
        if (req.RequestId <= 0) { Status(IppStatus.BadRequest, $"Bad request-id {req.RequestId}."); return resp; }
        if (printer == null) { Status(IppStatus.NotFound, "Printer not found."); return resp; }
        string host = (http.Header("Host") ?? HostName).Split(':')[0].Trim('[', ']');
        if (host.Length == 0) host = HostName;

        switch (req.Code)
        {
            case IppOp.GetPrinterAttributes:
                Status(IppStatus.Ok, "successful-ok");
                AddPrinterAttributes(resp.NewGroup(IppTag.PrinterGroup), printer, host, Requested(req));
                break;
            case IppOp.ValidateJob:
                {
                    var err = CheckFormat(req, printer);
                    if (err != null) Status(IppStatus.DocumentFormatNotSupported, err);
                    else Status(IppStatus.Ok, "successful-ok");
                }
                break;
            case IppOp.PrintJob:
                {
                    var err = CheckFormat(req, printer);
                    if (err != null) { Status(IppStatus.DocumentFormatNotSupported, err); break; }
                    if (dataOffset >= body.Length) { Status(IppStatus.BadRequest, "No file in request."); break; }
                    var job = NewJob(req, printer, remote);
                    job.Bytes = body.Length - dataOffset;
                    try
                    {
                        File.WriteAllBytes(job.FilePath, body.AsSpan(dataOffset).ToArray());
                    }
                    catch (Exception ex) { Status(IppStatus.InternalError, "Unable to write print file: " + ex.Message); break; }
                    lock (_gate) _jobs.Add(job);
                    _queue.Add(job);
                    JobsChanged?.Invoke();
                    Status(IppStatus.Ok, "successful-ok");
                    AddJobAttributes(resp.NewGroup(IppTag.JobGroup), job, host, null);
                }
                break;
            case IppOp.CancelJob:
                {
                    var job = FindJob(req);
                    if (job == null) { Status(IppStatus.NotFound, "Job does not exist."); break; }
                    if (job.IsDone) { Status(IppStatus.NotPossible, $"Job #{job.Id} is already {job.State.ToString().ToLowerInvariant()} - can't cancel."); break; }
                    CancelJob(job.Id);
                    Status(IppStatus.Ok, "successful-ok");
                }
                break;
            case IppOp.GetJobAttributes:
                {
                    var job = FindJob(req);
                    if (job == null) { Status(IppStatus.NotFound, "Job not found."); break; }
                    Status(IppStatus.Ok, "successful-ok");
                    AddJobAttributes(resp.NewGroup(IppTag.JobGroup), job, host, Requested(req));
                }
                break;
            case IppOp.GetJobs:
                {
                    string which = req.Find("which-jobs")?.FirstString ?? "not-completed";
                    int limit = req.Find("limit")?.FirstInt ?? int.MaxValue;
                    if (which != "completed" && which != "not-completed" && which != "all")
                    { Status(IppStatus.AttributesOrValues, $"The which-jobs value \"{which}\" is not supported."); break; }
                    Status(IppStatus.Ok, "successful-ok");
                    var req2 = Requested(req) ?? new HashSet<string> { "job-id", "job-uri" };
                    List<PrintJob> list;
                    lock (_gate) list = _jobs.Where(j => j.Printer.Key == printer.Key).OrderByDescending(j => j.Id).ToList();
                    foreach (var j in list.Where(j => which == "all" || (which == "completed" ? j.IsDone : !j.IsDone)).Take(limit))
                        AddJobAttributes(resp.NewGroup(IppTag.JobGroup), j, host, req2);
                }
                break;
            case IppOp.IdentifyPrinter:
                Status(IppStatus.Ok, "successful-ok");
                break;
            default:
                Status(IppStatus.OperationNotSupported, "Operation not supported.");
                break;
        }
        return resp;
    }

    PrintJob? FindJob(IppMessage req)
    {
        int? id = req.Find("job-id")?.FirstInt;
        if (id == null)
        {
            var uri = req.Find("job-uri")?.FirstString;
            if (uri != null && int.TryParse(uri.Split('/').Last(), out var v)) id = v;
        }
        lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id);
    }

    static HashSet<string>? Requested(IppMessage req)
    {
        var a = req.Find("requested-attributes");
        if (a == null) return null;
        var set = a.Values.Select(v => v.AsString).ToHashSet();
        if (set.Contains("all") || set.Contains("printer-description") || set.Contains("job-template") || set.Contains("job-description")) return null;
        return set;
    }

    string? CheckFormat(IppMessage req, PrinterEntry p)
    {
        var fmt = req.Find("document-format")?.FirstString;
        if (fmt == null || fmt == "application/octet-stream") return null;
        var ok = (p.IsVirtual ? Defaults.FolderFormats : Defaults.RealFormats).Split(',');
        return ok.Contains(fmt) || fmt == "image/png" ? null : $"Unsupported document-format \"{fmt}\".";
    }

    PrintJob NewJob(IppMessage req, PrinterEntry printer, string remote)
    {
        int id; lock (_gate) id = _nextJobId++;
        var spool = Path.Combine(SettingsStore.DataFolder, "spool");
        Directory.CreateDirectory(spool);
        var job = new PrintJob
        {
            Id = id,
            Printer = printer,
            Name = req.Find("job-name")?.FirstString ?? req.Find("document-name")?.FirstString ?? "Untitled",
            User = req.Find("requesting-user-name")?.FirstString ?? "anonymous",
            Format = req.Find("document-format")?.FirstString ?? "application/octet-stream",
            ClientIp = remote,
            Mac = MacOf(remote),
        };
        job.FilePath = Path.Combine(spool, $"{id}.prn");
        var jg = req.Group(IppTag.JobGroup);
        if (jg?["copies"]?.FirstInt is int c && c > 0) job.Copies = Math.Min(c, 999);
        if (jg?["sides"]?.FirstString is string sides) job.TwoSided = sides.StartsWith("two-sided");
        return job;
    }

    void AddJobAttributes(IppGroup g, PrintJob j, string host, HashSet<string>? want)
    {
        bool W(string n) => want == null || want.Contains(n);
        string printerUri = $"ipp://{host}:{Port}/{j.Printer.Resource}";
        if (W("job-id")) g.Integer("job-id", j.Id);
        if (W("job-uri")) g.Uri("job-uri", $"{printerUri}/{j.Id}");
        if (W("job-printer-uri")) g.Uri("job-printer-uri", printerUri);
        if (W("job-name")) g.NameAttr("job-name", j.Name);
        if (W("job-originating-user-name")) g.NameAttr("job-originating-user-name", j.User);
        if (W("job-state")) g.Enum("job-state", (int)j.State);
        if (W("job-state-reasons")) g.Keyword("job-state-reasons", j.State switch
        {
            JobState.Pending => "none",
            JobState.Processing => "job-printing",
            JobState.Completed => "job-completed-successfully",
            JobState.Canceled => "job-canceled-by-user",
            JobState.Aborted => "aborted-by-system",
            _ => "none",
        });
        if (W("job-state-message")) g.Text("job-state-message", j.State.ToString());
        if (W("job-printer-up-time")) g.Integer("job-printer-up-time", UpTime);
        if (W("time-at-creation")) g.Integer("time-at-creation", UpTimeAt(j.Created));
        if (W("time-at-processing")) { if (j.Started is DateTime s) g.Integer("time-at-processing", UpTimeAt(s)); else g.Add("time-at-processing"); }
        if (W("time-at-completed")) { if (j.Finished is DateTime f) g.Integer("time-at-completed", UpTimeAt(f)); else g.Add("time-at-completed"); }
        if (W("job-k-octets")) g.Integer("job-k-octets", (int)((j.Bytes + 1023) / 1024));
        if (W("job-impressions-completed")) g.Integer("job-impressions-completed", j.Pages);
        if (W("number-of-documents")) g.Integer("number-of-documents", 1);
        if (W("copies")) g.Integer("copies", j.Copies);
    }

    int UpTime => (int)(DateTime.UtcNow - _startTime).TotalSeconds + 1;
    int UpTimeAt(DateTime local) => Math.Max(1, (int)(local.ToUniversalTime() - _startTime).TotalSeconds + 1);

    static readonly (string name, int x, int y)[] Media =
    {
        ("iso_a4_210x297mm", 21000, 29700), ("iso_a5_148x210mm", 14800, 21000), ("iso_a6_105x148mm", 10500, 14800),
        ("iso_dl_110x220mm", 11000, 22000), ("na_legal_8.5x14in", 21590, 35560), ("na_letter_8.5x11in", 21590, 27940),
        ("na_number-10_4.125x9.5in", 10477, 24130), ("na_index-3x5_3x5in", 7620, 12700), ("oe_photo-l_3.5x5in", 8890, 12700),
        ("na_index-4x6_4x6in", 10160, 15240), ("na_5x7_5x7in", 12700, 17780),
    };

    static IppValue MediaCol((string name, int x, int y) m, string type = "auto") => IppValue.Collection(
        new IppAttribute("media-size", IppValue.Collection(
            new IppAttribute("x-dimension", IppValue.Int(IppTag.Integer, m.x)),
            new IppAttribute("y-dimension", IppValue.Int(IppTag.Integer, m.y)))),
        new IppAttribute("media-top-margin", IppValue.Int(IppTag.Integer, 0)),
        new IppAttribute("media-bottom-margin", IppValue.Int(IppTag.Integer, 0)),
        new IppAttribute("media-left-margin", IppValue.Int(IppTag.Integer, 0)),
        new IppAttribute("media-right-margin", IppValue.Int(IppTag.Integer, 0)),
        new IppAttribute("media-source", IppValue.Str(IppTag.Keyword, "auto")),
        new IppAttribute("media-type", IppValue.Str(IppTag.Keyword, type)));

    public static string DefaultMedia => RegionInfo.CurrentRegion.IsMetric ? "iso_a4_210x297mm" : "na_letter_8.5x11in";

    void AddPrinterAttributes(IppGroup g, PrinterEntry p, string host, HashSet<string>? want)
    {
        var all = new IppGroup(IppTag.PrinterGroup);
        string formats = p.IsVirtual ? Defaults.FolderFormats : Defaults.RealFormats;
        string uri = $"ipp://{host}:{Port}/{p.Resource}";
        var def = Media.First(m => m.name == DefaultMedia);
        bool secured = p.Secured;

        all.Add("charset-configured", IppValue.Str(IppTag.Charset, "utf-8"));
        all.Add("charset-supported", IppValue.Str(IppTag.Charset, "utf-8"), IppValue.Str(IppTag.Charset, "us-ascii"));
        all.Bool("color-supported", p.Color);
        all.Keyword("compression-supported", "none");
        all.Integer("copies-default", 1);
        all.Add("copies-supported", IppValue.RangeOf(1, 99));
        all.Mime("document-format-default", "application/octet-stream");
        all.Mime("document-format-supported", formats.Split(',').Append("application/octet-stream").ToArray());
        all.Enum("finishings-default", 3);
        all.Enum("finishings-supported", 3);
        all.Add("generated-natural-language-supported", IppValue.Str(IppTag.Language, "en-us"));
        all.Keyword("ipp-versions-supported", "1.0", "1.1", "2.0");
        all.Keyword("job-creation-attributes-supported", "copies", "media", "media-col", "sides", "print-quality", "printer-resolution", "orientation-requested", "output-bin");
        all.Keyword("job-sheets-default", "none");
        all.Keyword("job-sheets-supported", "none");
        all.Add("job-priority-default", IppValue.Int(IppTag.Integer, 50));
        all.Add("job-priority-supported", IppValue.Int(IppTag.Integer, 100));
        all.Add("jpeg-k-octets-supported", IppValue.RangeOf(0, 16384));
        all.Add("media-bottom-margin-supported", IppValue.Int(IppTag.Integer, 0));
        all.Add("media-left-margin-supported", IppValue.Int(IppTag.Integer, 0));
        all.Add("media-right-margin-supported", IppValue.Int(IppTag.Integer, 0));
        all.Add("media-top-margin-supported", IppValue.Int(IppTag.Integer, 0));
        all.Add("media-col-default", MediaCol(def));
        all.Add("media-col-ready", MediaCol(def));
        all.Add("media-col-database", Media.Select(m => MediaCol(m)).ToArray());
        all.Keyword("media-col-supported", "media-size", "media-top-margin", "media-bottom-margin", "media-left-margin", "media-right-margin", "media-source", "media-type");
        all.Keyword("media-default", def.name);
        all.Keyword("media-ready", def.name);
        all.Keyword("media-supported", Media.Select(m => m.name).ToArray());
        all.Add("media-size-supported", Media.Select(m => IppValue.Collection(
            new IppAttribute("x-dimension", IppValue.Int(IppTag.Integer, m.x)),
            new IppAttribute("y-dimension", IppValue.Int(IppTag.Integer, m.y)))).ToArray());
        all.Keyword("media-source-supported", "auto");
        all.Keyword("media-type-supported", "auto", "stationery", "photographic", "photographic-glossy", "photographic-matte", "envelope", "cardstock", "labels", "transparency");
        all.Bool("multiple-document-jobs-supported", false);
        all.Keyword("multiple-document-handling-supported", "separate-documents-uncollated-copies", "separate-documents-collated-copies");
        all.Add("natural-language-configured", IppValue.Str(IppTag.Language, "en-us"));
        all.Integer("number-up-default", 1);
        all.Integer("number-up-supported", 1);
        all.Enum("operations-supported", IppOp.PrintJob, IppOp.ValidateJob, IppOp.CancelJob, IppOp.GetJobAttributes, IppOp.GetJobs, IppOp.GetPrinterAttributes, IppOp.IdentifyPrinter);
        all.Enum("orientation-requested-default", 3);
        all.Enum("orientation-requested-supported", 3, 4);
        all.Keyword("output-bin-default", "face-down");
        all.Keyword("output-bin-supported", "face-down");
        all.Integer("pages-per-minute", 8);
        if (p.Color) all.Integer("pages-per-minute-color", 4);
        all.Keyword("pdl-override-supported", "attempted");
        all.Enum("print-quality-default", 4);
        all.Enum("print-quality-supported", 3, 4, 5);
        all.Text("printer-info", p.Alias);
        all.Text("printer-location", p.Location);
        all.Text("printer-make-and-model", $"{Defaults.Maker} {Defaults.Model}");
        all.NameAttr("printer-name", p.Alias);
        all.Text("printer-device-id", $"MFG:{Defaults.Maker};MDL:{Defaults.Model};CMD:URF,JPEG{(p.IsVirtual ? ",PDF" : "")};");
        all.Uri("printer-icons", $"http://{host}:{Port}/icon.png");
        all.Uri("printer-more-info", $"http://{host}:{Port}/");
        all.Uri("printer-supply-info-uri", $"http://{host}:{Port}/");
        all.Bool("printer-is-accepting-jobs", true);
        bool busy = CurrentJob?.Printer.Key == p.Key;
        all.Enum("printer-state", busy ? 4 : 3);
        all.Text("printer-state-message", busy ? "Printing" : "Idle");
        all.Keyword("printer-state-reasons", "none");
        all.Integer("printer-up-time", UpTime);
        all.Add("printer-current-time", IppValue.Date(DateTime.Now));
        all.Integer("queued-job-count", Jobs.Count(j => j.Printer.Key == p.Key && !j.IsDone));
        all.Add("printer-resolution-default", IppValue.Res(p.Dpi, p.Dpi));
        all.Add("printer-resolution-supported", IppValue.Res(p.Dpi, p.Dpi));
        all.Uri("printer-uri-supported", uri);
        all.Uri("printer-uuid", "urn:uuid:" + UuidFor(p));
        all.Keyword("printer-kind", "document", "photo");
        all.Keyword("sides-default", "one-sided");
        all.Keyword("sides-supported", p.Duplex ? new[] { "one-sided", "two-sided-long-edge", "two-sided-short-edge" } : new[] { "one-sided" });
        all.Keyword("uri-authentication-supported", secured ? "basic" : "none");
        all.Keyword("uri-security-supported", "none");
        all.Keyword("urf-supported", UrfKeywords(p));
        all.Keyword("which-jobs-supported", "completed", "not-completed", "all");
        all.Keyword("print-color-mode-default", p.Color ? "color" : "monochrome");
        all.Keyword("print-color-mode-supported", p.Color ? new[] { "auto", "color", "monochrome" } : new[] { "auto", "monochrome" });
        all.Keyword("print-scaling-default", "auto");
        all.Keyword("print-scaling-supported", "auto", "fit", "fill", "none");
        all.Keyword("job-hold-until-default", "no-hold");
        all.Keyword("job-hold-until-supported", "no-hold");
        all.Keyword("identify-actions-default", "sound");
        all.Keyword("identify-actions-supported", "sound");
        all.Keyword("multiple-operation-time-out-action", "abort-job");
        all.Integer("multiple-operation-time-out", 60);

        foreach (var a in all.Attributes)
            if (want == null || want.Contains(a.Name)) g.Attributes.Add(a);
    }

    public static string[] UrfKeywords(PrinterEntry p)
    {
        var list = new List<string> { "V1.4" };
        list.AddRange(Defaults.UrfCaps.Split(','));
        list.Add("RS" + p.Dpi);
        if (p.Color) list.Add("SRGB24");
        list.Add("W8");
        return list.ToArray();
    }

    // ------------------------------------------------------------------ job worker
    void WorkLoop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            if (!_running) break;
            if (job.IsDone) { TryDelete(job.FilePath); continue; }
            job.State = JobState.Processing; job.Started = DateTime.Now;
            JobsChanged?.Invoke();
            var p = job.Printer;
            try
            {
                if (p.IsVirtual)
                {
                    var file = PrintEngine.SaveToFolder(job);
                    job.Pages = 1;
                    JobLog.Write(p.Kind == PrinterKind.Dropbox ? "Drop box" : "Folder", p.Alias, job.User, job.Mac, file, 1);
                }
                else
                {
                    job.Pages = PrintEngine.PrintToPrinter(job, job.Cancel.Token);
                    JobLog.Write("Printing", p.Alias, job.User, job.Mac, job.Name, job.Pages);
                }
                job.State = JobState.Completed;
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Canceled;
                JobLog.Write("Printing warning! Canceled job", p.Alias, job.User, job.Mac, job.Name, 0);
            }
            catch (PrinterRemovedException)
            {
                job.State = JobState.Aborted;
                JobLog.Write("Printing error! Printer removed", p.Alias, job.User, job.Mac, job.Name, 0);
                JobFailed?.Invoke(job, "Printer removed");
            }
            catch (Exception ex)
            {
                job.State = JobState.Aborted;
                JobLog.Write("Printing error! " + ex.Message, p.Alias, job.User, job.Mac, job.Name, 0);
                Log?.Invoke($"Job #{job.Id} failed: {ex.Message}");
                JobFailed?.Invoke(job, ex.Message);
            }
            job.Finished = DateTime.Now;
            TryDelete(job.FilePath);
            lock (_gate)
            {
                // keep a short history
                while (_jobs.Count(j => j.IsDone) > 100) _jobs.Remove(_jobs.First(j => j.IsDone));
            }
            JobsChanged?.Invoke();
        }
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    // ------------------------------------------------------------------ MAC address of the phone
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physicalAddrLen);

    static string MacOf(string ip)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return "";
            var mac = new byte[6]; int len = 6;
            if (SendARP(BitConverter.ToUInt32(a.GetAddressBytes(), 0), 0, mac, ref len) != 0) return "";
            return string.Join("-", mac.Take(len).Select(b => b.ToString("x2")));
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _running = false;
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
        _listeners.Clear();
        _queue.CompleteAdding();
    }
}

// ---------------------------------------------------------------------- tiny HTTP/1.1 reader
public sealed class HttpRequest
{
    public string Method = "", Path = "", Version = "";
    public readonly List<KeyValuePair<string, string>> Headers = new();
    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public long ContentLength => long.TryParse(Header("Content-Length"), out var n) ? n : (IsChunked ? 1 : 0);
    public bool IsChunked => Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class HttpReader
{
    readonly Stream _s;
    readonly byte[] _buf = new byte[65536];
    int _pos, _len;
    const long MaxBody = 1L << 30;

    public HttpReader(Stream s) { _s = s; }

    int Fill() { _pos = 0; _len = _s.Read(_buf, 0, _buf.Length); return _len; }

    int ReadByte()
    {
        if (_pos >= _len && Fill() <= 0) return -1;
        return _buf[_pos++];
    }

    string? ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = ReadByte();
            if (b < 0) return sb.Length == 0 ? null : sb.ToString();
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
            if (sb.Length > 16384) throw new IOException("Header line too long");
        }
    }

    public HttpRequest? ReadRequest()
    {
        string? line;
        do { line = ReadLine(); if (line == null) return null; } while (line.Length == 0);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) throw new IOException("Bad request line.");
        var req = new HttpRequest { Method = parts[0].ToUpperInvariant(), Path = parts[1], Version = parts[2] };
        if (Uri.TryCreate(req.Path, UriKind.Absolute, out var abs)) req.Path = abs.AbsolutePath;
        while ((line = ReadLine()) != null && line.Length > 0)
        {
            int i = line.IndexOf(':');
            if (i > 0) req.Headers.Add(new(line[..i].Trim(), line[(i + 1)..].Trim()));
        }
        return req;
    }

    public byte[] ReadBody(HttpRequest req)
    {
        var ms = new MemoryStream();
        if (req.IsChunked)
        {
            while (true)
            {
                var sizeLine = ReadLine() ?? throw new IOException("Unexpected end of chunked body");
                if (sizeLine.Length == 0) continue;
                long size = Convert.ToInt64(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0) { while (!string.IsNullOrEmpty(ReadLine())) { } break; }
                Copy(ms, size);
                ReadLine(); // CRLF after chunk
            }
        }
        else if (long.TryParse(req.Header("Content-Length"), out var n) && n > 0) Copy(ms, n);
        return ms.ToArray();
    }

    void Copy(MemoryStream ms, long n)
    {
        if (ms.Length + n > MaxBody) throw new IOException("Request too large");
        while (n > 0)
        {
            if (_pos >= _len && Fill() <= 0) throw new IOException("Connection closed");
            int take = (int)Math.Min(n, _len - _pos);
            ms.Write(_buf, _pos, take);
            _pos += take; n -= take;
        }
    }
}
