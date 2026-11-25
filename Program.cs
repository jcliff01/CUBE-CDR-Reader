using System.Globalization;

namespace CubeCdrReader;

internal static class Program
{
    [STAThread]
    // A file or folder given on the command line (or dropped onto the .exe, or chosen with
    // "Open with") is opened as soon as the window appears.
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ReaderWindow(args.Length > 0 ? args[0] : null));
    }
}

internal sealed class ReaderWindow : Form
{
    private static readonly string[] ColumnTitles =
    [
        "Setup time", "Calling number", "Called number", "Duration (s)", "Codec",
        "Disconnected by", "Cause code", "Disconnect reason", "Forwarded", "Forwarded to",
        "Remote signaling IP", "Remote SIP port", "Remote UDP port", "Remote media IP",
        "Remote media port", "Local signaling IP", "Local SIP port", "Local media IP",
        "Local media port", "Connect time", "Disconnect time", "Call origin", "Call ID",
        "Conference ID", "Source file / line"
    ];

    private readonly DateTimePicker start = SetupPicker();
    private readonly DateTimePicker end = SetupPicker();
    private readonly TextBox minimum = new();
    private readonly TextBox maximum = new();
    private readonly Dictionary<Column, TextBox> fields = new();
    private readonly ComboBox forwarded = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button openFile = new() { Text = "Open log file…", AutoSize = true };
    private readonly Button openFolder = new() { Text = "Open folder…", AutoSize = true };
    private readonly Label status = new() { Text = "Open a Cisco CUBE accounting log or folder.", AutoSize = true };
    private readonly ListView grid = new()
    {
        Dock = DockStyle.Fill, View = View.Details, VirtualMode = true,
        FullRowSelect = true, GridLines = true
    };

    private ImportResult? imported;
    private List<string[]> visible = [];
    private RecordFilters applied = new();
    private int sortColumn = -1;
    private bool descending;
    private bool loading;

    internal ReaderWindow(string? startPath = null)
    {
        Text = "CUBE CDR Reader";
        if (startPath is not null)
        {
            Shown += async (_, _) =>
            {
                if (Directory.Exists(startPath))
                    await LoadLogs(() => LogReader.ReadFolder(startPath));
                else if (File.Exists(startPath))
                    await LoadLogs(() => LogReader.ReadFile(startPath));
                else
                    status.Text = "Not found: " + startPath;
            };
        }
        ClientSize = new Size(1150, 660);
        MinimumSize = new Size(850, 440);
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        toolbar.Controls.AddRange([openFile, openFolder]);
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(BuildFilters(), 0, 1);
        layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(status, 0, 3);
        Controls.Add(layout);

        foreach (string title in ColumnTitles)
            grid.Columns.Add(title, title.EndsWith("time", StringComparison.Ordinal) ? 245 : 142);
        grid.RetrieveVirtualItem += (_, args) =>
        {
            args.Item = (uint)args.ItemIndex < visible.Count
                ? new ListViewItem(visible[args.ItemIndex]) : new ListViewItem();
        };
        grid.ColumnClick += (_, args) =>
        {
            descending = args.Column == sortColumn && !descending;
            sortColumn = args.Column;
            RefreshRows();
        };
        openFile.Click += OpenFile;
        openFolder.Click += OpenFolder;
    }

