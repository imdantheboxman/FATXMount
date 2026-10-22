using System.Reflection;

namespace FatxMount;

internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(16, 124, 16);       // Xbox green
    public static readonly Color AccentDark = Color.FromArgb(11, 90, 11);
    public static readonly Color Danger = Color.FromArgb(196, 43, 28);
    public static readonly Color Back = Color.FromArgb(243, 243, 243);
    public static readonly Color CardBorder = Color.FromArgb(221, 221, 221);
    public static readonly Color Text = Color.FromArgb(32, 32, 32);
    public static readonly Color Muted = Color.FromArgb(105, 105, 105);
    public static readonly Color Header = Color.FromArgb(24, 24, 24);

    public static Font Base(float size = 10f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
}

/// <summary>White rounded-feel panel with a title; the content control sits underneath.</summary>
internal sealed class Card : TableLayoutPanel
{
    public Card(string title, Control content, bool fill = false)
    {
        DoubleBuffered = true;
        ColumnCount = 1;
        RowCount = 2;
        Dock = DockStyle.Fill;
        AutoSize = !fill;
        BackColor = Color.White;
        Margin = new Padding(0, 0, 0, 12);
        Padding = new Padding(1);

        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));

        var lbl = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.Base(11f, FontStyle.Bold),
            ForeColor = Theme.Text,
            Margin = new Padding(14, 12, 0, 2)
        };
        content.Margin = new Padding(14, 6, 14, 14);
        content.Dock = DockStyle.Fill;

        Controls.Add(lbl, 0, 0);
        Controls.Add(content, 0, 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CardBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal static class Ui
{
    public static Button MakeButton(string text, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(primary ? 150 : 118, 40),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(12, 0, 12, 0),
            Font = Theme.Base(10f, primary ? FontStyle.Bold : FontStyle.Regular),
            UseVisualStyleBackColor = false
        };
        if (primary)
        {
            b.BackColor = Theme.Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.AccentDark;
        }
        else
        {
            b.BackColor = Color.White;
            b.ForeColor = danger ? Theme.Danger : Theme.Text;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = danger ? Theme.Danger : Color.FromArgb(190, 190, 190);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 236, 236);
        }
        return b;
    }

    /// <summary>Taller, cleaner list: row height is set through a 1px-wide image list.</summary>
    public static ListView MakeList(int rowHeight, params (string name, int width)[] cols)
    {
        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = Theme.Base(10.5f),
            SmallImageList = new ImageList { ImageSize = new Size(1, rowHeight) }
        };
        foreach (var (n, w) in cols) lv.Columns.Add(n, w);

        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(lv, true);
        lv.Resize += (_, _) => FitLastColumn(lv);
        return lv;
    }

    public static void FitLastColumn(ListView lv)
    {
        if (lv.Columns.Count == 0) return;
        int used = 0;
        for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
        int w = lv.ClientSize.Width - used;
        if (w > 60) lv.Columns[lv.Columns.Count - 1].Width = w;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GiB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MiB" : $"{bytes} B";
}
