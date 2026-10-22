using System.Text;

namespace FatxMount;

/// <summary>Chooses the console type and shows the exact partitions that will be created on the whole drive.</summary>
public sealed class PrepareDriveDialog : Form
{
    private readonly string _disk;
    private readonly long _length;

    private readonly RadioButton _rbXbox = new() { Text = "Original Xbox  (modded console)", AutoSize = true, Checked = true, Font = Theme.Base(10.5f) };
    private readonly RadioButton _rb360 = new() { Text = "Xbox 360  (modded console)", AutoSize = true, Font = Theme.Base(10.5f) };
    private readonly ComboBox _cboMethod = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(11f), Width = 540 };
    private readonly Label _lblTarget = new() { AutoSize = true, MaximumSize = new Size(600, 0), Font = Theme.Base(10.5f, FontStyle.Bold) };
    private readonly TextBox _txtLayout = new()
    {
        Multiline = true, ReadOnly = true, Font = new Font("Consolas", 9.5f), Width = 600, Height = 170,
        BackColor = Color.FromArgb(248, 248, 248), BorderStyle = BorderStyle.FixedSingle, ScrollBars = ScrollBars.Vertical
    };
    private readonly Label _lblNote = new() { AutoSize = true, MaximumSize = new Size(600, 0) };
    private readonly TextBox _txtConfirm = new() { Width = 220, Font = Theme.Base(12f), CharacterCasing = CharacterCasing.Upper };
    private readonly Button _btnOk = Ui.MakeButton("Create layout", danger: true);
    private readonly Button _btnCancel = Ui.MakeButton("Cancel");

    private List<LayoutPartition>? _parts;
    private string? _problem;

    public bool Is360 => _rb360.Checked;
    public bool FullErase => _cboMethod.SelectedIndex == 1;
    public IReadOnlyList<LayoutPartition> Partitions => _parts ?? new List<LayoutPartition>();

    public PrepareDriveDialog(string diskText, long deviceLength)
    {
        _disk = diskText; _length = deviceLength;

        Text = "Prepare drive for a console";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Base(10f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _cboMethod.Items.Add("Quick  (write the partitions and empty file systems)");
        _cboMethod.Items.Add("Full  (overwrite every partition with zeros first, slow)");
        _cboMethod.SelectedIndex = 0;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(22, 18, 22, 18) };

        var title = new Label { Text = "Prepare drive for a console", AutoSize = true, Font = Theme.Base(15f, FontStyle.Bold), ForeColor = Theme.Danger, Margin = new Padding(0, 0, 0, 8) };
        _lblTarget.Margin = new Padding(0, 0, 0, 10);

        var consoleLabel = new Label { Text = "Console", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        _rbXbox.Margin = new Padding(0, 0, 24, 0);
        row.Controls.Add(_rbXbox);
        row.Controls.Add(_rb360);

        var methodLabel = new Label { Text = "Method", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        _cboMethod.Margin = new Padding(0, 0, 0, 10);

        var layoutLabel = new Label { Text = "Partitions that will be created", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        _txtLayout.Margin = new Padding(0, 0, 0, 10);
        _lblNote.Margin = new Padding(0, 0, 0, 12);

        var confirmLabel = new Label { Text = "Type ERASE to confirm", AutoSize = true, Font = Theme.Base(10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        _txtConfirm.Margin = new Padding(0, 0, 0, 16);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
        _btnCancel.Margin = new Padding(0);
        _btnOk.Margin = new Padding(10, 0, 0, 0);
        buttons.Controls.Add(_btnCancel);
        buttons.Controls.Add(_btnOk);

        foreach (Control c in new Control[] { title, _lblTarget, consoleLabel, row, methodLabel, _cboMethod, layoutLabel, _txtLayout, _lblNote, confirmLabel, _txtConfirm, buttons })
            root.Controls.Add(c);
        Controls.Add(root);

        _btnOk.DialogResult = DialogResult.OK;
        _btnCancel.DialogResult = DialogResult.Cancel;
        AcceptButton = null;
        CancelButton = _btnCancel;

        _rbXbox.CheckedChanged += (_, _) => Update_();
        _rb360.CheckedChanged += (_, _) => Update_();
        _cboMethod.SelectedIndexChanged += (_, _) => Update_();
        _txtConfirm.TextChanged += (_, _) => Update_();
        Update_();
    }

    private void Update_()
    {
        _parts = Is360 ? DriveLayouts.Xbox360(_length, out _problem) : DriveLayouts.OriginalXbox(_length, out _problem);
        _lblTarget.Text = $"{_disk}\nEvery file on the partitions below will be permanently erased.";

        var sb = new StringBuilder();
        if (_parts != null)
        {
            sb.AppendLine($"{"Name",-14}{"Offset",-14}{"Size",-12}FS");
            foreach (var p in _parts)
                sb.AppendLine($"{p.Name,-14}0x{p.Offset,-12:X}{Ui.FormatSize(p.Size),-12}{p.FileSystem}");
            if (!Is360 && _length > KnownPartitions.OriginalXboxLimit)
                sb.AppendLine($"\n{Ui.FormatSize(_length - KnownPartitions.OriginalXboxLimit)} beyond 128 GiB stays unpartitioned.");
        }
        else sb.Append(_problem);
        _txtLayout.Text = sb.ToString();

        if (Is360)
        {
            _lblNote.Text = "EXPERIMENTAL. For modded (RGH / JTAG) consoles only. These offsets are written from memory of the standard Xbox 360 layout and " +
                            "have not been verified against a console. This tool cannot create the security sector a stock console requires. " +
                            "If the console does not accept the drive, format it from the console's own storage menu.";
            _lblNote.ForeColor = Theme.Danger;
        }
        else
        {
            _lblNote.Text = "For modded consoles with an unlocked drive. Creates the standard X, Y, Z, C and E partitions. " +
                            "A stock console needs a drive locked with its own key, which this tool cannot do. " +
                            "C is empty afterwards, so install your dashboard or BIOS files onto it.";
            _lblNote.ForeColor = Theme.Muted;
        }

        _btnOk.Enabled = _parts != null && string.Equals(_txtConfirm.Text.Trim(), "ERASE", StringComparison.OrdinalIgnoreCase);
    }
}
