using System.Diagnostics;
using System.Reflection;

namespace FatxMount;

public sealed class MainForm : Form
{
    private sealed class DiskItem
    {
        public string Target = "";
        public string Text = "";
        public bool IsPhysical;
        public bool HasData360;
        public bool HasXbox;
        public override string ToString() => Text;
    }

    private sealed class PartRow
    {
        public string Name = "";
        public long Offset;
        public long? Size;
        public bool Custom;
        public string? Sig;
    }

    private static string VersionText =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    // --- source
    private readonly ComboBox _cboDisk = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(12f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 10, 0), DropDownWidth = 700 };
    private readonly Button _btnRefresh = Ui.MakeButton("Refresh");
    private readonly Button _btnImage = Ui.MakeButton("Open image…");

    // --- partition
    private readonly ListView _lvParts = Ui.MakeList(36, ("Partition", 230), ("Offset", 160), ("Size", 130), ("File system", 200));
    private readonly Label _lblDetected = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 8, 0, 6), MaximumSize = new Size(900, 0) };
    private readonly TextBox _txtOffset = new() { Width = 170, Enabled = false, PlaceholderText = "0x130EB0000", Font = Theme.Base(10.5f), Anchor = AnchorStyles.Left };
    private readonly TextBox _txtSize = new() { Width = 170, Enabled = false, PlaceholderText = "blank = to end of disk", Font = Theme.Base(10.5f), Anchor = AnchorStyles.Left };
    private readonly CheckBox _chkAuto = new() { Text = "Auto-detect and select the Data360 (or first Xbox) partition", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Font = Theme.Base(10f) };
    private readonly Button _btnFormat = Ui.MakeButton("Format / Erase…", danger: true);
    private readonly Button _btnPrepare = Ui.MakeButton("Prepare drive for console…", danger: true);

    // --- mount
    private readonly ComboBox _cboDrive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(12f), Width = 110, MinimumSize = new Size(110, 0), Anchor = AnchorStyles.Left };
    private readonly CheckBox _chkRo = new() { Text = "Read-only", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(18, 0, 0, 0), Font = Theme.Base(10.5f) };
    private readonly CheckBox _chkJournal = new() { Text = "Journaling (crash recovery)", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(18, 0, 0, 0), Font = Theme.Base(10.5f) };
    private readonly Button _btnMount = Ui.MakeButton("Mount", primary: true);

    // --- mounted
    private readonly ListView _lvMounted = Ui.MakeList(34, ("Drive", 90), ("Source", 330), ("Partition", 170), ("Mode", 130), ("Format", 250));
    private readonly Button _btnUnmount = Ui.MakeButton("Unmount selected");
    private readonly Button _btnExplorer = Ui.MakeButton("Open in Explorer");

    // --- log
    private readonly TextBox _txtLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
        BackColor = Color.White, Font = new Font("Consolas", 9.5f), Dock = DockStyle.Fill
    };

    private readonly List<MountSession> _mounts = new();
    private bool _busy;

    public MainForm()
    {
        Text = "FatxMount – Xbox FATX / XTAF drive mounter";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Base(10f);
        BackColor = Theme.Back;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        ClientSize = new Size(Math.Min(1060, wa.Width - 60), Math.Min(940, wa.Height - 90));
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;

        // ---------------- header band
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Header };
        header.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Theme.Accent });
        header.Controls.Add(new Label
        {
            Text = "FatxMount", Font = Theme.Base(22f, FontStyle.Bold), ForeColor = Color.White,
            AutoSize = true, Location = new Point(28, 6), BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = $"Mount Xbox (FATX) and Xbox 360 (XTAF) drives in Windows Explorer   ·   v{VersionText}",
            Font = Theme.Base(10f), ForeColor = Color.FromArgb(190, 190, 190),
            AutoSize = true, Location = new Point(31, 52), BackColor = Color.Transparent
        });

        // ---------------- 1. source
        var srcRow = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = false, Height = 56, MinimumSize = new Size(0, 56) };
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        srcRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _cboDisk.MinimumSize = new Size(320, 0);
        _btnImage.Margin = new Padding(0);
        srcRow.Controls.Add(_cboDisk, 0, 0);
        srcRow.Controls.Add(_btnRefresh, 1, 0);
        srcRow.Controls.Add(_btnImage, 2, 0);

        // ---------------- 2. partition
        _lvParts.Dock = DockStyle.Fill;
        _lvParts.MinimumSize = new Size(300, 190);
        var customRow = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        for (int i = 0; i < 4; i++) customRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        customRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        customRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        customRow.Controls.Add(new Label { Text = "Custom offset:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        customRow.Controls.Add(_txtOffset, 1, 0);
        customRow.Controls.Add(new Label { Text = "size:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(16, 0, 8, 0) }, 2, 0);
        customRow.Controls.Add(_txtSize, 3, 0);
        customRow.Controls.Add(new Label { Text = "used when “Custom…” is selected (decimal or 0x-hex)", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Muted, Margin = new Padding(16, 0, 0, 0) }, 4, 0);

        var partActions = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        partActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        partActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        partActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        partActions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _btnFormat.Margin = new Padding(0);
        partActions.Controls.Add(_chkAuto, 0, 0);
        partActions.Controls.Add(_btnPrepare, 1, 0);
        partActions.Controls.Add(_btnFormat, 2, 0);

        var partLayout = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Height = 440, MinimumSize = new Size(0, 440) };
        partLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        partLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        partLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        partLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        partLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        partLayout.Controls.Add(_lvParts, 0, 0);
        partLayout.Controls.Add(_lblDetected, 0, 1);
        partLayout.Controls.Add(customRow, 0, 2);
        partLayout.Controls.Add(partActions, 0, 3);

        // ---------------- 3. mount options
        var mountRow = new TableLayoutPanel { ColumnCount = 6, RowCount = 1, AutoSize = false, Height = 60, MinimumSize = new Size(0, 60) };
        for (int i = 0; i < 4; i++) mountRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mountRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mountRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _btnMount.Margin = new Padding(0);
        mountRow.Controls.Add(new Label { Text = "Drive letter", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        mountRow.Controls.Add(_cboDrive, 1, 0);
        mountRow.Controls.Add(_chkRo, 2, 0);
        mountRow.Controls.Add(_chkJournal, 3, 0);
        mountRow.Controls.Add(_btnMount, 5, 0);

        // ---------------- mounted volumes
        _lvMounted.Dock = DockStyle.Fill;
        _lvMounted.MinimumSize = new Size(300, 150);
        _txtLog.MinimumSize = new Size(300, 190);
        _txtLog.Height = 190;
        var mButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        mButtons.Controls.Add(_btnUnmount);
        mButtons.Controls.Add(_btnExplorer);
        var mLayout = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Height = 320, MinimumSize = new Size(0, 320) };
        mLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mLayout.Controls.Add(_lvMounted, 0, 0);
        mLayout.Controls.Add(mButtons, 0, 1);

        // ---------------- assemble
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 5, Padding = new Padding(18, 16, 18, 12), BackColor = Theme.Back
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Card("1   Source disk or image", srcRow), 0, 0);
        root.Controls.Add(new Card("2   Partition", partLayout), 0, 1);
        root.Controls.Add(new Card("3   Mount", mountRow), 0, 2);
        root.Controls.Add(new Card("Mounted volumes", mLayout), 0, 3);
        root.Controls.Add(new Card("Activity log", _txtLog), 0, 4);

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Back };
        body.Controls.Add(root);
        var menu = new MenuStrip { Dock = DockStyle.Top, Font = Theme.Base(10f) };
        var mFile = new ToolStripMenuItem("&File");
        mFile.DropDownItems.Add("Open image…", null, (_, _) => OnOpenImage(null, EventArgs.Empty));
        mFile.DropDownItems.Add("Refresh disks", null, async (_, _) => await RefreshDisksAsync());
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("Exit", null, (_, _) => Close());
        var mVol = new ToolStripMenuItem("&Volumes");
        mVol.DropDownItems.Add("Mount selected partition", null, (_, _) => OnMount(null, EventArgs.Empty));
        mVol.DropDownItems.Add("Unmount selected", null, async (_, _) => await UnmountSelectedAsync());
        mVol.DropDownItems.Add("Unmount all", null, async (_, _) => await UnmountAllAsync());
        var mTools = new ToolStripMenuItem("&Tools");
        mTools.DropDownItems.Add("Format / Erase partition…", null, (_, _) => OnFormat(null, EventArgs.Empty));
        mTools.DropDownItems.Add("Prepare drive for console…", null, (_, _) => OnPrepare(null, EventArgs.Empty));
        menu.Items.AddRange(new ToolStripItem[] { mFile, mVol, mTools });
        MainMenuStrip = menu;

        Controls.Add(body);
        Controls.Add(header);
        Controls.Add(menu);

        // ---------------- events
        _btnRefresh.Click += async (_, _) => await RefreshDisksAsync();
        _btnImage.Click += OnOpenImage;
        _cboDisk.SelectedIndexChanged += async (_, _) => await LoadPartitionsAsync();
        _lvParts.SelectedIndexChanged += (_, _) => UpdateSelectionState();
        _btnMount.Click += OnMount;
        _btnFormat.Click += OnFormat;
        _btnPrepare.Click += OnPrepare;
        _btnUnmount.Click += async (_, _) => await UnmountSelectedAsync();
        _btnExplorer.Click += OnOpenExplorer;
        _chkRo.CheckedChanged += (_, _) => _chkJournal.Enabled = !_chkRo.Checked;
        _chkJournal.Enabled = !_chkRo.Checked;
        FatxFileSystem.Log = Log;
        FormClosing += OnFormClosing;
        Shown += async (_, _) =>
        {
            Log($"FatxMount {VersionText} started. Diagnostic log: {CrashLog.LogPath}");
            FillDriveLetters();
            Ui.FitLastColumn(_lvParts);
            Ui.FitLastColumn(_lvMounted);
            await RefreshDisksAsync();
        };
    }

    // ------------------------------------------------------------------ helpers

    private static long ParseNum(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(s[2..], 16) : long.Parse(s);
    }

    public void Log(string msg)
    {
        CrashLog.Write(msg);
        try
        {
            void Append() => _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            if (IsDisposed) return;
            if (IsHandleCreated && InvokeRequired) BeginInvoke(Append); else Append();
        }
        catch { /* window is closing */ }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _btnMount.Enabled = !busy;
        _btnFormat.Enabled = !busy && _lvParts.SelectedItems.Count > 0;
        _btnPrepare.Enabled = !busy;
        _btnRefresh.Enabled = !busy;
        _btnUnmount.Enabled = !busy;
    }

    private void FillDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        foreach (var m in _mounts) used.Add(m.MountPoint[0]);

        string? previous = _cboDrive.SelectedItem as string;
        _cboDrive.Items.Clear();
        for (char c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c)) _cboDrive.Items.Add(c.ToString());
        if (previous != null && _cboDrive.Items.Contains(previous)) _cboDrive.SelectedItem = previous;
        else if (_cboDrive.Items.Count > 0) _cboDrive.SelectedIndex = 0;
    }

    private void UpdateSelectionState()
    {
        bool has = _lvParts.SelectedItems.Count > 0;
        bool custom = has && _lvParts.SelectedItems[0].Tag is PartRow { Custom: true };
        _txtOffset.Enabled = _txtSize.Enabled = custom;
        _btnFormat.Enabled = has && !_busy;
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, "FatxMount", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ------------------------------------------------------------------ disks

    private async Task RefreshDisksAsync()
    {
        SetBusy(true);
        try
        {
            var found = await Task.Run(ScanDisks);
            var images = _cboDisk.Items.OfType<DiskItem>().Where(d => !d.IsPhysical).ToList();
            string? previous = (_cboDisk.SelectedItem as DiskItem)?.Target;

            _cboDisk.Items.Clear();
            foreach (var d in found) _cboDisk.Items.Add(d);
            foreach (var i in images) _cboDisk.Items.Add(i);

            var items = _cboDisk.Items.OfType<DiskItem>().ToList();
            var pick = items.FirstOrDefault(d => d.Target == previous)
                       ?? items.FirstOrDefault(d => d.HasData360)
                       ?? items.FirstOrDefault(d => d.HasXbox)
                       ?? items.FirstOrDefault();
            if (pick != null) _cboDisk.SelectedItem = pick;

            Log($"Found {found.Count} physical disk(s).");
        }
        catch (Exception ex) { Log($"Disk scan failed: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private static List<DiskItem> ScanDisks()
    {
        var list = new List<DiskItem>();
        for (int i = 0; i < 32; i++)
        {
            try
            {
                using var dev = BlockDevice.Open(i.ToString(), writable: false);
                var hits = KnownPartitions.All
                    .Where(p => FatxVolume.GetSignature(dev, p.Offset) != null)
                    .Select(p => p.Name).ToList();
                string text = $"Disk {i}   ({Ui.FormatSize(dev.Length)})";
                if (hits.Count > 0)
                    text += "   –   Xbox partitions: " + string.Join(", ", hits.Select(h => h == "Data360" ? "Data360 (Xbox 360)" : h));
                list.Add(new DiskItem
                {
                    Target = i.ToString(), Text = text, IsPhysical = true,
                    HasXbox = hits.Count > 0, HasData360 = hits.Contains("Data360")
                });
            }
            catch { /* no such disk */ }
        }
        return list;
    }

    private void OnOpenImage(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog { Title = "Select a raw disk / partition image", Filter = "All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var item = new DiskItem { Target = dlg.FileName, Text = $"Image: {dlg.FileName}", IsPhysical = false };
        _cboDisk.Items.Add(item);
        _cboDisk.SelectedItem = item;
    }

    // ------------------------------------------------------------------ partitions

    private static string FriendlyName(string name) => name switch
    {
        "X" or "Y" or "Z" => $"{name}   (Xbox cache)",
        "C" => "C   (Xbox system)",
        "E" => "E   (Xbox user data)",
        "Data360" => "Data360   (Xbox 360 data)",
        _ => name
    };

    private async Task LoadPartitionsAsync()
    {
        _lvParts.Items.Clear();
        _lblDetected.Text = "";
        UpdateSelectionState();
        if (_cboDisk.SelectedItem is not DiskItem d) return;

        List<(PartRow row, long size)> rows;
        try
        {
            rows = await Task.Run(() =>
            {
                using var dev = BlockDevice.Open(d.Target, writable: false);
                var result = new List<(PartRow, long)>
                {
                    (new PartRow { Name = "Raw volume / image (offset 0)", Offset = 0, Size = null, Sig = FatxVolume.GetSignature(dev, 0) }, dev.Length)
                };
                foreach (var k in KnownPartitions.All)
                {
                    long size = k.Size ?? Math.Max(0, dev.Length - k.Offset);
                    result.Add((new PartRow { Name = k.Name, Offset = k.Offset, Size = k.Size, Sig = FatxVolume.GetSignature(dev, k.Offset) }, size));
                }
                return result;
            });
        }
        catch (Exception ex) { Log($"Cannot read {d.Text}: {ex.Message}"); return; }

        foreach (var (row, size) in rows)
        {
            var li = new ListViewItem(row.Offset == 0 && row.Name.StartsWith("Raw") ? row.Name : FriendlyName(row.Name)) { Tag = row };
            li.SubItems.Add($"0x{row.Offset:X}");
            li.SubItems.Add(size > 0 ? Ui.FormatSize(size) : "–");
            li.SubItems.Add(row.Sig switch { "FATX" => "FATX (original Xbox)", "XTAF" => "XTAF (Xbox 360)", _ => "–" });
            if (row.Sig != null) li.Font = new Font(_lvParts.Font, FontStyle.Bold);
            else li.ForeColor = Theme.Muted;
            _lvParts.Items.Add(li);
        }
        var custom = new ListViewItem("Custom…") { Tag = new PartRow { Name = "Custom", Custom = true } };
        custom.SubItems.Add("enter below"); custom.SubItems.Add(""); custom.SubItems.Add("");
        _lvParts.Items.Add(custom);
        Ui.FitLastColumn(_lvParts);

        ListViewItem? pickItem = null;
        if (_chkAuto.Checked)
        {
            var all = _lvParts.Items.Cast<ListViewItem>().Where(i => i.Tag is PartRow { Sig: not null }).ToList();
            pickItem = all.FirstOrDefault(i => i.Tag is PartRow { Name: "Data360" }) ?? all.FirstOrDefault();
        }

        if (pickItem != null)
        {
            pickItem.Selected = true;
            pickItem.EnsureVisible();
            var pr = (PartRow)pickItem.Tag!;
            _lblDetected.ForeColor = Theme.Accent;
            _lblDetected.Text = pr.Name == "Data360"
                ? "✔  Xbox 360 Data partition detected and selected. Click any other row to override."
                : $"✔  Detected a {pr.Sig} volume ({pickItem.Text.Split(' ')[0]}) and selected it. Click any other row to override.";
        }
        else
        {
            _lblDetected.ForeColor = Theme.Muted;
            _lblDetected.Text = _chkAuto.Checked
                ? "No Xbox signature was found at the standard locations. Pick a partition yourself, or use Custom… with an offset."
                : "Auto-detect is off. Choose a partition.";
        }
    }

    // ------------------------------------------------------------------ mount / unmount

    private bool TryGetTarget(out DiskItem disk, out PartRow part, out long offset, out long? size)
    {
        disk = null!; part = null!; offset = 0; size = null;
        if (_cboDisk.SelectedItem is not DiskItem d) { Warn("Choose a disk or image first."); return false; }
        if (_lvParts.SelectedItems.Count == 0 || _lvParts.SelectedItems[0].Tag is not PartRow p) { Warn("Choose a partition."); return false; }

        disk = d; part = p; offset = p.Offset; size = p.Size;
        if (p.Custom)
        {
            try
            {
                offset = ParseNum(_txtOffset.Text);
                size = string.IsNullOrWhiteSpace(_txtSize.Text) ? null : ParseNum(_txtSize.Text);
            }
            catch { Warn("Offset and size must be a decimal number or 0x-prefixed hex."); return false; }
        }
        return true;
    }

    private async void OnMount(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (!TryGetTarget(out var d, out var p, out long off, out long? size)) return;
        if (_cboDrive.SelectedItem is not string letter) { Warn("No free drive letter available."); return; }

        if (_mounts.Any(m => m.IsSameTarget(d.Target, off)))
        {
            Warn("That partition is already mounted. Unmount it first.");
            return;
        }

        bool ro = _chkRo.Checked;
        bool journaling = _chkJournal.Checked;
        string partName = p.Custom ? $"offset 0x{off:X}" : p.Name;

        SetBusy(true);
        Log($"Mounting {partName} from {d.Text} on {letter}: ({(ro ? "read-only" : "read/write")}{(!ro && journaling ? ", journaled" : "")})…");
        try
        {
            var session = await Task.Run(() => MountSession.Start(d.Target, partName, off, size, ro, letter, journaling));
            _mounts.Add(session);

            var li = new ListViewItem($"{session.MountPoint[0]}:") { Tag = session };
            li.SubItems.Add(d.Text);
            li.SubItems.Add(partName);
            li.SubItems.Add(ro ? "read-only" : (session.Volume.Journaled ? "read/write + journal" : "read/write"));
            li.SubItems.Add(session.Volume.Flavor);
            _lvMounted.Items.Add(li);
            Ui.FitLastColumn(_lvMounted);

            if (session.Volume.RecoveredTransactions > 0)
                Log($"Recovered {session.Volume.RecoveredTransactions} pending change(s) from the journal after an unclean shutdown.");
            Log($"Mounted on {session.MountPoint} – {session.Volume.ClusterCount} clusters × {session.Volume.ClusterSize} B, {session.Volume.FreeClusters} free.");
            FillDriveLetters();
        }
        catch (Exception ex)
        {
            Log($"Mount failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Mount failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task UnmountSelectedAsync()
    {
        if (_busy || _lvMounted.SelectedItems.Count == 0) return;
        var li = _lvMounted.SelectedItems[0];
        if (li.Tag is not MountSession s) return;

        SetBusy(true);
        try
        {
            await Task.Run(s.Dispose);   // unmounts, finishes pending writes, flushes, closes the device
            _mounts.Remove(s);
            _lvMounted.Items.Remove(li);
            Log($"Unmounted {s.MountPoint}.");
            FillDriveLetters();
        }
        catch (Exception ex) { Log($"Unmount failed: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async Task UnmountAllAsync()
    {
        if (_busy || _mounts.Count == 0) return;
        SetBusy(true);
        try
        {
            foreach (var s in _mounts.ToList())
            {
                await Task.Run(s.Dispose);
                _mounts.Remove(s);
                var li = _lvMounted.Items.Cast<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, s));
                if (li != null) _lvMounted.Items.Remove(li);
                Log($"Unmounted {s.MountPoint}.");
            }
            FillDriveLetters();
        }
        catch (Exception ex) { Log($"Unmount failed: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private void OnOpenExplorer(object? sender, EventArgs e)
    {
        if (_lvMounted.SelectedItems.Count == 0 || _lvMounted.SelectedItems[0].Tag is not MountSession s) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", s.MountPoint)); }
        catch (Exception ex) { Log($"Cannot open Explorer: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ format

    private async void OnFormat(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (!TryGetTarget(out var d, out var p, out long off, out long? size)) return;

        if (_mounts.Any(m => string.Equals(m.Source, d.Target, StringComparison.OrdinalIgnoreCase)))
        {
            Warn("A volume from this disk is currently mounted. Unmount it before formatting.");
            return;
        }
        if (d.IsPhysical && d.Target == "0")
        {
            Warn("Disk 0 is normally the disk Windows runs from, so FatxMount will not format it.");
            return;
        }
        if (d.IsPhysical && off < 0x80000)
        {
            Warn("Formatting at the very start of a physical disk would destroy its partition table. " +
                 "Choose one of the Xbox partitions (X, Y, Z, C, E, Data360) or a custom offset of 0x80000 or higher.");
            return;
        }

        SetBusy(true);
        long deviceLength; string? sig; long effSize;
        try
        {
            (deviceLength, sig, effSize) = await Task.Run(() =>
            {
                using var dev = BlockDevice.Open(d.Target, writable: false);
                long end = p.Name == "E" ? Math.Min(dev.Length, KnownPartitions.OriginalXboxLimit) : dev.Length;
                long sz = size ?? (end - off);
                return (dev.Length, FatxVolume.GetSignature(dev, off), sz);
            });
        }
        catch (Exception ex) { SetBusy(false); Log($"Cannot read target: {ex.Message}"); Warn(ex.Message); return; }
        SetBusy(false);

        if (effSize < (1L << 20) || off + effSize > deviceLength)
        {
            Warn("The chosen offset and size do not fit on this disk or are too small to format.");
            return;
        }

        string partName = p.Custom ? $"offset 0x{off:X}" : p.Name;
        using var dlg = new FormatDialog(d.Text, partName, off, effSize, sig, d.IsPhysical, deviceLength);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var targets = new List<(string name, long off, long size)>();
        if (dlg.WholeDrive) targets.AddRange(KnownPartitions.OriginalLayout(deviceLength));
        else targets.Add((partName, off, effSize));

        bool be = dlg.BigEndian;
        uint spc = dlg.SectorsPerCluster;
        var mode = dlg.Mode;
        string what = mode switch
        {
            EraseMode.QuickFormat => "Formatting",
            EraseMode.WipeZero => "Erasing",
            _ => "Erasing and formatting"
        };

        SetBusy(true);
        Log($"{what} {string.Join(", ", targets.Select(t => t.name))} on {d.Text} ({mode}{(mode == EraseMode.WipeZero ? "" : $", {(be ? "XTAF" : "FATX")}, {spc * 512 / 1024} KB clusters")})…");

        Exception? error = null;
        bool cancelled = false;
        using (var cts = new CancellationTokenSource())
        using (var prog = new ProgressDialog(what + "…", cts))
        {
            prog.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var dev = BlockDevice.Open(d.Target, writable: true);
                        Eraser.Run(dev, targets, mode, be, spc, prog.Report, cts.Token);
                    });
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { error = ex; }
                prog.Finished = true;
                prog.Close();
            };
            prog.ShowDialog(this);
        }
        SetBusy(false);

        if (error != null)
        {
            Log($"{what} failed: {error.Message}");
            CrashLog.Write("Format/erase failed:\n" + error);
            MessageBox.Show(this, error.Message, "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else if (cancelled)
        {
            Log("Cancelled. The area may be partly erased and is not a valid file system until you format it.");
            MessageBox.Show(this, "Cancelled. The area may be partly erased and is not a usable file system until you format it again.",
                "FatxMount", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            Log("Finished.");
            MessageBox.Show(this, "Finished.", "FatxMount", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        await LoadPartitionsAsync();
    }

    // ------------------------------------------------------------------ prepare whole drive

    private async void OnPrepare(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_cboDisk.SelectedItem is not DiskItem d) { Warn("Choose a disk or image first."); return; }
        if (_mounts.Any(m => string.Equals(m.Source, d.Target, StringComparison.OrdinalIgnoreCase)))
        {
            Warn("A volume from this disk is currently mounted. Unmount it first.");
            return;
        }
        if (d.IsPhysical && d.Target == "0")
        {
            Warn("Disk 0 is normally the disk Windows runs from, so FatxMount will not prepare it.");
            return;
        }

        long length;
        SetBusy(true);
        try { length = await Task.Run(() => { using var dev = BlockDevice.Open(d.Target, writable: false); return dev.Length; }); }
        catch (Exception ex) { SetBusy(false); Log($"Cannot read target: {ex.Message}"); Warn(ex.Message); return; }
        SetBusy(false);

        using var dlg = new PrepareDriveDialog(d.Text, length);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var parts = dlg.Partitions.ToList();
        var mode = dlg.FullErase ? EraseMode.ZeroThenFormat : EraseMode.QuickFormat;
        string console = dlg.Is360 ? "Xbox 360" : "original Xbox";

        SetBusy(true);
        Log($"Preparing {d.Text} for a modded {console}: {string.Join(", ", parts.Select(p => p.Name))} ({mode})…");

        Exception? error = null;
        bool cancelled = false;
        using (var cts = new CancellationTokenSource())
        using (var prog = new ProgressDialog("Preparing drive…", cts))
        {
            prog.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var dev = BlockDevice.Open(d.Target, writable: true);
                        Eraser.RunLayout(dev, parts, mode, 32, prog.Report, cts.Token);
                    });
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { error = ex; }
                prog.Finished = true;
                prog.Close();
            };
            prog.ShowDialog(this);
        }
        SetBusy(false);

        if (error != null)
        {
            Log($"Prepare failed: {error.Message}");
            CrashLog.Write("Prepare failed:\n" + error);
            MessageBox.Show(this, error.Message, "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else if (cancelled)
        {
            Log("Cancelled. The drive is partly prepared; run Prepare again before using it.");
            MessageBox.Show(this, "Cancelled. The drive is only partly prepared. Run Prepare again before using it.",
                "FatxMount", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            Log($"Drive prepared for a modded {console}.");
            MessageBox.Show(this, $"Drive prepared for a modded {console}.", "FatxMount", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        await LoadPartitionsAsync();
    }

    // ------------------------------------------------------------------ closing

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_mounts.Count > 0)
        {
            var r = MessageBox.Show(this, $"{_mounts.Count} volume(s) are still mounted. Unmount them and exit?",
                "Exit", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) { e.Cancel = true; return; }
        }
        UseWaitCursor = true;
        foreach (var m in _mounts.ToList())
        {
            try { m.Dispose(); } catch (Exception ex) { CrashLog.Write("Close: " + ex); }
        }
        _mounts.Clear();
        MountSession.ShutdownDokan();
    }
}
