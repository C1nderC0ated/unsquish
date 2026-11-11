// Before / after comparison of the files of the last run: pick a file, slide the split line with the mouse.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Unsquish;

sealed class CompareForm : Form
{
    readonly List<(string Input, string Output, string Route)> items;
    readonly ListBox list = new() { Dock = DockStyle.Left, Width = 270, IntegralHeight = false, HorizontalScrollbar = true };
    readonly ComparePanel view = new() { Dock = DockStyle.Fill };
    readonly Label info = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(6, 0, 0, 0) };
    int token; string current = "", error = "";
    readonly SemaphoreSlim gate = new(1, 1);   // one pair is decoded at a time; selections made meanwhile wait, and only the newest runs

    public CompareForm(List<(string Input, string Output, string Route)> items)
    {
        this.items = items;
        Text = "Unsquish: before | after"; Size = new Size(1240, 820); MinimumSize = new Size(640, 400); StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Controls.Add(view); Controls.Add(list); Controls.Add(info);   // docked back to front: status line, list, then the view fills the rest
        foreach (var it in items) list.Items.Add($"{Path.GetFileName(it.Input)}   [{it.Route}]");
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedIndex >= 0) LoadPair(list.SelectedIndex); };
        view.StateChanged += UpdateInfo;
        KeyDown += (_, e) => { if (view.HandleKey(e.KeyCode)) { e.Handled = true; e.SuppressKeyPress = true; } };
        Shown += (_, _) => { if (list.Items.Count > 0) list.SelectedIndex = 0; };
        FormClosed += (_, _) => { token++; view.SetImages(null, null); };
    }

    void LoadPair(int index)
    {
        int mine = ++token; var it = items[index]; info.Text = "Loading " + Path.GetFileName(it.Input) + " ...";
        Task.Run(() =>
        {
            Bitmap? before = null, after = null; string err = "";
            gate.Wait();
            try
            {
                if (mine != Volatile.Read(ref token)) return;   // scrolled past while waiting
                before = LoadBitmap(it.Input);
                if (mine == Volatile.Read(ref token)) after = LoadBitmap(it.Output);
            }
            catch (Exception ex) { err = ex.Message; }
            finally { gate.Release(); }
            if (mine != Volatile.Read(ref token)) { before?.Dispose(); after?.Dispose(); return; }
            try
            {
                BeginInvoke(() =>
                {
                    if (mine != token || IsDisposed) { before?.Dispose(); after?.Dispose(); return; }
                    current = Path.GetFileName(it.Input) + "  ->  " + Path.GetFileName(it.Output); error = err;
                    view.SetImages(before, after);
                });
            }
            catch { before?.Dispose(); after?.Dispose(); }   // the window was closed meanwhile
        });
    }

    void UpdateInfo()
    {
        if (error.Length > 0) { info.Text = "Could not load: " + error; return; }
        var b = view.BeforeSize; var a = view.AfterSize;
        string which = view.Mode == 1 ? "BEFORE only" : view.Mode == 2 ? "AFTER only" : "before | after";
        info.Text = $"{current}    {b.Width}x{b.Height} -> {a.Width}x{a.Height}    {(view.Zoom * 100).ToString("0", CultureInfo.InvariantCulture)}%  {which}"
                  + "      mouse: slide the line, drag to pan, wheel to zoom      keys: B before, A after, S split, F fit, 1 = 100%";
    }

    /// <summary>Decodes any file the tool reads or writes into a bitmap, upright (EXIF orientation applied).</summary>
    static Bitmap LoadBitmap(string path)
    {
        RgbImage? img;
        if (path.EndsWith(".jxl", StringComparison.OrdinalIgnoreCase)) img = Jxl.Decode(File.ReadAllBytes(path));
        else if (Loader.Sniff(path) == "jpeg")
        {
            img = Jpeg.DecodePlain(path);
            if (img != null) { int orient = Exif.Orientation(JpegProbe.Probe(path).ExifTiff); if (orient > 1) img = img.Orient(orient); }
        }
        else
        {
            img = Loader.LoadNonJpeg(path, out _, out _, out var exif);
            if (img != null) { int orient = Exif.Orientation(exif); if (orient > 1) img = img.Orient(orient); }
        }
        if (img == null) throw new IOException("cannot decode " + Path.GetFileName(path));
        return ToBitmap(img);
    }

    static Bitmap ToBitmap(RgbImage img)
    {
        var bmp = new Bitmap(img.W, img.H, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.W, img.H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride]; const int bg = 40;   // transparency is shown over dark grey
            for (int y = 0; y < img.H; y++)
            {
                int s = y * img.W * 3, ai = y * img.W;
                for (int x = 0, d = 0; x < img.W; x++, s += 3, d += 3)
                {
                    int r = img.Rgb[s], g = img.Rgb[s + 1], b = img.Rgb[s + 2];
                    if (img.Alpha != null) { int a = img.Alpha[ai + x]; r = (r * a + bg * (255 - a)) / 255; g = (g * a + bg * (255 - a)) / 255; b = (b * a + bg * (255 - a)) / 255; }
                    row[d] = (byte)b; row[d + 1] = (byte)g; row[d + 2] = (byte)r;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}

/// <summary>Draws "before" left of a vertical line and "after" right of it, both mapped onto the same rectangle
/// (so an upscaled or rotated result still lines up with its source).</summary>
sealed class ComparePanel : Control
{
    Bitmap? before, after; float zoom = 1f; PointF origin; int splitX; bool fit = true; int mode;   // mode: 0 split, 1 before, 2 after
    Point dragFrom; PointF dragOrigin; bool dragging;
    public event Action? StateChanged;
    public float Zoom => zoom; public int Mode => mode;
    public Size BeforeSize => before?.Size ?? Size.Empty; public Size AfterSize => after?.Size ?? Size.Empty;

    public ComparePanel() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.FromArgb(24, 24, 24); }

    public void SetImages(Bitmap? b, Bitmap? a)
    {
        before?.Dispose(); after?.Dispose(); before = b; after = a; fit = true; mode = 0; splitX = Width / 2;
        Fit(); Invalidate(); StateChanged?.Invoke();
    }

    Size Canvas => after?.Size ?? before?.Size ?? Size.Empty;

    void Fit()
    {
        var c = Canvas; if (c.IsEmpty || Width < 2 || Height < 2) return;
        zoom = Math.Min((float)Width / c.Width, (float)Height / c.Height);
        if (zoom > 1f) zoom = Math.Min(8f, MathF.Floor(zoom));          // whole factors keep pixels crisp
        origin = new PointF((c.Width - Width / zoom) / 2f, (c.Height - Height / zoom) / 2f);
    }

    void SetZoom(float z, Point at)
    {
        z = Math.Clamp(z, 0.02f, 16f);
        var p = new PointF(origin.X + at.X / zoom, origin.Y + at.Y / zoom);   // the picture point under the cursor stays put
        zoom = z; origin = new PointF(p.X - at.X / zoom, p.Y - at.Y / zoom); fit = false;
        Invalidate(); StateChanged?.Invoke();
    }

    public bool HandleKey(Keys k)
    {
        switch (k)
        {
            case Keys.B: mode = mode == 1 ? 0 : 1; break;
            case Keys.A: mode = mode == 2 ? 0 : 2; break;
            case Keys.S: case Keys.Escape: mode = 0; break;
            case Keys.F: fit = true; Fit(); break;
            case Keys.D1: case Keys.NumPad1: SetZoom(1f, new Point(Width / 2, Height / 2)); return true;
            case Keys.D2: case Keys.NumPad2: SetZoom(2f, new Point(Width / 2, Height / 2)); return true;
            case Keys.D4: case Keys.NumPad4: SetZoom(4f, new Point(Width / 2, Height / 2)); return true;
            default: return false;
        }
        Invalidate(); StateChanged?.Invoke(); return true;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (fit) Fit(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { dragging = true; dragFrom = e.Location; dragOrigin = origin; } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) { origin = new PointF(dragOrigin.X - (e.X - dragFrom.X) / zoom, dragOrigin.Y - (e.Y - dragFrom.Y) / zoom); fit = false; }
        else splitX = e.X;
        Invalidate();
    }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); SetZoom(zoom * (e.Delta > 0 ? 1.25f : 0.8f), e.Location); }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (fit) SetZoom(1f, e.Location); else { fit = true; Fit(); Invalidate(); StateChanged?.Invoke(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        var c = Canvas; if (c.IsEmpty) return;
        var dest = new RectangleF(-origin.X * zoom, -origin.Y * zoom, c.Width * zoom, c.Height * zoom);
        g.InterpolationMode = zoom >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int sx = mode == 1 ? Width : mode == 2 ? 0 : Math.Clamp(splitX, 0, Width);
        if (before != null && sx > 0) { g.SetClip(new Rectangle(0, 0, sx, Height)); g.DrawImage(before, dest); }
        if (after != null && sx < Width) { g.SetClip(new Rectangle(sx, 0, Width - sx, Height)); g.DrawImage(after, dest); }
        g.ResetClip();
        using var back = new SolidBrush(Color.FromArgb(170, 0, 0, 0)); using var fore = new SolidBrush(Color.White);
        void Tag(string text, float x) { var size = g.MeasureString(text, Font); g.FillRectangle(back, x, 6, size.Width + 8, size.Height + 4); g.DrawString(text, Font, fore, x + 4, 8); }
        if (mode == 0)
        {
            using var pen = new Pen(Color.FromArgb(230, 255, 255, 255)); g.DrawLine(pen, sx, 0, sx, Height);
            Tag("before", Math.Max(4, sx - 70)); Tag("after", Math.Min(Width - 60, sx + 8));
        }
        else Tag(mode == 1 ? "before" : "after", 8);
    }

    protected override void Dispose(bool disposing) { if (disposing) { before?.Dispose(); after?.Dispose(); } base.Dispose(disposing); }
}
