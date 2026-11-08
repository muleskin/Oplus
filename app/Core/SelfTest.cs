using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;

namespace Oplus.Core;

/// <summary>
/// "--selftest &lt;report.txt&gt;": checks the sharing engine on this computer only (loopback): the
/// raster decoder, the print server (printer questions, a raster job and a JPEG job saved to a temp
/// folder) and the network announcer's answers. Writes nothing to the settings.
/// </summary>
public static class SelfTest
{
    public static int Run(string reportPath)
    {
        SettingsStore.ReadOnly = true;
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

        try
        {
            // 1. raster round trip
            var urf = MakeUrf(2, 300, 200, 300);
            using (var ms = new MemoryStream(urf))
            {
                var pages = UrfDecoder.Pages(ms).ToList();
                Check(pages.Count == 2, $"raster decoder reads 2 pages (got {pages.Count})");
                if (pages.Count > 0)
                {
                    var c = pages[0].Image.GetPixel(10, 10);
                    Check(c.R == 255 && c.G == 0 && c.B == 0, $"raster pixel colour red (got {c})");
                    var w = pages[0].Image.GetPixel(250, 150);
                    Check(w.R == 255 && w.G == 255 && w.B == 255, $"raster white fill (got {w})");
                    Check(pages[0].Dpi == 300, "raster dpi 300");
                }
                foreach (var p in pages) p.Dispose();
            }

            // 2. print server on loopback with a folder printer
            var folder = Path.Combine(Path.GetTempPath(), "oplus-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var printer = new PrinterEntry { Key = "test", Alias = "Test Folder", Kind = PrinterKind.Folder, FolderPath = folder, Share = true, Color = true, Duplex = true, Dpi = 600, Location = "Desk" };
            using var srv = new IppServer("test-host.local");
            srv.Start(new[] { 0 }, IPAddress.Loopback);
            srv.SetPrinters(new[] { printer });
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            string url = $"http://127.0.0.1:{srv.Port}/{printer.Resource}";

            var gpa = Request(IppOp.GetPrinterAttributes, 1, url);
            var resp = Post(http, url, gpa.ToBytes(), chunked: false);
            Check(resp.Code == IppStatus.Ok, $"Get-Printer-Attributes status ok (0x{resp.Code:x4})");
            var formats = resp.Find("document-format-supported")?.Values.Select(v => v.AsString).ToList() ?? new();
            Check(formats.Contains("application/pdf") && formats.Contains("image/urf"), "folder printer offers PDF and raster: " + string.Join(",", formats));
            Check(resp.Find("printer-uri-supported")?.FirstString?.EndsWith(printer.Resource) == true, "printer-uri-supported " + resp.Find("printer-uri-supported")?.FirstString);
            Check(resp.Find("urf-supported")?.Values.Any(v => v.AsString == "RS600") == true, "urf-supported has RS600");
            Check(resp.Find("media-col-database")?.Values.Count == 11, "media-col-database has 11 sizes");

            // Print-Job: raster, chunked like iOS sends it
            var pj = Request(IppOp.PrintJob, 2, url);
            pj.Group(IppTag.OperationGroup)!.NameAttr("job-name", "Photo: test").Mime("document-format", "image/urf");
            var body = pj.ToBytes().Concat(urf).ToArray();
            var r2 = Post(http, url, body, chunked: true);
            Check(r2.Code == IppStatus.Ok, $"Print-Job (raster, chunked) accepted (0x{r2.Code:x4})");
            int jobId = r2.Find("job-id")?.FirstInt ?? 0;
            Check(jobId > 0, "job-id returned " + jobId);

            // Print-Job: JPEG
            var pj2 = Request(IppOp.PrintJob, 3, url);
            pj2.Group(IppTag.OperationGroup)!.NameAttr("job-name", "snapshot").Mime("document-format", "image/jpeg");
            var r3 = Post(http, url, pj2.ToBytes().Concat(MakeJpeg()).ToArray(), chunked: false);
            Check(r3.Code == IppStatus.Ok, $"Print-Job (JPEG) accepted (0x{r3.Code:x4})");

            // wait for both jobs
            var until = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < until && srv.Jobs.Any(j => !j.IsDone)) Thread.Sleep(100);
            Check(srv.Jobs.All(j => j.State == JobState.Completed), "jobs completed: " + string.Join(",", srv.Jobs.Select(j => j.State)));
            var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
            log.AppendLine("     files: " + string.Join(" | ", files));
            var pdf = Directory.GetFiles(folder, "*.pdf").FirstOrDefault();
            Check(pdf != null && Encoding.ASCII.GetString(File.ReadAllBytes(pdf).AsSpan(0, 5)) == "%PDF-", "raster job saved as PDF");
            Check(pdf != null && Path.GetFileName(pdf).EndsWith("-Photo  test.pdf"), "PDF file name pattern YYYYMMDD-hhmmss-mmm-<job>.pdf");
            Check(Directory.GetFiles(folder, "*.jpg").Length == 1, "JPEG job saved as .jpg");

            // Get-Jobs
            var gj = Request(IppOp.GetJobs, 4, url);
            gj.Group(IppTag.OperationGroup)!.Keyword("which-jobs", "completed");
            var r4 = Post(http, url, gj.ToBytes(), chunked: false);
            Check(r4.Groups.Count(g => g.Tag == IppTag.JobGroup) == 2, "Get-Jobs lists 2 completed jobs");

            // secured printer asks for a password
            printer.UserName = "me"; printer.Password = "pw";
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Request(IppOp.PrintJob, 5, url).ToBytes().Concat(MakeJpeg()).ToArray()) };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
            var hr = http.Send(req);
            Check(hr.StatusCode == HttpStatusCode.Unauthorized, "secured printer answers 401 without password (" + (int)hr.StatusCode + ")");
            var req2 = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Request(IppOp.ValidateJob, 6, url).ToBytes()) };
            req2.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
            req2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pw")));
            var hr2 = http.Send(req2);
            Check(hr2.StatusCode == HttpStatusCode.OK, "secured printer accepts the right password");

            try { Directory.Delete(folder, true); } catch { }

            // 3. network announcer answers
            var mdns = new MdnsResponder();
            mdns.SetServices(new[] { new MdnsService { Instance = "Test Folder", Port = 631, Txt = new() { new("txtvers", "1"), new("rp", printer.Resource), new("URF", "V1.4,RS600") } } });
            var query = DnsQuery("_universal._sub._ipp._tcp.local", 12);
            var ans = mdns.AnswerPacket(query, new List<IPAddress> { IPAddress.Parse("192.168.1.50") });
            Check(ans != null, "announcer answers the _universal printer question");
            if (ans != null)
            {
                int an = BinaryPrimitives.ReadUInt16BigEndian(ans.AsSpan(6)), ar = BinaryPrimitives.ReadUInt16BigEndian(ans.AsSpan(10));
                Check(an == 1 && ar == 3, $"answer has 1 PTR and 3 extra records SRV/TXT/A (got {an}/{ar})");
                var text = Encoding.UTF8.GetString(ans);
                Check(text.Contains("Test Folder") && text.Contains("URF=V1.4,RS600") && text.Contains("-oplus"), "answer names the printer, its TXT and host");
            }
        }
        catch (Exception ex) { log.AppendLine("FAIL exception: " + ex); failures++; }

        log.AppendLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        File.WriteAllText(reportPath, log.ToString());
        return failures == 0 ? 0 : 1;
    }

    static IppMessage Request(short op, int id, string url)
    {
        var m = new IppMessage { Code = op, RequestId = id };
        var g = m.NewGroup(IppTag.OperationGroup);
        g.Add("attributes-charset", IppValue.Str(IppTag.Charset, "utf-8"));
        g.Add("attributes-natural-language", IppValue.Str(IppTag.Language, "en"));
        g.Uri("printer-uri", url.Replace("http://", "ipp://"));
        g.NameAttr("requesting-user-name", "tester");
        return m;
    }

    sealed class ChunkedContent : HttpContent
    {
        readonly byte[] _b;
        public ChunkedContent(byte[] b) { _b = b; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_b, 0, _b.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    static IppMessage Post(HttpClient http, string url, byte[] body, bool chunked)
    {
        HttpContent c = chunked ? new ChunkedContent(body) : new ByteArrayContent(body);
        c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
        var r = http.PostAsync(url, c).GetAwaiter().GetResult();
        var bytes = r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        return IppMessage.Parse(bytes, out _);
    }

    /// <summary>A small raster document: left half red, right half "rest of line white".</summary>
    static byte[] MakeUrf(int pages, int w, int h, int dpi)
    {
        var ms = new MemoryStream();
        ms.Write("UNIRAST\0"u8);
        var b4 = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b4, (uint)pages); ms.Write(b4);
        for (int p = 0; p < pages; p++)
        {
            var hdr = new byte[32];
            hdr[0] = 24; hdr[1] = 1; hdr[2] = 1; hdr[3] = 4;
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(12), (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16), (uint)h);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20), (uint)dpi);
            ms.Write(hdr);
            int y = 0;
            while (y < h)
            {
                int rep = Math.Min(256, h - y);
                ms.WriteByte((byte)(rep - 1));
                int x = 0, half = w / 2;
                while (x < half) { int n = Math.Min(128, half - x); ms.WriteByte((byte)(n - 1)); ms.Write(new byte[] { 255, 0, 0 }); x += n; }
                ms.WriteByte(0x80);
                y += rep;
            }
        }
        return ms.ToArray();
    }

    static byte[] MakeJpeg()
    {
        using var bmp = new Bitmap(120, 80);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.SkyBlue); g.FillEllipse(Brushes.Orange, 20, 10, 60, 60); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    static byte[] DnsQuery(string name, ushort type)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var l in name.Split('.')) { var b = Encoding.UTF8.GetBytes(l); ms.WriteByte((byte)b.Length); ms.Write(b); }
        ms.WriteByte(0);
        ms.WriteByte((byte)(type >> 8)); ms.WriteByte((byte)type); ms.WriteByte(0); ms.WriteByte(1);
        return ms.ToArray();
    }
}
