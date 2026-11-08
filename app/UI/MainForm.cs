using System.Diagnostics;
using System.Drawing;
using Oplus.Core;

namespace Oplus;

/// <summary>
/// The control panel: printers on the left, the selected
/// printer's details on the right, tools along the bottom, and a tray icon.
/// </summary>
public sealed class MainForm : Form
{
    readonly ShareService? _service;
    readonly bool _screenshot;
    List<PrinterEntry> _printers = new();
    PrinterEntry? _sel;
    bool _editing;
    bool _exiting;

    readonly ToolTip _tips = new();
    readonly ListView _list = new();
    readonly PictureBox _bigIcon = new();
    readonly TextBox _name = new(), _location = new();
    readonly Label _folder, _info, _sharedCount, _status, _detailTitle;
    readonly PictureBox _capColor = new(), _capDuplex = new(), _capDpi = new();
    readonly Button _shareBtn, _editBtn, _saveBtn, _cancelBtn, _secBtn, _queueBtn, _addBtn, _removeBtn;
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    public MainForm(ShareService? service, bool screenshot)
    {
        _service = service;
        _screenshot = screenshot;
        Text = "Oplus";
        Icon = Icons.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(620, 332);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Back;
        Font = Ui.Body;
        DoubleBuffered = true;

        // ---- header
        var logo = new PictureBox { Left = 205, Top = 2, Width = 210, Height = 44, Image = Icons.Logo(210, 44), BackColor = Color.Transparent };
        Controls.Add(logo);

        // ---- printer list
        _list.SetBounds(6, 50, 310, 230);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.OwnerDraw = true;
        _list.BackColor = Ui.ListBack;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, 40) }; // row height
        _list.Columns.Add("", 30);
        _list.Columns.Add("", 26);
        _list.Columns.Add("Printer Name / Location", 310 - 30 - 26 - 40 - 4);
        _list.Columns.Add("", 40);
        _list.DrawColumnHeader += (_, e) => DrawHeader(e);
        _list.DrawSubItem += (_, e) => DrawRow(e);
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndices.Count > 0) Select(_printers[_list.SelectedIndices[0]]); };
        // Double-click opens the Windows print queue, or the folder of a folder printer.
        _list.DoubleClick += (_, _) => OpenQueue();
        Controls.Add(_list);

        // ---- details box
        var box = new Panel { Left = 326, Top = 50, Width = 288, Height = 130, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(box);
        _detailTitle = Ui.Lbl("", 8, 4, 270, 18, ContentAlignment.MiddleLeft, Ui.Bold);
        box.Controls.Add(_detailTitle);
        _bigIcon.SetBounds(6, 26, 64, 64);
        box.Controls.Add(_bigIcon);
        box.Controls.Add(Ui.Lbl("Name:", 70, 28, 58, 24, ContentAlignment.MiddleRight));
        box.Controls.Add(Ui.Lbl("Location:", 70, 56, 58, 24, ContentAlignment.MiddleRight));
        _name.SetBounds(132, 28, 148, 24); _name.ReadOnly = true; _name.MaxLength = Defaults.NameMax;
        _location.SetBounds(132, 56, 148, 24); _location.ReadOnly = true; _location.MaxLength = Defaults.LocationMax;
        _name.KeyDown += EditKeys; _location.KeyDown += EditKeys;
        box.Controls.Add(_name); box.Controls.Add(_location);
        _folder = Ui.Lbl("", 76, 82, 206, 16, ContentAlignment.MiddleLeft);
        _folder.ForeColor = Ui.SubInk; _folder.AutoEllipsis = true;
        box.Controls.Add(_folder);
        _capColor.SetBounds(12, 96, 28, 28); _capDuplex.SetBounds(48, 96, 28, 28); _capDpi.SetBounds(84, 96, 28, 28);
        foreach (var pb in new[] { _capColor, _capDuplex, _capDpi }) { pb.SizeMode = PictureBoxSizeMode.Zoom; box.Controls.Add(pb); }
        _info = Ui.Lbl("", 118, 100, 164, 20);
        _info.ForeColor = Ui.SubInk; _info.AutoEllipsis = true;
        box.Controls.Add(_info);

        // ---- printer tools:
        // Share on/off, Security, print queue / open folder, Edit → Save + Cancel, Log.
        int ty = 188, s = 34;
        _shareBtn = Ui.IconBtn(Icons.ShareWaves(26, true), 326, ty, s, "Status: share with iPhone and iPad (on / off)", _tips, (_, _) => ToggleShare());
        _secBtn = Ui.IconBtn(Icons.Lock(24), 364, ty, s, "Security", _tips, (_, _) => EditSecurity());
        _queueBtn = Ui.IconBtn(Icons.Gear(24), 402, ty, s, "Open the Windows print queue", _tips, (_, _) => OpenQueue());
        _editBtn = Ui.IconBtn(Icons.Pencil(24), 440, ty, s, "Edit", _tips, (_, _) => BeginEdit());
        _saveBtn = Ui.IconBtn(Icons.Check(24), 440, ty, s, "Save", _tips, (_, _) => SaveEdit());
        _cancelBtn = Ui.IconBtn(Icons.Cross(22), 478, ty, s, "Cancel", _tips, (_, _) => CancelEdit());
        var logBtn = Ui.IconBtn(Icons.Document(24), 580, ty, s, "Log", _tips, (_, _) => ShowLog());
        _saveBtn.Visible = _cancelBtn.Visible = false;
        Controls.AddRange(new Control[] { _shareBtn, _editBtn, _saveBtn, _cancelBtn, _secBtn, _queueBtn, logBtn });

        _sharedCount = Ui.Lbl("", 326, 228, 288, 18);
        _sharedCount.ForeColor = Ui.SubInk;
        Controls.Add(_sharedCount);

        // ---- bottom bar
        Controls.Add(new Label { Left = 0, Top = 284, Width = 620, Height = 1, BackColor = Color.FromArgb(215, 220, 228) });
        _addBtn = Ui.IconBtn(Icons.Folder(26, false, +1), 6, 291, 34, "Add virtual printer", _tips, (_, _) => ShowAddMenu());
        _removeBtn = Ui.IconBtn(Icons.Folder(26, false, -1), 44, 291, 34, "Remove virtual printer", _tips, (_, _) => RemoveVirtual());
        Controls.Add(_addBtn); Controls.Add(_removeBtn);
        _status = Ui.Lbl("", 84, 291, 300, 34);
        _status.ForeColor = Ui.SubInk; _status.AutoEllipsis = true;
        Controls.Add(_status);
        // Bottom right: Email Alert, Settings, About.
        Controls.Add(Ui.Btn("Email Alert", 390, 293, 74, 30, (_, _) => ShowEmail()));
        Controls.Add(Ui.Btn("Settings", 470, 293, 70, 30, (_, _) => ShowSettings()));
        Controls.Add(Ui.Btn("About", 546, 293, 68, 30, (_, _) => ShowAbout()));

        // ---- tray (Control Panel / About / Exit)
        _tray.Icon = Icons.AppIcon;
        _tray.Text = "Oplus";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Control Panel", null, (_, _) => ShowFromTray());
        menu.Items.Add("About", null, (_, _) => { ShowFromTray(); ShowAbout(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = !screenshot;

        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        if (_service != null)
        {
            _service.Changed += () => BeginInvokeSafe(RefreshStatus);
            _service.JobsChanged += () => BeginInvokeSafe(RefreshStatus);
        }

        LoadPrinters();
    }

    void BeginInvokeSafe(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

    // ------------------------------------------------------------------ list
    void LoadPrinters(string? keepKey = null)
    {
        keepKey ??= _sel?.Key;
        _printers = SettingsStore.LoadPrinters();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _printers)
        {
            var it = new ListViewItem(new[] { "", "", p.Alias, "" });
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        FitColumns();
        int idx = Math.Max(0, _printers.FindIndex(p => p.Key == keepKey));
        if (_printers.Count > 0) { _list.Items[idx].Selected = true; _list.Items[idx].Focused = true; Select(_printers[idx]); }
        else Select(null);
        _service?.Apply(_printers);
        RefreshStatus();
    }

    /// <summary>Name column takes what is left so there is never a sideways scrollbar.</summary>
    void FitColumns()
    {
        int rowsHeight = _list.Items.Count * 40 + 26;
        int scroll = rowsHeight > _list.ClientSize.Height ? SystemInformation.VerticalScrollBarWidth : 0;
        int fixedW = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[3].Width;
        _list.Columns[2].Width = Math.Max(80, _list.Width - 4 - scroll - fixedW);
    }

    void DrawHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(Color.FromArgb(222, 228, 238));
        e.Graphics.FillRectangle(back, e.Bounds);
        using var line = new Pen(Color.FromArgb(200, 206, 216));
        e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Ui.Bold, Rectangle.Inflate(e.Bounds, -4, 0), Ui.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    void DrawRow(DrawListViewSubItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _printers.Count || e.Item == null) return;
        var p = _printers[e.ItemIndex];
        var g = e.Graphics;
        var r = e.Bounds;
        bool selected = e.Item.Selected;
        using (var back = new SolidBrush(selected ? Color.FromArgb(198, 220, 245) : (e.ItemIndex % 2 == 0 ? Ui.ListBack : Color.FromArgb(242, 245, 250))))
            g.FillRectangle(back, r);
        switch (e.ColumnIndex)
        {
            case 0:
                using (var dot = Icons.Dot(22, p.Share ? Color.FromArgb(40, 180, 70) : Color.FromArgb(70, 70, 70)))
                    g.DrawImage(dot, r.Left + (r.Width - 22) / 2, r.Top + (r.Height - 22) / 2);
                break;
            case 1:
                if (p.Secured) using (var lk = Icons.Lock(20)) g.DrawImage(lk, r.Left + (r.Width - 20) / 2, r.Top + (r.Height - 20) / 2);
                break;
            case 2:
                {
                    var nameR = new Rectangle(r.Left + 2, r.Top + 3, r.Width - 4, r.Height / 2 - 2);
                    var locR = new Rectangle(r.Left + 2, r.Top + r.Height / 2, r.Width - 4, r.Height / 2 - 3);
                    TextRenderer.DrawText(g, p.Alias, Ui.Bold, nameR, Ui.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
                    string loc = p.IsVirtual ? (p.FolderPath.Length > 0 ? p.FolderPath : SettingsStore.DropboxFolder) : (p.Location.Length > 0 ? p.Location : p.RealPrinterName);
                    TextRenderer.DrawText(g, loc, Ui.Body, locR, Ui.SubInk, TextFormatFlags.EndEllipsis | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis);
                }
                break;
            case 3:
                using (var ic = TypeIcon(p, 30)) g.DrawImage(ic, r.Left + (r.Width - 30) / 2, r.Top + (r.Height - 30) / 2);
                break;
        }
        using var sep = new Pen(Color.FromArgb(215, 221, 230));
        g.DrawLine(sep, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
    }

    static Bitmap TypeIcon(PrinterEntry p, int s) => p.Kind switch
    {
        PrinterKind.Folder => Icons.Folder(s),
        PrinterKind.Dropbox => Icons.Folder(s, cloud: true),
        _ => Icons.Printer(s, p.Color),
    };

    // ------------------------------------------------------------------ details
    void Select(PrinterEntry? p)
    {
        if (_editing) CancelEdit();
        _sel = p;
        bool has = p != null;
        foreach (var b in new[] { _shareBtn, _editBtn, _secBtn }) b.Enabled = has;
        _queueBtn.Enabled = has;
        _removeBtn.Enabled = has && p!.IsVirtual;
        if (has) _tips.SetToolTip(_queueBtn, p!.IsVirtual ? "Open the folder" : "Open the Windows print queue");
        if (p == null)
        {
            _detailTitle.Text = "No printer"; _name.Text = _location.Text = _folder.Text = _info.Text = "";
            _bigIcon.Image = null; return;
        }
        _detailTitle.Text = p.IsVirtual ? (p.Kind == PrinterKind.Dropbox ? "Save to Dropbox" : "Save to Folder") : p.RealPrinterName;
        _bigIcon.Image?.Dispose();
        _bigIcon.Image = TypeIcon(p, 64);
        _name.Text = p.Alias;
        _location.Text = p.Location;
        _folder.Text = p.IsVirtual ? "Folder: " + (p.FolderPath.Length > 0 ? p.FolderPath : SettingsStore.DropboxFolder) : "";
        _tips.SetToolTip(_folder, _folder.Text);
        SetCap(_capColor, Icons.ColorWheel(28, p.Color), p.Color ? "Prints in colour" : "Black and white only");
        SetCap(_capDuplex, Icons.Duplex(28, p.Duplex), p.Duplex ? "Prints on both sides" : "Prints on one side");
        SetCap(_capDpi, Icons.DpiBadge(28, p.Dpi), $"Resolution {p.Dpi} dpi");
        _shareBtn.Image?.Dispose();
        _shareBtn.Image = Icons.ShareWaves(26, p.Share);
        _tips.SetToolTip(_shareBtn, p.Share ? "Shared — click to stop sharing" : "Not shared — click to share with iPhone and iPad");
        _secBtn.Image?.Dispose();
        _secBtn.Image = Icons.Lock(24, open: !p.Secured);
        RefreshStatus();
    }

    void SetCap(PictureBox pb, Bitmap img, string tip) { pb.Image?.Dispose(); pb.Image = img; _tips.SetToolTip(pb, tip); }

    void RefreshStatus()
    {
        int shared = _printers.Count(p => p.Share);
        _sharedCount.Text = $"Shared printers: {shared} of {_printers.Count}";
        if (_sel != null)
        {
            string state;
            if (!_sel.Share) state = "Not shared";
            else if (_service?.Server?.CurrentJob is PrintJob j && j.Printer.Key == _sel.Key) state = $"Printing \"{j.Name}\"";
            else if (!_sel.IsVirtual && !_screenshot && ShareService.ProblemOf(_sel.RealPrinterName) is { Length: > 0 } prob) state = prob;
            else state = "Shared · Ready";
            _info.Text = state;
        }
        if (_screenshot) { _status.Text = $"Sharing on port {Defaults.FirstPort}"; return; }
        var svc = _service;
        if (svc == null) return;
        var last = svc.Server?.Jobs.LastOrDefault();
        string jobText = last == null ? "" : $"\nLast job: {last.Name} ({last.State.ToString().ToLowerInvariant()})";
        _status.Text = svc.Status + jobText;
        _tray.Text = ("Oplus – " + svc.Status).Length > 63 ? "Oplus" : "Oplus – " + svc.Status;
    }

    void ToggleShare()
    {
        if (_sel == null || _editing) return;
        _sel.Share = !_sel.Share;
        SettingsStore.SavePrinter(_sel);
        _service?.Apply(_printers);
        Select(_sel);
        _list.Invalidate();
    }

    // ------------------------------------------------------------------ edit name / location
    void BeginEdit()
    {
        if (_sel == null) return;
        _editing = true;
        _name.ReadOnly = _location.ReadOnly = false;
        _name.BackColor = _location.BackColor = Color.FromArgb(255, 255, 235);
        _editBtn.Visible = false; _saveBtn.Visible = _cancelBtn.Visible = true;
        // While editing, the list and the add/remove buttons are locked.
        _list.Enabled = _addBtn.Enabled = _removeBtn.Enabled = false;
        _name.Focus(); _name.SelectAll();
    }

    void EditKeys(object? sender, KeyEventArgs e)
    {
        if (!_editing) return;
        if (e.KeyCode == Keys.Enter) { SaveEdit(); e.SuppressKeyPress = true; }
        if (e.KeyCode == Keys.Escape) { CancelEdit(); e.SuppressKeyPress = true; }
    }

    void EndEdit()
    {
        _editing = false;
        _name.ReadOnly = _location.ReadOnly = true;
        _name.BackColor = _location.BackColor = SystemColors.Control;
        _editBtn.Visible = true; _saveBtn.Visible = _cancelBtn.Visible = false;
        _list.Enabled = _addBtn.Enabled = true;
        _removeBtn.Enabled = _sel?.IsVirtual == true;
    }

    void CancelEdit()
    {
        EndEdit();
        if (_sel != null) { _name.Text = _sel.Alias; _location.Text = _sel.Location; }
    }

    void SaveEdit()
    {
        if (_sel == null) return;
        string name = _name.Text.Trim(), loc = _location.Text.Trim();
        // Name and location are both required, 1-256 characters each.
        if (name.Length < 1 || name.Length > Defaults.NameMax) { Ui.Warn(this, $"Printer name: 1~{Defaults.NameMax} characters."); return; }
        if (name.Contains('\\')) { Ui.Warn(this, "Input error! Printer name without \"\\\"."); return; }
        if (loc.Length < 1 || loc.Length > Defaults.LocationMax) { Ui.Warn(this, $"Location: 1~{Defaults.LocationMax} characters."); return; }
        if (_printers.Any(p => p != _sel && string.Equals(p.Alias, name, StringComparison.OrdinalIgnoreCase)))
        { Ui.Warn(this, $"The printer name \"{name}\" already exists.\nPlease enter another printer name."); return; }
        _sel.Alias = name; _sel.Location = loc;
        SettingsStore.SavePrinter(_sel);
        EndEdit();
        _service?.Apply(_printers);
        _list.Invalidate();
        Select(_sel);
    }

    // ------------------------------------------------------------------ dialogs
    void EditSecurity()
    {
        if (_sel == null) return;
        if (Dialogs.Security(this, _sel))
        {
            SettingsStore.SavePrinter(_sel);
            _service?.Apply(_printers);
            _list.Invalidate();
            Select(_sel);
        }
    }

    void OpenQueue()
    {
        // Real printer: the Windows print queue; folder printer: open the folder.
        if (_sel == null) { Ui.Warn(this, "Please select a printer."); return; }
        try
        {
            if (_sel.IsVirtual)
            {
                var dir = _sel.FolderPath.Length > 0 ? _sel.FolderPath : SettingsStore.DropboxFolder;
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            else Process.Start(new ProcessStartInfo("rundll32", $"printui.dll,PrintUIEntry /o /n \"{_sel.RealPrinterName}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Ui.Warn(this, ex.Message); }
    }

    void ShowAddMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Save to Folder", Icons.Folder(16), (_, _) => AddVirtual(PrinterKind.Folder));
        m.Items.Add("Save to Dropbox", Icons.Folder(16, cloud: true), (_, _) => AddVirtual(PrinterKind.Dropbox));
        m.Show(_addBtn, new Point(0, _addBtn.Height));
    }

    void AddVirtual(PrinterKind kind)
    {
        var entry = Dialogs.AddVirtualPrinter(this, kind, _printers);
        if (entry == null) return;
        SettingsStore.SavePrinter(entry);
        if (SettingsStore.ReadOnly) _printers.Add(entry);
        LoadPrinters(entry.Key);
    }

    void RemoveVirtual()
    {
        // Removes the folder printer and its Security key at once, no question asked;
        // nothing happens for real printers. Files already saved stay in the folder.
        if (_sel == null || !_sel.IsVirtual) return;
        SettingsStore.DeletePrinter(_sel);
        _sel = null;
        LoadPrinters();
    }

    void ShowSettings()
    {
        if (Dialogs.Settings(this)) _service?.RestartServer();
        RefreshStatus();
    }

    void ShowAbout() => Dialogs.About(this);
    void ShowLog() => Dialogs.Log(this);
    void ShowEmail() => Dialogs.Email(this);

    // ------------------------------------------------------------------ tray
    /// <summary>Set before Application.Run to start in the tray (/hide).</summary>
    public bool StartHidden { get; set; }

    protected override void SetVisibleCore(bool value)
    {
        if (StartHidden && value)
        {
            StartHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    void HideToTray()
    {
        Hide();
        _tray.Visible = true;
    }

    void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        LoadPrinters();
    }

    void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && !_screenshot && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
