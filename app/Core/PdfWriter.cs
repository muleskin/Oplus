using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace Oplus.Core;

/// <summary>A tiny PDF 1.3 writer: one JPEG picture per page (/DCTDecode), page size from the picture's dpi.</summary>
public static class PdfWriter
{
    public static void Write(string path, IEnumerable<(Bitmap image, int dpi)> pages)
    {
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); fs.Write(b); }
        Ascii("%PDF-1.3\n%âãÏÓ\n");

        // object numbers: 1 catalog, 2 pages, then per page: page, content, image
        var pageObjs = new List<(int page, int content, int img, byte[] jpg, int w, int h, double ptW, double ptH)>();
        int next = 3;
        foreach (var (image, dpi) in pages)
        {
            using var ms = new MemoryStream();
            image.Save(ms, jpegCodec, ep);
            double d = dpi > 0 ? dpi : 300;
            pageObjs.Add((next, next + 1, next + 2, ms.ToArray(), image.Width, image.Height, image.Width * 72.0 / d, image.Height * 72.0 / d));
            next += 3;
        }

        void Obj(int n, string body)
        {
            while (offsets.Count < n) offsets.Add(0);
            offsets[n - 1] = fs.Position;
            Ascii($"{n} 0 obj\n{body}\nendobj\n");
        }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Kids [{string.Join(" ", pageObjs.Select(p => $"{p.page} 0 R"))}] /Count {pageObjs.Count} >>");
        foreach (var p in pageObjs)
        {
            string w = p.ptW.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string h = p.ptH.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            Obj(p.page, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im{p.img} {p.img} 0 R >> >> /Contents {p.content} 0 R >>");
            string content = $"q {w} 0 0 {h} 0 0 cm /Im{p.img} Do Q";
            Obj(p.content, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            while (offsets.Count < p.img) offsets.Add(0);
            offsets[p.img - 1] = fs.Position;
            Ascii($"{p.img} 0 obj\n<< /Type /XObject /Subtype /Image /Width {p.w} /Height {p.h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.jpg.Length} >>\nstream\n");
            fs.Write(p.jpg);
            Ascii("\nendstream\nendobj\n");
        }
        long xref = fs.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Ascii($"{o:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
