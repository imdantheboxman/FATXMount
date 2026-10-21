using System.Diagnostics;

namespace FatxMount;

public sealed class MainForm : Form
{
    private sealed class DiskItem
    {
        public string Target = "";
        public string Text = "";
        public override string ToString() => Text;
    }

    private sealed class PartRow
    {
        public string Name = "";
        public long Offset;
        public long? Size;
        public bool Custom;
    }

    private readonly ComboBox _cboDisk = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
    private readonly Button _btnRefresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _btnImage = new() { Text = "Open image file…", AutoSize = true };

    private readonly ListView _lvParts = NewList(("Partition", 170), ("Offset", 120), ("Size", 100), ("Signature", 90));
    private readonly TextBox _txtOffset = new() { Width = 140, Enabled = false, PlaceholderText = "0x130EB0000" };
    private readonly TextBox _txtSize = new() { Width = 140, Enabled = false, PlaceholderText = "blank = to end" };

    private readonly ComboBox _cboDrive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly CheckBox _chkRo = new() { Text = "Read-only", AutoSize = true, Checked = true };
    private readonly Button _btnMount = new() { Text = "Mount", AutoSize = true };

    private readonly ListView _lvMounted = NewList(("Drive", 60), ("Source", 260), ("Partition", 130), ("Mode", 90), ("Format", 190));
    private readonly Button _btnUnmount = new() { Text = "Unmount selected", AutoSize = true };
    private readonly Button _btnExplorer = new() { Text = "Open in Explorer", AutoSize = true };

    private readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

    private readonly List<MountSession> _mounts = new();
    private bool _warnedWrite;

