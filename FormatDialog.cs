namespace FatxMount;

/// <summary>Collects format/erase options and demands an explicit typed confirmation.</summary>
public sealed class FormatDialog : Form
{
    private sealed record ClusterChoice(string Text, uint Sectors)
    {
        public override string ToString() => Text;
    }

    private sealed record ModeChoice(string Text, EraseMode Mode)
    {
        public override string ToString() => Text;
    }

    private readonly string _disk, _part;
    private readonly long _offset, _size;
    private readonly string? _sig;
    private readonly bool _physical;
    private readonly long _deviceLength;

    private readonly ComboBox _cboMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(11f), Width = 540 };
    private readonly RadioButton _rbXbox = new() { Text = "Original Xbox  (FATX)", AutoSize = true, Font = Theme.Base(10.5f) };
    private readonly RadioButton _rbX360 = new() { Text = "Xbox 360  (XTAF)", AutoSize = true, Font = Theme.Base(10.5f) };
    private readonly ComboBox _cboCluster = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(11f), Width = 300 };
    private readonly CheckBox _chkWhole = new() { Text = "Apply to the whole original Xbox drive (X, Y, Z, C and E)", AutoSize = true, Font = Theme.Base(10.5f) };
    private readonly Label _lblTarget = new() { AutoSize = true, MaximumSize = new Size(560, 0), Font = Theme.Base(10.5f, FontStyle.Bold) };
    private readonly Label _lblWarn = new() { AutoSize = true, MaximumSize = new Size(560, 0), Font = Theme.Base(10f) };
    private readonly Label _lblFsHint = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.Muted };
    private readonly TextBox _txtConfirm = new() { Width = 220, Font = Theme.Base(12f), CharacterCasing = CharacterCasing.Upper };
    private readonly Button _btnOk = Ui.MakeButton("Start", danger: true);
    private readonly Button _btnCancel = Ui.MakeButton("Cancel");
    private readonly FlowLayoutPanel _fsRow = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 10), WrapContents = false };
    private readonly Label _fsLabel = new() { Text = "File system", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
    private readonly Label _clLabel = new() { Text = "Cluster size", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };

    public EraseMode Mode => (_cboMode.SelectedItem as ModeChoice)?.Mode ?? EraseMode.QuickFormat;
    public bool BigEndian => _rbX360.Checked;
    public uint SectorsPerCluster => (_cboCluster.SelectedItem as ClusterChoice)?.Sectors ?? 32;
    public bool WholeDrive => _chkWhole.Checked && _chkWhole.Enabled;
    private bool WipeOnly => Mode == EraseMode.WipeZero;

    public FormatDialog(string diskText, string partName, long offset, long size, string? existingSignature,
                        bool physicalDisk, long deviceLength)
    {
        _disk = diskText; _part = partName; _offset = offset; _size = size; _sig = existingSignature;
        _physical = physicalDisk; _deviceLength = deviceLength;

        Text = "Format or erase";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Base(10f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _cboMode.Items.Add(new ModeChoice("Quick format  (fast; old data stays on the disk but is unreachable)", EraseMode.QuickFormat));
        _cboMode.Items.Add(new ModeChoice("Full erase with zeros, then format  (one pass)", EraseMode.ZeroThenFormat));
        _cboMode.Items.Add(new ModeChoice("Full erase with random data, then format  (one pass)", EraseMode.RandomThenFormat));
        _cboMode.Items.Add(new ModeChoice("Erase only with zeros  (leave unformatted)", EraseMode.WipeZero));
        _cboMode.SelectedIndex = 0;

        _cboCluster.Items.Add(new ClusterChoice("4 KB clusters", 8));
        _cboCluster.Items.Add(new ClusterChoice("8 KB clusters", 16));
        var def = new ClusterChoice("16 KB clusters (standard)", 32);
        _cboCluster.Items.Add(def);
        _cboCluster.Items.Add(new ClusterChoice("32 KB clusters", 64));
        _cboCluster.Items.Add(new ClusterChoice("64 KB clusters", 128));
        _cboCluster.SelectedItem = def;

        bool x360 = existingSignature == "XTAF" || (existingSignature == null && partName == "Data360");
        _rbX360.Checked = x360;
        _rbXbox.Checked = !x360;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(22, 18, 22, 18)
        };

        var title = new Label { Text = "Format or erase", AutoSize = true, Font = Theme.Base(15f, FontStyle.Bold), ForeColor = Theme.Danger, Margin = new Padding(0, 0, 0, 8) };
        _lblTarget.Margin = new Padding(0, 0, 0, 6);
        _lblWarn.Margin = new Padding(0, 0, 0, 14);

        var modeLabel = new Label { Text = "Method", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        _cboMode.Margin = new Padding(0, 0, 0, 12);

        _rbXbox.Margin = new Padding(0, 0, 24, 0);
        _fsRow.Controls.Add(_rbXbox);
        _fsRow.Controls.Add(_rbX360);

        _cboCluster.Margin = new Padding(0, 0, 0, 10);
        _chkWhole.Margin = new Padding(0, 0, 0, 10);

        _lblFsHint.Text = "Xbox 360 note: a retail 360 hard drive also carries a console-written security sector, which this tool cannot create. " +
                          "Formatting here is meant for the Data partition of a drive the 360 has already set up.";
        _lblFsHint.Margin = new Padding(0, 0, 0, 12);

        var confirmLabel = new Label { Text = "Type ERASE to confirm", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        _txtConfirm.Margin = new Padding(0, 0, 0, 16);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
        _btnCancel.Margin = new Padding(0);
        _btnOk.Margin = new Padding(10, 0, 0, 0);
        buttons.Controls.Add(_btnCancel);
        buttons.Controls.Add(_btnOk);

        foreach (Control c in new Control[] { title, _lblTarget, _lblWarn, modeLabel, _cboMode, _fsLabel, _fsRow, _clLabel, _cboCluster, _chkWhole, _lblFsHint, confirmLabel, _txtConfirm, buttons })
            root.Controls.Add(c);
        Controls.Add(root);

        _btnOk.DialogResult = DialogResult.OK;
        _btnCancel.DialogResult = DialogResult.Cancel;
        AcceptButton = null;          // Enter must never confirm a destructive action by accident
        CancelButton = _btnCancel;

        _cboMode.SelectedIndexChanged += (_, _) => Update_();
        _rbXbox.CheckedChanged += (_, _) => Update_();
        _rbX360.CheckedChanged += (_, _) => Update_();
        _chkWhole.CheckedChanged += (_, _) => Update_();
        _txtConfirm.TextChanged += (_, _) => Update_();
        Update_();
    }

    private void Update_()
    {
        bool fsVisible = !WipeOnly;
        _fsLabel.Visible = _fsRow.Visible = _clLabel.Visible = _cboCluster.Visible = _lblFsHint.Visible = fsVisible;

        bool bigEnough = _physical && _deviceLength >= 0xABE80000L + (512L << 20);
        bool wholeAllowed = bigEnough && (WipeOnly || _rbXbox.Checked);
        _chkWhole.Enabled = wholeAllowed;
        if (!wholeAllowed) _chkWhole.Checked = false;

        string action = Mode switch
        {
            EraseMode.QuickFormat => "Quick format",
            EraseMode.ZeroThenFormat => "Erase with zeros and format",
            EraseMode.RandomThenFormat => "Erase with random data and format",
            _ => "Erase with zeros"
        };
        _btnOk.Text = WipeOnly ? "Erase" : (Mode == EraseMode.QuickFormat ? "Format" : "Erase and format");

        if (WholeDrive)
        {
            _lblTarget.Text = $"{action}: standard original Xbox layout (X, Y, Z, C, E) on {_disk}";
            _lblWarn.Text = "Every file on all five partitions will be permanently erased.";
            _lblWarn.ForeColor = Theme.Danger;
        }
        else
        {
            _lblTarget.Text = $"{action}\nPartition {_part}  ·  offset 0x{_offset:X}  ·  {Ui.FormatSize(_size)}\n{_disk}";
            if (_sig != null)
            {
                _lblWarn.Text = $"This area currently holds a {_sig} volume. Every file on it will be permanently erased.";
                _lblWarn.ForeColor = Theme.Text;
            }
            else
            {
                _lblWarn.Text = "No Xbox file system was detected here, so this area may hold other data. " +
                                "Everything in it will be permanently erased. Make sure this is the right place.";
                _lblWarn.ForeColor = Theme.Danger;
            }
        }
        if (Mode is EraseMode.ZeroThenFormat or EraseMode.RandomThenFormat or EraseMode.WipeZero)
            _lblWarn.Text += " A full erase writes to the entire area and can take a long time on a large drive; you can cancel it from the progress window.";

        _btnOk.Enabled = string.Equals(_txtConfirm.Text.Trim(), "ERASE", StringComparison.OrdinalIgnoreCase);
    }
}
