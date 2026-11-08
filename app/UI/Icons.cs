using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Oplus;

/// <summary>All pictures are drawn in code: simple shapes.</summary>
public static class Icons
{
    static Bitmap Canvas(int w, int h, out Graphics g)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return b;
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>A printer: grey body, white sheet coming out, a colour stripe when it prints in colour.</summary>
    public static Bitmap Printer(int s, bool color, bool dim = false)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            // paper at the back
            using (var paper = Round(new RectangleF(9 * u, 2 * u, 14 * u, 12 * u), 1.2f * u))
            { g.FillPath(Brushes.White, paper); using var pen = new Pen(Color.FromArgb(150, 150, 160), 0.8f * u); g.DrawPath(pen, paper); }
            // body
            var bodyR = new RectangleF(2 * u, 10 * u, 28 * u, 13 * u);
            using (var body = Round(bodyR, 3 * u))
            {
                using var br = new LinearGradientBrush(bodyR, dim ? Color.FromArgb(200, 200, 200) : Color.FromArgb(120, 130, 145),
                                                        dim ? Color.FromArgb(160, 160, 160) : Color.FromArgb(55, 62, 75), 90f);
                g.FillPath(br, body);
            }
            // output slot + sheet
            using (var slot = new SolidBrush(Color.FromArgb(30, 34, 40))) g.FillRectangle(slot, 6 * u, 20 * u, 20 * u, 2 * u);
            using (var sheet = Round(new RectangleF(8 * u, 21 * u, 16 * u, 9 * u), 1f * u))
            {
                g.FillPath(Brushes.White, sheet);
                using var pen = new Pen(Color.FromArgb(150, 150, 160), 0.8f * u); g.DrawPath(pen, sheet);
            }
            if (color && !dim)
            {
                Color[] cs = { Color.FromArgb(0, 174, 239), Color.FromArgb(236, 0, 140), Color.FromArgb(255, 221, 0) };
                for (int i = 0; i < 3; i++) using (var br = new SolidBrush(cs[i])) g.FillRectangle(br, (10 + i * 4.3f) * u, 24 * u, 3.6f * u, 3.6f * u);
            }
            else using (var pen = new Pen(Color.FromArgb(120, 120, 130), 1f * u))
            { g.DrawLine(pen, 10 * u, 24.5f * u, 22 * u, 24.5f * u); g.DrawLine(pen, 10 * u, 27 * u, 19 * u, 27 * u); }
            // light
            using (var led = new SolidBrush(dim ? Color.Gray : Color.FromArgb(90, 220, 110))) g.FillEllipse(led, 25 * u, 13 * u, 2.5f * u, 2.5f * u);
        }
        return b;
    }

    /// <summary>A yellow folder; with a cloud for the cloud-folder printer.</summary>
    public static Bitmap Folder(int s, bool cloud = false, int badge = 0)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            Color back = cloud ? Color.FromArgb(40, 110, 200) : Color.FromArgb(222, 160, 30);
            Color front1 = cloud ? Color.FromArgb(110, 170, 240) : Color.FromArgb(255, 214, 90);
            Color front2 = cloud ? Color.FromArgb(60, 130, 220) : Color.FromArgb(246, 180, 40);
            using (var tab = new GraphicsPath())
            {
                tab.AddLines(new[] { new PointF(2 * u, 7 * u), new PointF(12 * u, 7 * u), new PointF(15 * u, 10 * u), new PointF(30 * u, 10 * u), new PointF(30 * u, 27 * u), new PointF(2 * u, 27 * u) });
                tab.CloseFigure();
                using var br = new SolidBrush(back); g.FillPath(br, tab);
            }
            var fr = new RectangleF(2 * u, 12 * u, 28 * u, 16 * u);
            using (var front = Round(fr, 1.5f * u))
            using (var br = new LinearGradientBrush(fr, front1, front2, 90f)) g.FillPath(br, front);
            if (cloud)
            {
                using var w = new SolidBrush(Color.White);
                g.FillEllipse(w, 9 * u, 17 * u, 7 * u, 7 * u);
                g.FillEllipse(w, 13 * u, 14.5f * u, 8 * u, 8 * u);
                g.FillEllipse(w, 18 * u, 17.5f * u, 6 * u, 6 * u);
                g.FillRectangle(w, 12 * u, 20 * u, 9 * u, 3.5f * u);
            }
            if (badge != 0)
            {
                var c = badge > 0 ? Color.FromArgb(40, 170, 70) : Color.FromArgb(215, 50, 50);
                using var br = new SolidBrush(c);
                g.FillEllipse(br, 17 * u, 15 * u, 14 * u, 14 * u);
                using var pen = new Pen(Color.White, 2.4f * u);
                g.DrawLine(pen, 20 * u, 22 * u, 28 * u, 22 * u);
                if (badge > 0) g.DrawLine(pen, 24 * u, 18 * u, 24 * u, 26 * u);
            }
        }
        return b;
    }

    public static Bitmap Dot(int s, Color c)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float d = s * 0.42f, o = (s - d) / 2;
            using var br = new LinearGradientBrush(new RectangleF(o, o, d, d), ControlPaint.Light(c, 0.6f), c, 60f);
            g.FillEllipse(br, o, o, d, d);
            using var pen = new Pen(ControlPaint.Dark(c, 0.1f), 1f); g.DrawEllipse(pen, o, o, d, d);
        }
        return b;
    }

    public static Bitmap Lock(int s, bool open = false)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            using (var pen = new Pen(Color.FromArgb(120, 120, 130), 3f * u))
            {
                if (open) g.DrawArc(pen, 6 * u, 4 * u, 12 * u, 14 * u, 180, 180);
                else g.DrawArc(pen, 10 * u, 4 * u, 12 * u, 14 * u, 180, 180);
                if (!open) { g.DrawLine(pen, 10 * u, 11 * u, 10 * u, 15 * u); g.DrawLine(pen, 22 * u, 11 * u, 22 * u, 15 * u); }
            }
            var r = new RectangleF(7 * u, 14 * u, 18 * u, 15 * u);
            using (var body = Round(r, 2.5f * u))
            using (var br = new LinearGradientBrush(r, Color.FromArgb(255, 205, 60), Color.FromArgb(225, 150, 10), 90f)) g.FillPath(br, body);
            using (var hole = new SolidBrush(Color.FromArgb(120, 80, 0))) { g.FillEllipse(hole, 14 * u, 18 * u, 4 * u, 4 * u); g.FillRectangle(hole, 15.2f * u, 21 * u, 1.6f * u, 4 * u); }
        }
        return b;
    }

    public static Bitmap ColorWheel(int s, bool on)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            Color[] cs = { Color.FromArgb(0, 174, 239), Color.FromArgb(236, 0, 140), Color.FromArgb(255, 221, 0), Color.FromArgb(40, 40, 40) };
            for (int i = 0; i < 4; i++)
            {
                var c = on ? cs[i] : Color.FromArgb(150 + i * 20, 150 + i * 20, 150 + i * 20);
                using var br = new SolidBrush(c);
                g.FillPie(br, 4 * u, 4 * u, 24 * u, 24 * u, i * 90 - 90, 90);
            }
            g.FillEllipse(Brushes.White, 11 * u, 11 * u, 10 * u, 10 * u);
        }
        return b;
    }

    public static Bitmap Duplex(int s, bool on)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            var line = on ? Color.FromArgb(60, 90, 150) : Color.FromArgb(160, 160, 160);
            using var pen = new Pen(line, 1.3f * u);
            g.FillRectangle(Brushes.White, 10 * u, 3 * u, 16 * u, 21 * u); g.DrawRectangle(pen, 10 * u, 3 * u, 16 * u, 21 * u);
            g.FillRectangle(Brushes.White, 5 * u, 8 * u, 16 * u, 21 * u); g.DrawRectangle(pen, 5 * u, 8 * u, 16 * u, 21 * u);
            using var f = new Font("Segoe UI", 8.5f * u, FontStyle.Bold, GraphicsUnit.Pixel);
            using var tb = new SolidBrush(line);
            g.DrawString("1|2", f, tb, new RectangleF(5 * u, 12 * u, 16 * u, 14 * u), new StringFormat { Alignment = StringAlignment.Center });
        }
        return b;
    }

    public static Bitmap DpiBadge(int s, int dpi)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            var r = new RectangleF(2 * u, 6 * u, 28 * u, 20 * u);
            using (var path = Round(r, 4 * u))
            using (var br = new LinearGradientBrush(r, Color.FromArgb(90, 110, 140), Color.FromArgb(50, 65, 90), 90f)) g.FillPath(br, path);
            using var f = new Font("Segoe UI", 9f * u, FontStyle.Bold, GraphicsUnit.Pixel);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(dpi.ToString(), f, Brushes.White, new RectangleF(r.X, r.Y - 2 * u, r.Width, r.Height * 0.7f + 2 * u), sf);
            using var f2 = new Font("Segoe UI", 6f * u, GraphicsUnit.Pixel);
            g.DrawString("dpi", f2, Brushes.White, new RectangleF(r.X, r.Y + r.Height * 0.5f, r.Width, r.Height * 0.5f), sf);
        }
        return b;
    }

    public static Bitmap Pencil(int s)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            var st = g.Save();
            g.TranslateTransform(16 * u, 16 * u); g.RotateTransform(45);
            using (var br = new SolidBrush(Color.FromArgb(250, 190, 40))) g.FillRectangle(br, -3.5f * u, -13 * u, 7 * u, 18 * u);
            using (var br = new SolidBrush(Color.FromArgb(230, 120, 140))) g.FillRectangle(br, -3.5f * u, -15 * u, 7 * u, 3 * u);
            using (var br = new SolidBrush(Color.FromArgb(240, 215, 170)))
                g.FillPolygon(br, new[] { new PointF(-3.5f * u, 5 * u), new PointF(3.5f * u, 5 * u), new PointF(0, 12 * u) });
            using (var br = new SolidBrush(Color.FromArgb(50, 50, 50)))
                g.FillPolygon(br, new[] { new PointF(-1.2f * u, 9.3f * u), new PointF(1.2f * u, 9.3f * u), new PointF(0, 12 * u) });
            g.Restore(st);
        }
        return b;
    }

    public static Bitmap Check(int s, Color? c = null)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            using var pen = new Pen(c ?? Color.FromArgb(30, 160, 60), 4.5f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(6 * u, 17 * u), new PointF(13 * u, 24 * u), new PointF(26 * u, 8 * u) });
        }
        return b;
    }

    public static Bitmap Cross(int s)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            using var pen = new Pen(Color.FromArgb(210, 50, 50), 4.5f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 8 * u, 8 * u, 24 * u, 24 * u); g.DrawLine(pen, 24 * u, 8 * u, 8 * u, 24 * u);
        }
        return b;
    }

    /// <summary>Sharing on/off: radio waves above a dot.</summary>
    public static Bitmap ShareWaves(int s, bool on)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            var c = on ? Color.FromArgb(30, 150, 60) : Color.FromArgb(150, 150, 150);
            using var pen = new Pen(c, 2.6f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 3; i++)
            {
                float r = 6 * u + i * 6 * u;
                g.DrawArc(pen, 16 * u - r, 24 * u - r, r * 2, r * 2, 225, 90);
            }
            using var br = new SolidBrush(c); g.FillEllipse(br, 13 * u, 21 * u, 6 * u, 6 * u);
            if (!on) { using var red = new Pen(Color.FromArgb(210, 50, 50), 3f * u); g.DrawLine(red, 5 * u, 5 * u, 27 * u, 27 * u); }
        }
        return b;
    }

    public static Bitmap Gear(int s)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            var c = Color.FromArgb(90, 100, 115);
            using var br = new SolidBrush(c);
            var st = g.Save(); g.TranslateTransform(16 * u, 16 * u);
            for (int i = 0; i < 8; i++) { g.FillRectangle(br, -2.5f * u, -14 * u, 5 * u, 6 * u); g.RotateTransform(45); }
            g.Restore(st);
            g.FillEllipse(br, 6 * u, 6 * u, 20 * u, 20 * u);
            g.FillEllipse(Brushes.White, 12 * u, 12 * u, 8 * u, 8 * u);
        }
        return b;
    }

    public static Bitmap Mail(int s, bool on)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            var r = new RectangleF(3 * u, 7 * u, 26 * u, 19 * u);
            using (var br = new LinearGradientBrush(r, on ? Color.FromArgb(255, 255, 255) : Color.FromArgb(235, 235, 235), on ? Color.FromArgb(215, 230, 250) : Color.FromArgb(200, 200, 200), 90f)) g.FillRectangle(br, r);
            using var pen = new Pen(on ? Color.FromArgb(50, 100, 180) : Color.Gray, 1.6f * u) { LineJoin = LineJoin.Round };
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            g.DrawLines(pen, new[] { new PointF(r.X, r.Y), new PointF(16 * u, 18 * u), new PointF(r.Right, r.Y) });
        }
        return b;
    }

    public static Bitmap Document(int s)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            float u = s / 32f;
            using var pen = new Pen(Color.FromArgb(90, 100, 120), 1.4f * u);
            var pts = new[] { new PointF(7 * u, 3 * u), new PointF(20 * u, 3 * u), new PointF(26 * u, 9 * u), new PointF(26 * u, 29 * u), new PointF(7 * u, 29 * u) };
            g.FillPolygon(Brushes.White, pts); g.DrawPolygon(pen, pts);
            for (int i = 0; i < 5; i++) g.DrawLine(pen, 10 * u, (13 + i * 3.2f) * u, 23 * u, (13 + i * 3.2f) * u);
        }
        return b;
    }

    /// <summary>The program's own mark: a rounded blue tile with a printer.</summary>
    public static Bitmap AppMark(int s)
    {
        var b = Canvas(s, s, out var g);
        using (g)
        {
            var r = new RectangleF(0.5f, 0.5f, s - 1, s - 1);
            using (var path = Round(r, s * 0.22f))
            using (var br = new LinearGradientBrush(r, Color.FromArgb(70, 160, 230), Color.FromArgb(25, 90, 170), 90f)) g.FillPath(br, path);
            using var p = Printer((int)(s * 0.78f), true);
            g.DrawImage(p, s * 0.11f, s * 0.13f);
        }
        return b;
    }

    static Icon? _appIcon;
    public static Icon AppIcon
    {
        get
        {
            if (_appIcon != null) return _appIcon;
            using var bmp = AppMark(32);
            _appIcon = Icon.FromHandle(bmp.GetHicon());
            return _appIcon;
        }
    }

    /// <summary>The header: own mark plus the program name.</summary>
    public static Bitmap Logo(int w, int h)
    {
        var b = Canvas(w, h, out var g);
        using (g)
        {
            using var mark = AppMark(h - 8);
            g.DrawImage(mark, 4, 4);
            using var f = new Font("Segoe UI", h * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var f2 = new Font("Segoe UI", h * 0.2f, FontStyle.Italic, GraphicsUnit.Pixel);
            using var tb = new SolidBrush(Color.FromArgb(35, 45, 60));
            using var tb2 = new SolidBrush(Color.FromArgb(40, 150, 70));
            g.DrawString("Oplus", f, tb, h + 2, h * 0.02f);
            g.DrawString("Print from iPhone & iPad", f2, tb2, h + 4, h * 0.56f);
        }
        return b;
    }
}

/// <summary>The icon iPhones and iPads show for the shared printer (GET /icon.png).</summary>
public static class PrinterIcon
{
    static byte[]? _png;
    public static byte[] Png
    {
        get
        {
            if (_png != null) return _png;
            using var bmp = Icons.Printer(128, true);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return _png = ms.ToArray();
        }
    }
}
