using System.Globalization;

namespace CubeCdrReader;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ReaderWindow());
    }
}

internal sealed class ReaderWindow : Form
{
    private static readonly (string Title, int Width)[] Columns =
    [
        ("Date / setup time", 245), ("Calling", 120), ("Called", 120), ("Duration (s)", 99),
        ("Codec", 115), ("Disconnected by", 174), ("Cause code (raw)", 122), ("Reason", 225),
        ("Forwarded", 122), ("Forwarded to", 125), ("Remote signaling IP", 170), ("Remote SIP port", 125),
        ("Remote UDP port", 125), ("Remote media IP", 170), ("Remote media port", 125),
        ("Local signaling IP", 170), ("Local SIP port", 125), ("Local media IP", 170),
        ("Local media port", 125), ("Connect time", 245), ("Disconnect time", 245),
        ("Call origin", 100), ("Call ID", 115), ("Conference ID", 235), ("Source file : line", 235)
    ];

    private readonly Button open = new() { Text = "Open Folder...", AutoSize = true };
    private readonly TextBox search = new() { MaxLength = 255, Dock = DockStyle.Fill };
    private readonly Button toggleFilters = new() { Text = "Show filters", AutoSize = true };
    private readonly FilterPanel filters = new() { Visible = false };
    private bool filtersExpanded;
    private readonly ListView grid = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        VirtualMode = true,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
    };
    private readonly Label status = new() { AutoSize = true, Text = "Open a folder to read CUBE call legs." };
    private ImportResult? result;
    private int[]? filteredRows;
    private List<int> visible = [];
    private int sortColumn = -1;
    private bool descending;
    private bool loading;

    internal ReaderWindow()
    {
        Text = "CUBE CDR Reader";
        ClientSize = new Size(1120, 660);
        MinimumSize = new Size(670, 240);
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 4, RowCount = 4, Dock = DockStyle.Fill, Padding = new Padding(9)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(open, 0, 0);
        layout.Controls.Add(new Label { Text = "Search all", Anchor = AnchorStyles.Left, AutoSize = true }, 1, 0);
        search.AccessibleName = "Search all columns";
        layout.Controls.Add(search, 2, 0);
        layout.Controls.Add(toggleFilters, 3, 0);
        layout.Controls.Add(filters, 0, 1);
        layout.SetColumnSpan(filters, 4);
        layout.Controls.Add(grid, 0, 2);
        layout.SetColumnSpan(grid, 4);
        layout.Controls.Add(status, 0, 3);
        layout.SetColumnSpan(status, 4);
        Controls.Add(layout);

        foreach (var (title, width) in Columns)
            grid.Columns.Add(title, width);

        open.Click += OpenFolder;
        toggleFilters.Click += (_, _) =>
        {
            filtersExpanded = !filtersExpanded;
            MinimumSize = filtersExpanded ? new Size(850, 520) : new Size(670, 240);
            filters.Visible = filtersExpanded;
            UpdateFilterButton();
        };
        filters.FiltersChanged += (_, _) =>
        {
            UpdateFilterButton();
            ApplyFilters();
        };
        search.TextChanged += (_, _) => RefreshRows();
        grid.RetrieveVirtualItem += (_, args) =>
        {
            args.Item = result is not null && (uint)args.ItemIndex < visible.Count
                ? new ListViewItem(result.Rows[visible[args.ItemIndex]])
                : new ListViewItem();
        };
        grid.ColumnClick += (_, args) =>
        {
            descending = sortColumn == args.Column && !descending;
            sortColumn = args.Column;
            RefreshRows();
        };
    }

    private void UpdateFilterButton()
    {
        string action = filtersExpanded ? "Hide filters" : "Show filters";
        toggleFilters.Text = filters.HasFilters ? $"{action} (active)" : action;
    }

    private async void OpenFolder(object? sender, EventArgs args)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder containing Cisco CUBE file accounting records",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string folder = dialog.SelectedPath;
        loading = true;
        open.Enabled = false;
        status.Text = "Reading accounting records...";
        ImportResult imported = await Task.Run(() => FolderReader.Read(folder));
        if (IsDisposed || Disposing)
            return;
        grid.VirtualListSize = 0;
        result = imported;
        loading = false;
        open.Enabled = true;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        filteredRows = null;
        if (result is not null && filters.HasFilters)
        {
            List<string[]> rows = result.Rows;
            RecordFilters applied = filters.Applied;
            var matching = new List<int>();
            for (int index = 0; index < rows.Count; index++)
            {
                if (applied.Matches(rows[index]))
                    matching.Add(index);
            }
            filteredRows = matching.ToArray();
        }
        RefreshRows();
    }

    private void RefreshRows()
    {
        if (result is null || loading)
            return;
        string query = search.Text;
        List<string[]> rows = result.Rows;
        int candidateCount = filteredRows?.Length ?? rows.Count;
        var matches = new List<int>(candidateCount);
        for (int offset = 0; offset < candidateCount; offset++)
        {
            int index = filteredRows?[offset] ?? offset;
            if (query.Length == 0 || rows[index].Any(cell => cell.Contains(query, StringComparison.OrdinalIgnoreCase)))
                matches.Add(index);
        }

        if (sortColumn >= 0 && matches.Count > 1)
            Sort(matches, rows);

        visible = matches;
        grid.VirtualListSize = visible.Count;
        grid.Invalidate();
        status.Text = $"{visible.Count} of {rows.Count} records   |   {result.Files} files read   |   " +
            $"{result.Skipped} rows skipped   |   {result.Errors} file errors" +
            (result.Limited ? "   |   Import limit reached" : "");
    }

    private void Sort(List<int> matches, List<string[]> rows)
    {
        int column = sortColumn;
        double?[]? durations = null;
        DateTime?[]? times = null;
        if (column == (int)Column.Duration)
        {
            durations = new double?[rows.Count];
            foreach (int index in matches)
            {
                if (double.TryParse(rows[index][column], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double duration))
                    durations[index] = duration;
            }
        }
        if ((Column)column is Column.Setup or Column.Connect or Column.End)
        {
            times = new DateTime?[rows.Count];
            foreach (int index in matches)
                times[index] = Cdr.Timestamp(rows[index][column])?.Clock;
        }

        matches.Sort((left, right) =>
        {
            int order;
            if (times is not null && (times[left].HasValue || times[right].HasValue))
                order = Nullable.Compare(times[left], times[right]);
            else if (durations is not null && (durations[left].HasValue || durations[right].HasValue))
                order = Nullable.Compare(durations[left], durations[right]);
            else
                order = StringComparer.OrdinalIgnoreCase.Compare(rows[left][column], rows[right][column]);

            if (times is not null && times[left].HasValue != times[right].HasValue)
                order = times[left].HasValue ? -1 : 1;
            if (durations is not null && durations[left].HasValue != durations[right].HasValue)
                order = durations[left].HasValue ? -1 : 1;
            return order == 0 ? left.CompareTo(right) : descending ? -Math.Sign(order) : order;
        });
    }
}