    private Control BuildFilters()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Top, ColumnCount = 4, RowCount = 7
        };
        for (int column = 0; column < 4; column++)
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        void Add(int index, string name, Control input)
        {
            int column = index % 4;
            int row = index / 4 * 2;
            input.AccessibleName = name;
            input.Dock = DockStyle.Top;
            panel.Controls.Add(new Label { Text = name, AutoSize = true }, column, row);
            panel.Controls.Add(input, column, row + 1);
        }

        Add(0, "Setup from (date and time)", start);
        Add(1, "Setup through (date and time)", end);
        Add(2, "Minimum duration (s)", minimum);
        Add(3, "Maximum duration (s)", maximum);

        Column[] textColumns = [Column.Calling, Column.Called, Column.Codec, Column.Cause,
            Column.Release, Column.RemoteIp, Column.CallId];
        for (int index = 0; index < textColumns.Length; index++)
        {
            Column column = textColumns[index];
            var input = new TextBox();
            fields.Add(column, input);
            Add(index + 4, ColumnTitles[(int)column], input);
        }

        forwarded.Items.AddRange(["Any", "Yes", "No", "Forwarding event", "Unknown"]);
        forwarded.SelectedIndex = 0;
        Add(11, "Forwarded", forwarded);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var apply = new Button { Text = "Apply filters", AutoSize = true };
        var clear = new Button { Text = "Clear filters", AutoSize = true };
        apply.Click += (_, _) => ApplyFilters();
        clear.Click += (_, _) =>
        {
            start.Checked = end.Checked = false;
            minimum.Clear();
            maximum.Clear();
            foreach (TextBox input in fields.Values)
                input.Clear();
            forwarded.SelectedIndex = 0;
            ApplyFilters();
        };
        buttons.Controls.AddRange([apply, clear]);
        panel.Controls.Add(buttons, 0, 6);
        panel.SetColumnSpan(buttons, 4);
        AcceptButton = apply;
        return panel;
    }

    private static DateTimePicker SetupPicker() => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss",
        ShowCheckBox = true, Checked = false
    };

    private static DateTime? CheckedSecond(DateTimePicker picker) => picker.Checked
        ? picker.Value.AddTicks(-(picker.Value.Ticks % TimeSpan.TicksPerSecond))
        : null;

    private static bool ReadDuration(TextBox input, out double? duration)
    {
        duration = null;
        if (string.IsNullOrWhiteSpace(input.Text))
            return true;
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds < 0)
            return false;
        duration = seconds;
        return true;
    }

    private void ApplyFilters()
    {
        DateTime? from = CheckedSecond(start);
        DateTime? through = CheckedSecond(end);
        if (from > through)
        {
            MessageBox.Show(this, "Setup from must be at or before setup through.", Text);
            return;
        }

        if (!ReadDuration(minimum, out double? min) || !ReadDuration(maximum, out double? max) || min > max)
        {
            MessageBox.Show(this, "Enter nonnegative durations with minimum at or below maximum.", Text);
            return;
        }

        var selected = fields.Select(field => (Column: field.Key, Value: field.Value.Text.Trim()))
            .Where(field => field.Value.Length > 0).ToList();
        if (forwarded.SelectedIndex > 0)
            selected.Add((Column.Forwarded, forwarded.Text));
        applied = new RecordFilters(from, through?.AddTicks(TimeSpan.TicksPerSecond - 1), min, max, selected.ToArray());
        RefreshRows();
    }

    private async void OpenFile(object? sender, EventArgs args)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a Cisco CUBE accounting log",
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await LoadLogs(() => LogReader.ReadFile(dialog.FileName));
    }

    private async void OpenFolder(object? sender, EventArgs args)
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = false };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await LoadLogs(() => LogReader.ReadFolder(dialog.SelectedPath));
    }

    private async Task LoadLogs(Func<ImportResult> read)
    {
        loading = true;
        openFile.Enabled = openFolder.Enabled = false;
        status.Text = "Reading CUBE accounting records…";
        try
        {
            imported = await Task.Run(read);
        }
        finally
        {
            loading = false;
            if (!IsDisposed && !Disposing)
                openFile.Enabled = openFolder.Enabled = true;
        }

        if (!IsDisposed && !Disposing)
            RefreshRows();
    }

    private void RefreshRows()
    {
        if (loading || imported is null)
            return;

        var matches = imported.Rows.Where(applied.Matches).ToList();
        if (sortColumn >= 0)
            matches = SortRows(matches, (Column)sortColumn);

        grid.SelectedIndices.Clear();
        visible = matches;
        grid.VirtualListSize = visible.Count;
        grid.Invalidate();
        status.Text = $"{visible.Count:N0} of {imported.Rows.Count:N0} records  |  " +
            $"{imported.Files:N0} files read  |  {imported.Skipped:N0} skipped rows  |  {imported.Errors:N0} file errors" +
            (imported.Limited ? "  |  250,000-record import limit reached" : "");
    }

    private List<string[]> SortRows(List<string[]> rows, Column column)
    {
        string Cell(string[] row) => row[(int)column];
        IEnumerable<string[]> Sort<TKey>(Func<string[], TKey> key, IComparer<TKey>? comparer = null) =>
            descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

        return (column switch
        {
            Column.Setup or Column.Connect or Column.End => Sort(row => Cdr.Timestamp(Cell(row))?.Clock),
            Column.Duration => Sort<double?>(row => double.TryParse(Cell(row), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double seconds) ? seconds : null),
            _ => Sort(Cell, StringComparer.OrdinalIgnoreCase)
        }).ToList();
    }
}
