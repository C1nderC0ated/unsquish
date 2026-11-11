// The window shown when unsquish.exe is started by double-click (or with files dropped on its icon).
// It only collects Options and drives Engine.Run on a worker thread; all decisions stay in the engine.
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Unsquish;

public static class Gui
{
    public static void Start(string[] initialInputs)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetColorMode(SystemColorMode.Dark); } catch { }
        Application.Run(new MainForm(initialInputs));
    }

    /// <summary>The before / after viewer on its own, for any two pictures the tool can read.</summary>
    public static void View(string before, string after)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetColorMode(SystemColorMode.Dark); } catch { }
        Application.Run(new CompareForm(new List<(string Input, string Output, string Route)> { (before, after, "view") }) { StartPosition = FormStartPosition.CenterScreen });
    }
}

sealed class MainForm : Form
{
    readonly ListBox inputs = new() { IntegralHeight = false, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended, AllowDrop = true };
    readonly TextBox outBox = new() { PlaceholderText = "empty = <input>_restored next to the input" };
    readonly CheckBox recurse = new() { Text = "Include subfolders", Checked = true, AutoSize = true };
    readonly CheckBox overwrite = new() { Text = "Overwrite existing outputs", AutoSize = true };
    readonly ComboBox content = Combo("Auto-detect: photo / art / pixel art", "Photo", "Art (anime, flat color, scanned documents)", "Pixel art", "No detection: every JPEG is a photo");
    readonly ComboBox level = Combo("Auto (photo 4, art 6)", "0 (fastest, mildest)", "1", "2 (fast)", "3", "4", "5", "6 (strongest)");
    readonly ComboBox blur = Combo("Off", "On");
    readonly ComboBox xbrz = Combo("Auto (up to ~1024 px)", "Keep size (no upscale)", "2x", "3x", "4x", "5x", "6x");
    readonly ComboBox style = Combo("Crisp (nearest-neighbor)", "Smooth (xBRZ)");
    readonly ComboBox format = Combo("JPEG XL (lossless, smallest)", "PNG (lossless, opens anywhere)", "JPEG quality 95 (small, opens anywhere)");
    readonly CheckBox history = new() { Text = "Find earlier compression", Checked = true, AutoSize = true };
    readonly Button compare = new() { Text = "Compare before / after", AutoSize = true, Enabled = false };
    List<(string Input, string Output, string Route)> lastOutputs = new();
    readonly List<CompareForm> viewers = new();
    readonly NumericUpDown snap = new() { Minimum = 0, Maximum = 256, Value = 0, Width = 70 };
    readonly NumericUpDown qf = new() { Minimum = 1, Maximum = 100, Value = 90, Width = 70 };
    readonly NumericUpDown threads = new() { Minimum = 1, Maximum = 256, Value = Environment.ProcessorCount, Width = 70 };
    readonly Button addFiles = new() { Text = "Add files..." }, addFolder = new() { Text = "Add folder..." }, remove = new() { Text = "Remove" }, clear = new() { Text = "Clear" };
    readonly Button browseOut = new() { Text = "Browse...", AutoSize = true };
    readonly Button preview = new() { Text = "Preview (writes nothing)", AutoSize = true }, run = new() { Text = "Restore", AutoSize = true }, cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    readonly Button openOut = new() { Text = "Open output folder", AutoSize = true, Enabled = false }, guide = new() { Text = "Guide", AutoSize = true }, reset = new() { Text = "Reset to Auto", AutoSize = true };
    readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Height = 18 };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "Add files or a folder, then click Restore." };
    static readonly Color LogBack = Color.FromArgb(24, 24, 24), LogFore = Color.FromArgb(220, 220, 220);
    readonly RichTextBox log = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9f), BackColor = LogBack, ForeColor = LogFore, DetectUrls = false, BorderStyle = BorderStyle.FixedSingle };
    CancellationTokenSource? cts; string? lastOut; bool closeWhenDone;
    static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "unsquish", "gui.ini");

    static ComboBox Combo(params string[] items) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill }; c.Items.AddRange(items); c.SelectedIndex = 0; return c; }

    public MainForm(string[] initialInputs)
    {
        Text = $"Unsquish {Engine.Version}"; MinimumSize = new Size(760, 680); Size = new Size(860, 760); StartPosition = FormStartPosition.CenterScreen; AllowDrop = true;
        Font = SystemFonts.MessageBoxFont ?? Font; AutoScaleMode = AutoScaleMode.Font;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch { }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));   // inputs
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // output
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // settings
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // progress
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // log
        Controls.Add(root);

        // --- inputs
        var inGroup = new GroupBox { Text = "Input: files or folders (drop them anywhere on this window)", Dock = DockStyle.Fill };
        var inTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        inTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); inTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputs.Dock = DockStyle.Fill; inTable.Controls.Add(inputs, 0, 0);
        var inButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        foreach (var b in new[] { addFiles, addFolder, remove, clear }) { b.Width = 110; inButtons.Controls.Add(b); }
        inTable.Controls.Add(inButtons, 1, 0); inGroup.Controls.Add(inTable); root.Controls.Add(inGroup, 0, 0);

        // --- output
        var outTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        outTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); outTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); outTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outTable.Controls.Add(new Label { Text = "Output folder:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        outBox.Dock = DockStyle.Fill; outTable.Controls.Add(outBox, 1, 0); outTable.Controls.Add(browseOut, 2, 0);
        var outFlags = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        outFlags.Controls.Add(recurse); outFlags.Controls.Add(overwrite); outFlags.Controls.Add(history);
        outTable.Controls.Add(outFlags, 1, 1); outTable.SetColumnSpan(outFlags, 2);
        root.Controls.Add(outTable, 0, 1);

        // --- settings
        var setGroup = new GroupBox { Text = "Settings: Auto is right for mixed folders; force a content type when the detection is wrong", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, Padding = new Padding(4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        void Row(int r, string l1, Control c1, string l2, Control c2)
        {
            grid.Controls.Add(new Label { Text = l1, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r); grid.Controls.Add(c1, 1, r);
            grid.Controls.Add(new Label { Text = l2, AutoSize = true, Anchor = AnchorStyles.Left }, 2, r); grid.Controls.Add(c2, 3, r);
        }
        Row(0, "Content:", content, "Output format:", format);
        Row(1, "Restoration level:", level, "Restore JPEGs below quality:", qf);
        Row(2, "Selective blur:", blur, "Pixel-art upscale:", xbrz);
        Row(3, "Palette snap, colors (0 = off):", snap, "Threads:", threads);
        grid.Controls.Add(new Label { Text = "Pixel-art style:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4); grid.Controls.Add(style, 1, 4);
        grid.Controls.Add(reset, 3, 4); reset.Anchor = AnchorStyles.Right;
        setGroup.Controls.Add(grid); root.Controls.Add(setGroup, 0, 2);
        var tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
        tips.SetToolTip(content, "Auto-detect looks at edges, color count and pixel grids. Force a type when the log shows a wrong class.");
        tips.SetToolTip(level, "0-1: light smoothing, fastest. 2: quick denoising. 3-6: full denoising; each step up spends more time on chroma (photos) or on flattening flat color (art). Auto uses 4 for photos and 6 for art.");
        tips.SetToolTip(blur, "The selective blur that versions before 0.8 applied to photos after restoration. Off by default: it lowers measured quality. Bad for line art.");
        tips.SetToolTip(xbrz, "Upscale factor for pixel art. Auto is enough for about 1024 px and never below an undone enlargement. Keep size writes it at native resolution.");
        tips.SetToolTip(style, "Crisp keeps every pixel a block (the pixel-art look). Smooth redraws stair-steps as curves with xBRZ, the emulator-filter look. Clean pixel art that is already at an integer scale is left alone on Crisp.");
        tips.SetToolTip(snap, "Pixel art that came from a JPEG: snap to this many colors before upscaling. Helps few-color sprites, hurts busy scenes.");
        tips.SetToolTip(qf, "JPEGs below this quality factor are restored; files at or above it are re-saved losslessly. 1 = lossless for everything.");
        tips.SetToolTip(format, "JPEG XL holds the restored pixels losslessly in the smallest file. PNG is lossless and opens anywhere. JPEG at quality 95 is small and opens anywhere, but compresses once more.");
        tips.SetToolTip(history, "Recognizes JPEGs that were saved again at a higher quality, and PNG / BMP / WebP files that were JPEGs once, and restores them with the settings of that earlier compression. Only when the traces are certain.");
        tips.SetToolTip(compare, "Opens a viewer on the files of the last run: the original left of the line, the result right of it.");
        tips.SetToolTip(threads, "CPU threads for restoration and JPEG XL encoding.");
        tips.SetToolTip(overwrite, "By default existing output files are left alone and reported as EXISTS.");

        // --- buttons
        var btnRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        run.Font = new Font(Font, FontStyle.Bold);
        foreach (var b in new[] { run, preview, cancel, openOut, compare, guide }) { b.Padding = new Padding(6, 2, 6, 2); btnRow.Controls.Add(b); }
        root.Controls.Add(btnRow, 0, 3);

        // --- progress
        var progTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        progTable.Controls.Add(bar, 0, 0); progTable.Controls.Add(status, 0, 1); status.Height = 22;
        root.Controls.Add(progTable, 0, 4);
        root.Controls.Add(log, 0, 5);

        // --- behaviour
        addFiles.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Multiselect = true, Title = "Add images", Filter = "Images|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.gif;*.bmp;*.webp|All files|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) AddInputs(d.FileNames);
        };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Add a folder of images", UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (d.ShowDialog(this) == DialogResult.OK) AddInputs(new[] { d.SelectedPath });
        };
        remove.Click += (_, _) => { foreach (var i in inputs.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList()) inputs.Items.RemoveAt(i); };
        clear.Click += (_, _) => inputs.Items.Clear();
        inputs.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) remove.PerformClick(); };
        browseOut.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Output folder (created if missing)", UseDescriptionForTitle = true };
            if (!string.IsNullOrWhiteSpace(outBox.Text) && Directory.Exists(outBox.Text)) d.InitialDirectory = outBox.Text;
            if (d.ShowDialog(this) == DialogResult.OK) outBox.Text = d.SelectedPath;
        };
        foreach (Control c in new Control[] { this, inputs, log, inGroup, setGroup })
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
            c.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddInputs(paths); };
        }
        decimal lastSnap = 0;   // 1 is not a valid palette size: step over it in the direction the user is going
        snap.ValueChanged += (_, _) => { if (snap.Value == 1) snap.Value = lastSnap >= 2 ? 0 : 2; lastSnap = snap.Value; };
        reset.Click += (_, _) => { content.SelectedIndex = level.SelectedIndex = blur.SelectedIndex = xbrz.SelectedIndex = style.SelectedIndex = format.SelectedIndex = 0; snap.Value = 0; qf.Value = 90; threads.Value = Environment.ProcessorCount; history.Checked = true; };
        run.Click += (_, _) => StartRun(false);
        preview.Click += (_, _) => StartRun(true);
        cancel.Click += (_, _) => { cts?.Cancel(); status.Text = "Cancelling after the current file..."; cancel.Enabled = false; };
        openOut.Click += (_, _) => { if (lastOut != null && Directory.Exists(lastOut)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lastOut + "\"") { UseShellExecute = true }); };
        guide.Click += (_, _) => ShowGuide();
        compare.Click += (_, _) =>
        {
            if (lastOutputs.Count == 0) return;
            var v = new CompareForm(lastOutputs); viewers.Add(v); v.FormClosed += (_, _) => viewers.Remove(v); v.Show(this);
        };
        FormClosing += (_, e) =>
        {
            SaveSettings();
            if (cts != null && !cts.IsCancellationRequested) { cts.Cancel(); closeWhenDone = true; status.Text = "Cancelling, the window closes after the current file..."; e.Cancel = true; }
            else if (cts != null) { closeWhenDone = true; e.Cancel = true; }
        };
        LoadSettings();
        AddInputs(initialInputs);
        Append($"Unsquish {Engine.Version}. Inputs are never modified; results go to the output folder with an unsquish_log.csv.", LogFore);
        Append(Jxl.Available ? $"JPEG XL: libjxl {Jxl.VersionString} (bundled)." : "JPEG XL library not available: outputs will be PNG.", Color.Gray);
    }

    void AddInputs(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            string full; try { full = Path.GetFullPath(p); } catch { continue; }
            if (!File.Exists(full) && !Directory.Exists(full)) continue;
            if (!inputs.Items.Cast<string>().Any(x => x.Equals(full, StringComparison.OrdinalIgnoreCase))) inputs.Items.Add(full);
        }
        status.Text = inputs.Items.Count == 0 ? "Add files or a folder, then click Restore." : $"{inputs.Items.Count} input(s). Click Preview to see what would happen, or Restore.";
    }

    Options Collect(bool dry)
    {
        var o = new Options { Dry = dry, Recurse = recurse.Checked, Overwrite = overwrite.Checked, Qf = (int)qf.Value, Threads = (int)threads.Value, Snap = (int)snap.Value, Mode = format.SelectedIndex == 1 ? "png" : format.SelectedIndex == 2 ? "jpg" : "jxl", History = history.Checked };
        o.Inputs.AddRange(inputs.Items.Cast<string>());
        if (!string.IsNullOrWhiteSpace(outBox.Text)) o.Output = outBox.Text.Trim();
        switch (content.SelectedIndex) { case 1: o.ForceClass = "photo"; break; case 2: o.ForceClass = "art"; break; case 3: o.ForceClass = "pixelart"; break; case 4: o.Auto = false; break; }
        if (level.SelectedIndex > 0) { o.Qs = level.SelectedIndex - 1; o.ExplicitQs = true; }
        if (blur.SelectedIndex == 1) { o.Blur = true; o.ExplicitBlur = true; }
        if (xbrz.SelectedIndex > 0) o.Xbrz = xbrz.SelectedIndex;   // 1 = keep size, 2..6 = factor
        o.Smooth = style.SelectedIndex == 1;
        return o;
    }

    void StartRun(bool dry)
    {
        if (inputs.Items.Count == 0) { MessageBox.Show(this, "Add at least one file or folder first.", "Unsquish", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var o = Collect(dry);
        if (o.Output != null)
        {
            // by resolved identity, so a mapped drive, subst drive or junction naming an input folder is caught too
            var inside = o.Inputs.Where(inp => Paths.IsInside(Directory.Exists(inp) ? inp : (Path.GetDirectoryName(inp) ?? inp), o.Output)).ToList();
            if (inside.Count > 0)
            {
                MessageBox.Show(this, "These inputs lie inside the output folder and would be skipped. Choose another output folder.\n\n" + string.Join("\n", inside.Take(6)) + (inside.Count > 6 ? "\n..." : ""), "Unsquish", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        if (!dry) foreach (var v in viewers.ToArray()) v.Close();   // a viewer still reading an output would make replacing it fail
        SetBusy(true); log.Clear(); bar.Value = 0; bar.Maximum = 1; status.Text = dry ? "Previewing..." : "Working...";
        cts = new CancellationTokenSource(); var token = cts.Token;
        Task.Run(() =>
        {
            RunResult? r = null; Exception? err = null;
            try { r = Engine.Run(o, (s, c) => BeginInvoke(() => Append(s, Map(c))), (n, t) => BeginInvoke(() => { bar.Maximum = Math.Max(1, t); bar.Value = Math.Min(bar.Maximum, Math.Max(0, n - 1)); status.Text = $"{(dry ? "Previewing" : "Processing")} file {n} of {t}..."; }), token); }
            catch (Exception ex) { err = ex; }
            BeginInvoke(() =>
            {
                cts = null; SetBusy(false);
                if (err != null) { Append("Error: " + err, Color.Red); status.Text = "Failed: " + err.Message; }
                else if (r != null)
                {
                    bar.Value = bar.Maximum; lastOut = r.OutDir; openOut.Enabled = !dry && Directory.Exists(lastOut);
                    if (!dry) { lastOutputs = r.Outputs; compare.Enabled = lastOutputs.Count > 0; }
                    int fails = r.Counts.GetValueOrDefault("fail");
                    status.Text = token.IsCancellationRequested ? "Cancelled." : dry ? "Preview finished, nothing was written. Click Restore to run it." : (fails > 0 ? $"Finished with {fails} failure(s), see the log." : $"Finished in {r.Seconds.ToString("n1", CultureInfo.InvariantCulture)} s. Output: {r.OutDir}");
                }
                if (closeWhenDone) Close();
            });
        });
    }

    void SetBusy(bool busy)
    {
        foreach (Control c in new Control[] { addFiles, addFolder, remove, clear, browseOut, recurse, overwrite, history, content, level, blur, xbrz, style, format, snap, qf, threads, reset, run, preview, outBox }) c.Enabled = !busy;
        cancel.Enabled = busy; UseWaitCursor = busy; compare.Enabled = !busy && lastOutputs.Count > 0;
    }

    static Color Map(ConsoleColor? c) => c switch
    {
        ConsoleColor.Green => Color.FromArgb(110, 210, 130), ConsoleColor.Yellow => Color.FromArgb(235, 195, 90), ConsoleColor.Red => Color.FromArgb(255, 110, 110),
        ConsoleColor.Cyan => Color.FromArgb(100, 200, 235), ConsoleColor.Gray or ConsoleColor.DarkGray => Color.FromArgb(150, 150, 150), ConsoleColor.White => Color.White, _ => LogFore
    };

    void Append(string s, Color c)
    {
        log.SelectionStart = log.TextLength; log.SelectionLength = 0; log.SelectionColor = c;
        log.AppendText(s + Environment.NewLine); log.SelectionColor = log.ForeColor; log.ScrollToCaret();
    }

    void ShowGuide()
    {
        const string text =
            "WHAT HAPPENS TO EACH FILE (Auto)\r\n" +
            "  Photo (soft edges, texture)            JPEG below the quality threshold: level 4. The picture is denoised in the DCT domain over\r\n" +
            "                                         all 64 block positions, then chroma is smoothed along the luma edges.\r\n" +
            "  Art (anime, flat color, documents)    JPEG below the threshold: level 6. The same denoising, then everything flat is smoothed;\r\n" +
            "                                         screentone, dither and fine lines are detected and left alone.\r\n" +
            "  In between (painting-like, photo 0.50) the art method at level 4.\r\n" +
            "  Pixel art (1-px edges or an exact NN   restoration if JPEG, undo the nearest-neighbor enlargement, optional palette snap,\r\n" +
            "            enlargement)                 crisp nearest-neighbor upscale to about 1024 px -> PNG (transparency kept).\r\n" +
            "                                         Clean pixel art already at an integer scale is left alone.\r\n" +
            "  Animated pixel art (GIF/PNG/WebP)      the same for every frame -> animated PNG with the original timing\r\n" +
            "  Any JPEG at/above the threshold        re-saved losslessly: reversible JPEG XL (about 20% smaller), verified by rebuilding the\r\n" +
            "                                         original bytes; with PNG or JPEG output: same JPEG with optimized Huffman tables\r\n" +
            "  Saved again, or saved as PNG           a JPEG re-saved at a higher quality, and a PNG / BMP / WebP that was a JPEG once, still\r\n" +
            "                                         show the first compression in their coefficients. When that is certain, the file is\r\n" +
            "                                         restored with the settings of that first compression (Find earlier compression).\r\n" +
            "  12-bit, CMYK, arithmetic, lossless JPEG, MPF / Ultra HDR   copied unchanged (hash-verified)\r\n" +
            "  Other PNG / GIF / BMP / WebP           only processed when they are pixel art; other animations are skipped\r\n" +
            "\r\n" +
            "The restoration itself stays inside what the file allows: its result is a picture that the same JPEG settings would\r\n" +
            "turn into the same file. The optional blur and JPEG output come after it and are not bound by that.\r\n" +
            "\r\n" +
            "RECOMMENDATIONS\r\n" +
            "  Mixed folder, phone photos, screenshots   Auto. Check the class in the log; the CSV log has the confidence.\r\n" +
            "  Anime / illustrations detected as photo   Content = Art: flat color becomes flat again and edges clean.\r\n" +
            "  Large batches, slow computer              Level 2 or 3: most of the gain for photos at a fraction of the time.\r\n" +
            "  Drawings with soft pencil or paint        Level 3 or 4 if level 6 looks too clean.\r\n" +
            "  Pixel art / sprites / game screenshots    Auto. Palette snap only for sprites with few colors (it hurts busy scenes).\r\n" +
            "  Keep pixel art at native size             Pixel-art upscale = Keep size.\r\n" +
            "  Smooth, emulator-filter look              Pixel-art style = Smooth (xBRZ redraws the stair-steps as curves).\r\n" +
            "  Already good JPEGs (quality >= 90)        nothing is restored; they are re-saved losslessly. Raise the threshold to\r\n" +
            "                                            restore more files, lower it (e.g. 1) to make everything lossless.\r\n" +
            "  Viewer cannot open .jxl                   Output format = PNG (lossless, large) or JPEG quality 95 (small).\r\n" +
            "  See what changed                          Compare before / after, after a run: slide the line with the mouse, wheel to zoom,\r\n" +
            "                                            drag to pan, B / A show one side only.\r\n" +
            "\r\n" +
            "Preview runs the detection and shows the decision per file without writing anything.\r\n" +
            "Inputs are never modified. Outputs keep the folder structure, the ICC profile and EXIF.\r\n" +
            "Command line: run unsquish.exe /? in a terminal for the equivalent switches.";
        using var f = new Form { Text = "Unsquish guide", Size = new Size(980, 560), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, ShowInTaskbar = false };
        f.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), Text = text, BackColor = LogBack, ForeColor = LogFore });
        f.ShowDialog(this);
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllLines(SettingsPath, new[] { $"content={content.SelectedIndex}", $"level={level.SelectedIndex}", $"blur={blur.SelectedIndex}", $"xbrz={xbrz.SelectedIndex}", $"style={style.SelectedIndex}", $"format={format.SelectedIndex}",
                $"snap={snap.Value}", $"qf={qf.Value}", $"threads={threads.Value}", $"recurse={recurse.Checked}", $"overwrite={overwrite.Checked}", $"history={history.Checked}", $"width={Width}", $"height={Height}" });
        }
        catch { }
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var kv = File.ReadAllLines(SettingsPath).Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
            void Sel(ComboBox c, string k) { if (kv.TryGetValue(k, out var v) && int.TryParse(v, out int i) && i >= 0 && i < c.Items.Count) c.SelectedIndex = i; }
            void Num(NumericUpDown n, string k) { if (kv.TryGetValue(k, out var v) && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d >= n.Minimum && d <= n.Maximum) n.Value = d; }
            Sel(content, "content"); Sel(level, "level"); Sel(blur, "blur"); Sel(xbrz, "xbrz"); Sel(style, "style"); Sel(format, "format");
            Num(snap, "snap"); Num(qf, "qf"); Num(threads, "threads");
            if (kv.TryGetValue("recurse", out var r)) recurse.Checked = r == "True"; if (kv.TryGetValue("overwrite", out var ow)) overwrite.Checked = ow == "True"; if (kv.TryGetValue("history", out var hs)) history.Checked = hs == "True";
            if (kv.TryGetValue("width", out var w) && kv.TryGetValue("height", out var h) && int.TryParse(w, out int wi) && int.TryParse(h, out int hi) && wi >= MinimumSize.Width && hi >= MinimumSize.Height) Size = new Size(wi, hi);
        }
        catch { }
    }
}
