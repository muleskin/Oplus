using System.Drawing;

namespace Oplus;

/// <summary>Shared colours and control helpers.</summary>
public static class Ui
{
    public static readonly Color ListBack = Color.FromArgb(233, 237, 244);
    public static readonly Color ButtonFace = Color.FromArgb(204, 232, 207);
    public static readonly Color ButtonBorder = Color.FromArgb(150, 190, 155);
    public static readonly Color Back = Color.FromArgb(246, 248, 250);
    public static readonly Color Ink = Color.FromArgb(35, 45, 60);
    public static readonly Color SubInk = Color.FromArgb(105, 115, 130);
    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Big = new("Segoe UI", 10.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 11f);

    public static Button Btn(string text, int x, int y, int w, int h, EventHandler? click = null)
    {
        var b = new Button
        {
            Text = text, Left = x, Top = y, Width = w, Height = h,
            FlatStyle = FlatStyle.Flat, BackColor = ButtonFace, ForeColor = Ink, Font = Body,
            UseVisualStyleBackColor = false, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = ButtonBorder;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(ButtonFace, 0.4f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(ButtonFace, 0.05f);
        if (click != null) b.Click += click;
        return b;
    }

    public static Button IconBtn(Image img, int x, int y, int size, string tip, ToolTip tt, EventHandler? click = null)
    {
        var b = Btn("", x, y, size, size, click);
        b.Image = img; b.ImageAlign = ContentAlignment.MiddleCenter; b.Padding = Padding.Empty;
        tt.SetToolTip(b, tip);
        return b;
    }

    public static Label Lbl(string text, int x, int y, int w, int h, ContentAlignment align = ContentAlignment.MiddleLeft, Font? font = null)
        => new() { Text = text, Left = x, Top = y, Width = w, Height = h, TextAlign = align, Font = font ?? Body, ForeColor = Ink, BackColor = Color.Transparent };

    public static TextBox Box(int x, int y, int w, int max = 0, bool password = false)
    {
        var t = new TextBox { Left = x, Top = y, Width = w, Font = Body, UseSystemPasswordChar = password };
        if (max > 0) t.MaxLength = max;
        return t;
    }

    /// <summary>A plain dialog window.</summary>
    public static Form Dialog(string title, int w, int h)
    {
        var f = new Form
        {
            Text = title, ClientSize = new Size(w, h), FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent,
            BackColor = Back, Font = Body, Icon = Icons.AppIcon, AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
        };
        return f;
    }

    public static void Warn(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, "Oplus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
