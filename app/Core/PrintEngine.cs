using System.Drawing;
using System.Drawing.Printing;

namespace Oplus.Core;

/// <summary>Turns a received job into paper (Windows printer) or a file (folder printer).</summary>
public static class PrintEngine
{
    /// <summary>Prints the job file; returns the number of sheets/pages logged.</summary>
    public static int PrintToPrinter(PrintJob job, CancellationToken ct)
    {
        var printer = job.Printer;
        var ps = new PrinterSettings { PrinterName = printer.RealPrinterName };
        if (!ps.IsValid) throw new PrinterRemovedException();

        // Load all pages (URF or JPEG) as bitmaps.
        var pages = LoadPages(job).ToList();
        try
        {
            if (pages.Count == 0) throw new InvalidDataException("No pages in the job.");
            bool wantDuplex = job.TwoSided || pages.Any(p => p.duplex > 1);
            bool duplex = wantDuplex && ps.CanDuplex;

            using var doc = new PrintDocument { PrinterSettings = ps, DocumentName = job.Name };
            doc.PrintController = new StandardPrintController(); // no progress dialog
            ps.Copies = (short)Math.Clamp(job.Copies, 1, 999);
            ps.Collate = true;
            // DMDUP_HORIZONTAL (3) when two sides were asked for and the driver can
            ps.Duplex = duplex ? System.Drawing.Printing.Duplex.Horizontal : System.Drawing.Printing.Duplex.Simplex;

            int index = 0;
            doc.QueryPageSettings += (_, e) =>
            {
                if (index < pages.Count)
                {
                    var img = pages[index].image;
                    // Landscape when the page is wider than tall.
                    e.PageSettings.Landscape = img.Width > img.Height;
                }
            };
            doc.PrintPage += (_, e) =>
            {
                if (ct.IsCancellationRequested) { e.Cancel = true; return; }
                var img = pages[index].image;
                var g = e.Graphics!;
                // Stretch the page over the whole printable area (StretchBlt 0,0,HORZRES,VERTRES).
                var area = g.VisibleClipBounds;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, area);
                index++;
                e.HasMorePages = index < pages.Count;
            };
            doc.Print();
            if (ct.IsCancellationRequested) throw new OperationCanceledException();

            // simplex: pages x copies; duplex: ceil(pages/2) x copies
            return duplex ? (pages.Count + 1) / 2 * job.Copies : pages.Count * job.Copies;
        }
        finally
        {
            foreach (var p in pages) p.image.Dispose();
        }
    }

    static IEnumerable<(Bitmap image, int dpi, int duplex)> LoadPages(PrintJob job)
    {
        using var fs = File.OpenRead(job.FilePath);
        var head = new byte[8];
        int n = fs.Read(head, 0, 8);
        fs.Position = 0;
        if (UrfDecoder.IsUrf(head.AsSpan(0, n)))
        {
            foreach (var p in UrfDecoder.Pages(fs)) yield return (p.Image, p.Dpi, p.Duplex);
        }
        else
        {
            // JPEG / PNG: one picture
            Bitmap img;
            using (var src = Image.FromStream(fs)) img = new Bitmap(src);
            yield return (img, (int)Math.Round(img.HorizontalResolution), 1);
        }
    }

    /// <summary>
    /// Saves a job into the printer's folder as YYYYMMDD-hhmmss-mmm-&lt;job name&gt;.pdf/.jpg
    ///. URF jobs become a PDF with one picture per page.
    /// </summary>
    public static string SaveToFolder(PrintJob job)
    {
        var folder = job.Printer.Kind == PrinterKind.Dropbox && string.IsNullOrEmpty(job.Printer.FolderPath)
            ? SettingsStore.DropboxFolder : job.Printer.FolderPath;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Shared folder does not exist: " + folder);

        string name = SafeName(Path.GetFileName(job.Name.Replace('/', '\\')));
        if (string.IsNullOrWhiteSpace(name)) name = "document";
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

        string ext;
        bool isUrf;
        using (var fs = File.OpenRead(job.FilePath))
        {
            var head = new byte[8]; int n = fs.Read(head, 0, 8);
            isUrf = UrfDecoder.IsUrf(head.AsSpan(0, n));
            bool isPdf = n >= 4 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F';
            ext = isUrf || isPdf ? ".pdf" : ".jpg";
        }
        string lower = name.ToLowerInvariant();
        bool hasExt = ext == ".jpg" ? (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")) : lower.EndsWith(".pdf");
        string fileName = $"{stamp}-{name}{(hasExt ? "" : ext)}";
        string target = Path.Combine(folder, fileName);

        if (isUrf)
        {
            using var fs = File.OpenRead(job.FilePath);
            var pages = UrfDecoder.Pages(fs).ToList();
            try { PdfWriter.Write(target, pages.Select(p => (p.Image, p.Dpi))); }
            finally { foreach (var p in pages) p.Dispose(); }
        }
        else File.Copy(job.FilePath, target, false);
        return fileName;
    }

    /// <summary>Characters \ / : * | ? " &lt; &gt; become spaces.</summary>
    public static string SafeName(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if ("\\/:*|?\"<>".IndexOf(chars[i]) >= 0 || char.IsControl(chars[i])) chars[i] = ' ';
        return new string(chars).Trim();
    }
}

public sealed class PrinterRemovedException : Exception
{
    public PrinterRemovedException() : base("Printer removed") { }
}
