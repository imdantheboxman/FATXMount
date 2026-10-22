namespace FatxMount;

/// <summary>Modal progress window for long operations. The caller starts the work from its Shown event and closes it when done.</summary>
public sealed class ProgressDialog : Form
{
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1000, Dock = DockStyle.Top, Height = 26, Margin = new Padding(0, 10, 0, 14) };
    private readonly Label _lbl = new() { Text = "Starting…", AutoSize = true, Font = Theme.Base(11f), MaximumSize = new Size(520, 0) };
    private readonly Button _btnCancel = Ui.MakeButton("Cancel");
    private readonly CancellationTokenSource _cts;

    public bool Finished { get; set; }

    public ProgressDialog(string title, CancellationTokenSource cts)
    {
        _cts = cts;
        Text = title;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Base(10f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ControlBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 170);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(22, 20, 22, 16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _bar.Dock = DockStyle.Fill;
        _bar.MinimumSize = new Size(300, 26);
        _btnCancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _btnCancel.Margin = new Padding(0);
        root.Controls.Add(_lbl, 0, 0);
        root.Controls.Add(_bar, 0, 1);
        root.Controls.Add(_btnCancel, 0, 2);
        Controls.Add(root);

        _btnCancel.Click += (_, _) =>
        {
            _btnCancel.Enabled = false;
            _lbl.Text = "Cancelling…";
            _cts.Cancel();
        };
        FormClosing += (_, e) => { if (!Finished) e.Cancel = true; };   // can only close when the work has ended
    }

    public void Report(double fraction, string text)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _bar.Value = (int)Math.Clamp(fraction * 1000, 0, 1000);
                if (_btnCancel.Enabled) _lbl.Text = text;
            });
        }
        catch { /* dialog is closing */ }
    }
}