    public MainForm()
    {
        Text = "FatxMount – Xbox FATX / XTAF mounter";
        ClientSize = new Size(860, 760);
        MinimumSize = new Size(760, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        // 1. Source
        var src = Group("1. Source disk or image");
        var srcFlow = Flow();
        srcFlow.Controls.AddRange(new Control[] { _cboDisk, _btnRefresh, _btnImage });
        src.Controls.Add(srcFlow);
        root.Controls.Add(src, 0, 0);

        // 2. Partition
        var parts = Group("2. Partition");
        var partsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        partsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        partsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _lvParts.Dock = DockStyle.Fill;
        var custom = Flow();
        custom.Controls.Add(new Label { Text = "Custom offset:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
        custom.Controls.Add(_txtOffset);
        custom.Controls.Add(new Label { Text = "size:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        custom.Controls.Add(_txtSize);
        custom.Controls.Add(new Label { Text = "(only used when “Custom…” is selected; decimal or 0x-hex)", AutoSize = true, Padding = new Padding(8, 6, 0, 0), ForeColor = SystemColors.GrayText });
        partsLayout.Controls.Add(_lvParts, 0, 0);
        partsLayout.Controls.Add(custom, 0, 1);
        parts.Controls.Add(partsLayout);
        root.Controls.Add(parts, 0, 1);

        // 3. Mount options
        var opt = Group("3. Mount");
        var optFlow = Flow();
        optFlow.Controls.Add(new Label { Text = "Drive letter:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        optFlow.Controls.Add(_cboDrive);
        optFlow.Controls.Add(_chkRo);
        optFlow.Controls.Add(_btnMount);
        opt.Controls.Add(optFlow);
        root.Controls.Add(opt, 0, 2);

        // Mounted volumes
        var mounted = Group("Mounted volumes");
        var mLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        mLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _lvMounted.Dock = DockStyle.Fill;
        var mButtons = Flow();
        mButtons.Controls.AddRange(new Control[] { _btnUnmount, _btnExplorer });
        mLayout.Controls.Add(_lvMounted, 0, 0);
        mLayout.Controls.Add(mButtons, 0, 1);
        mounted.Controls.Add(mLayout);
        root.Controls.Add(mounted, 0, 3);

        // Log
        var log = Group("Log");
        log.Controls.Add(_txtLog);
        root.Controls.Add(log, 0, 4);

        Controls.Add(root);

        // events
        _btnRefresh.Click += async (_, _) => await RefreshDisksAsync();
        _btnImage.Click += OnOpenImage;
        _cboDisk.SelectedIndexChanged += async (_, _) => await LoadPartitionsAsync();
        _lvParts.SelectedIndexChanged += (_, _) => UpdateCustomEnabled();
        _btnMount.Click += OnMount;
        _btnUnmount.Click += async (_, _) => await UnmountSelectedAsync();
        _btnExplorer.Click += OnOpenExplorer;
        _chkRo.CheckedChanged += (_, _) =>
        {
            if (!_chkRo.Checked) Log("The HDD will be mounted with write access");
        };
        FatxFileSystem.Log = Log;
        FormClosing += OnFormClosing;
        Shown += async (_, _) =>
        {
            Log($"Diagnostic log: {CrashLog.LogPath}");
            FillDriveLetters();
            await RefreshDisksAsync();
        };
    }

    // ------------------------------------------------------------------ UI helpers

    private static GroupBox Group(string title) => new() { Text = title, Dock = DockStyle.Fill, Padding = new Padding(6), AutoSize = false };

    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };

    private static ListView NewList(params (string name, int width)[] cols)
    {
        var lv = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true };
        foreach (var (n, w) in cols) lv.Columns.Add(n, w);
        return lv;
    }

    private static string Fmt(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GiB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MiB" : $"{bytes} B";

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

    private void UpdateCustomEnabled()
    {
        bool custom = _lvParts.SelectedItems.Count > 0 && _lvParts.SelectedItems[0].Tag is PartRow { Custom: true };
        _txtOffset.Enabled = _txtSize.Enabled = custom;
    }

    // ------------------------------------------------------------------ disks

    private async Task RefreshDisksAsync()
    {
        _btnRefresh.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var found = await Task.Run(ScanDisks);
            var images = _cboDisk.Items.OfType<DiskItem>().Where(d => !int.TryParse(d.Target, out _)).ToList();
            string? previous = (_cboDisk.SelectedItem as DiskItem)?.Target;

            _cboDisk.Items.Clear();
            foreach (var d in found) _cboDisk.Items.Add(d);
            foreach (var i in images) _cboDisk.Items.Add(i);

            var again = _cboDisk.Items.OfType<DiskItem>().FirstOrDefault(d => d.Target == previous)
                        ?? _cboDisk.Items.OfType<DiskItem>().FirstOrDefault(d => d.Text.Contains("Xbox"));
            if (again != null) _cboDisk.SelectedItem = again;
            else if (_cboDisk.Items.Count > 0) _cboDisk.SelectedIndex = 0;

            Log($"Found {found.Count} physical disk(s).");
        }
        catch (Exception ex) { Log($"Disk scan failed: {ex.Message}"); }
        finally { _btnRefresh.Enabled = true; UseWaitCursor = false; }
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
                string text = $"Disk {i}  ({Fmt(dev.Length)})" + (hits.Count > 0 ? $"  –  Xbox partitions: {string.Join(", ", hits)}" : "");
                list.Add(new DiskItem { Target = i.ToString(), Text = text });
            }
            catch { /* no such disk */ }
        }
        return list;
    }

    private void OnOpenImage(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog { Title = "Select a raw disk / partition image", Filter = "All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var item = new DiskItem { Target = dlg.FileName, Text = $"Image: {dlg.FileName}" };
        _cboDisk.Items.Add(item);
        _cboDisk.SelectedItem = item;
    }

    // ------------------------------------------------------------------ partitions

    private async Task LoadPartitionsAsync()
    {
        _lvParts.Items.Clear();
        UpdateCustomEnabled();
        if (_cboDisk.SelectedItem is not DiskItem d) return;

        List<(PartRow row, string? sig, long size)> rows;
        try
        {
            rows = await Task.Run(() =>
            {
                using var dev = BlockDevice.Open(d.Target, writable: false);
                var result = new List<(PartRow, string?, long)>();
                var raw = new PartRow { Name = "Raw volume / image (offset 0)", Offset = 0, Size = null };
                result.Add((raw, FatxVolume.GetSignature(dev, 0), dev.Length));
                foreach (var k in KnownPartitions.All)
                {
                    long size = k.Size ?? Math.Max(0, dev.Length - k.Offset);
                    result.Add((new PartRow { Name = k.Name, Offset = k.Offset, Size = k.Size },
                                FatxVolume.GetSignature(dev, k.Offset), size));
                }
                return result;
            });
        }
        catch (Exception ex) { Log($"Cannot read {d.Text}: {ex.Message}"); return; }

        foreach (var (row, sig, size) in rows)
        {
            var li = new ListViewItem(row.Name) { Tag = row };
            li.SubItems.Add($"0x{row.Offset:X}");
            li.SubItems.Add(size > 0 ? Fmt(size) : "-");
            li.SubItems.Add(sig ?? "–");
            if (sig != null) li.Font = new Font(li.Font, FontStyle.Bold);
            _lvParts.Items.Add(li);
        }
        var customItem = new ListViewItem("Custom…") { Tag = new PartRow { Name = "Custom", Custom = true } };
        customItem.SubItems.Add("enter below"); customItem.SubItems.Add(""); customItem.SubItems.Add("");
        _lvParts.Items.Add(customItem);

        var first = _lvParts.Items.Cast<ListViewItem>().FirstOrDefault(i => i.SubItems[3].Text != "–");
        if (first != null) first.Selected = true;
    }

    // ------------------------------------------------------------------ mount / unmount

    private async void OnMount(object? sender, EventArgs e)
    {
        if (_cboDisk.SelectedItem is not DiskItem d) { Warn("Choose a disk or image first."); return; }
        if (_lvParts.SelectedItems.Count == 0 || _lvParts.SelectedItems[0].Tag is not PartRow p) { Warn("Choose a partition."); return; }
        if (_cboDrive.SelectedItem is not string letter) { Warn("No free drive letter available."); return; }

        long off = p.Offset;
        long? size = p.Size;
        if (p.Custom)
        {
            try
            {
                off = ParseNum(_txtOffset.Text);
                size = string.IsNullOrWhiteSpace(_txtSize.Text) ? null : ParseNum(_txtSize.Text);
            }
            catch { Warn("Offset/size must be a decimal number or 0x-prefixed hex."); return; }
        }

        bool ro = _chkRo.Checked;
        if (!ro && !_warnedWrite)
        {
            _warnedWrite = true;
        }

        _btnMount.Enabled = false;
        UseWaitCursor = true;
        Log($"Mounting {p.Name} from {d.Text} on {letter}: ({(ro ? "read-only" : "read/write")})…");
        try
        {
            var session = await Task.Run(() => MountSession.Start(d.Target, p.Name, off, size, ro, letter));
            _mounts.Add(session);

            var li = new ListViewItem($"{session.MountPoint[0]}:") { Tag = session };
            li.SubItems.Add(d.Text);
            li.SubItems.Add(p.Name);
            li.SubItems.Add(ro ? "read-only" : "read/write");
            li.SubItems.Add(session.Volume.Flavor);
            _lvMounted.Items.Add(li);

            Log($"Mounted on {session.MountPoint} – {session.Volume.ClusterCount} clusters × {session.Volume.ClusterSize} B, {session.Volume.FreeClusters} free.");
            FillDriveLetters();
        }
        catch (Exception ex)
        {
            Log($"Mount failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Mount failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnMount.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private async Task UnmountSelectedAsync()
    {
        if (_lvMounted.SelectedItems.Count == 0) return;
        var li = _lvMounted.SelectedItems[0];
        if (li.Tag is not MountSession s) return;

        _btnUnmount.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await Task.Run(s.Dispose);   // flushes, unmounts, closes the device
            _mounts.Remove(s);
            _lvMounted.Items.Remove(li);
            Log($"Unmounted {s.MountPoint}.");
            FillDriveLetters();
        }
        catch (Exception ex) { Log($"Unmount failed: {ex.Message}"); }
        finally { _btnUnmount.Enabled = true; UseWaitCursor = false; }
    }

    private void OnOpenExplorer(object? sender, EventArgs e)
    {
        if (_lvMounted.SelectedItems.Count == 0 || _lvMounted.SelectedItems[0].Tag is not MountSession s) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", s.MountPoint)); }
        catch (Exception ex) { Log($"Cannot open Explorer: {ex.Message}"); }
    }

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
            try { m.Dispose(); } catch { }
        }
        _mounts.Clear();
        MountSession.ShutdownDokan();
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, "FatxMount", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
