using System.Globalization;

namespace CubeCdrReader;

internal sealed class FilterPanel : UserControl
{
    private static readonly (Column Column, string Label)[] TextFields =
    [
        (Column.Calling, "Calling number"), (Column.Called, "Called number"),
        (Column.Codec, "Codec"), (Column.Release, "Disconnected by"),
        (Column.Cause, "Cause code"), (Column.Reason, "Disconnect reason"),
        (Column.Forwarded, "Forwarded"), (Column.ForwardedTo, "Forwarded to"),
        (Column.RemoteIp, "Remote signaling IP"), (Column.RemoteSipPort, "Remote SIP port"),
        (Column.RemoteMediaIp, "Remote media IP"), (Column.LocalIp, "Local signaling IP"),
        (Column.Leg, "Call origin"), (Column.CallId, "Call ID"),
        (Column.Conference, "Conference ID"), (Column.Source, "Source file / line")
    ];

    private readonly DateTimePicker from = SetupPicker();
    private readonly DateTimePicker through = SetupPicker();
    private readonly TextBox minimum = new() { MaxLength = 32 };
    private readonly TextBox maximum = new() { MaxLength = 32 };
    private readonly List<(Column Column, TextBox Input)> inputs = [];
    private readonly ComboBox forwarded = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label message = new() { AutoSize = true, Anchor = AnchorStyles.Left };

    internal RecordFilters Applied { get; private set; } = new();
    internal bool HasFilters => Applied.SetupFrom.HasValue || Applied.SetupThrough.HasValue ||
        Applied.MinimumDuration.HasValue || Applied.MaximumDuration.HasValue || Applied.Fields is { Length: > 0 };
    internal event EventHandler? FiltersChanged;

    internal FilterPanel()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 4, RowCount = 11, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top
        };
        for (int column = 0; column < 4; column++)
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        int fieldIndex = 0;
        void AddField(string label, Control control)
        {
            int column = fieldIndex % 4;
            int row = fieldIndex / 4 * 2;
            layout.Controls.Add(new Label { Text = label, AutoSize = true }, column, row);
            control.AccessibleName = label;
            control.Dock = DockStyle.Top;
            layout.Controls.Add(control, column, row + 1);
            fieldIndex++;
        }

        AddField("Setup from (date and time)", from);
        AddField("Setup through (date and time)", through);
        AddField("Minimum duration (s)", minimum);
        AddField("Maximum duration (s)", maximum);

        forwarded.Items.AddRange(["Any", "Yes", "No", "Forwarding event", "Unknown"]);
        forwarded.SelectedIndex = 0;
        foreach (var (column, label) in TextFields)
        {
            if (column == Column.Forwarded)
            {
                AddField(label, forwarded);
                continue;
            }

            var input = new TextBox { MaxLength = 255 };
            inputs.Add((column, input));
            AddField(label, input);
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var apply = new Button { Text = "Apply filters", AutoSize = true };
        var clear = new Button { Text = "Clear filters", AutoSize = true };
        apply.Click += (_, _) => Apply();
        clear.Click += (_, _) => Clear();
        buttons.Controls.AddRange([apply, clear, message]);
        layout.Controls.Add(buttons, 0, 10);
        layout.SetColumnSpan(buttons, 4);
        Controls.Add(layout);
    }

    private static DateTimePicker SetupPicker() => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm:ss",
        ShowCheckBox = true,
        Checked = false
    };

    private static DateTime ToSecond(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    private static bool TryDuration(string text, out double? duration)
    {
        duration = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
            !double.IsFinite(value) || value < 0)
            return false;
        duration = value;
        return true;
    }

    private void Apply()
    {
        DateTime? start = from.Checked ? ToSecond(from.Value) : null;
        DateTime? lastSecond = through.Checked ? ToSecond(through.Value) : null;
        if (start > lastSecond)
        {
            message.Text = "Setup from must be at or before setup through.";
            from.Focus();
            return;
        }

        if (!TryDuration(minimum.Text, out double? min) ||
            !TryDuration(maximum.Text, out double? max) || min > max)
        {
            message.Text = "Enter nonnegative durations with minimum at or below maximum.";
            minimum.Focus();
            return;
        }

        var fields = new List<(Column Column, string Value)>();
        foreach (var (column, input) in inputs)
        {
            string value = input.Text.Trim();
            if (value.Length > 0)
                fields.Add((column, value));
        }
        if (forwarded.SelectedIndex > 0)
            fields.Add((Column.Forwarded, forwarded.Text));

        DateTime? end = lastSecond?.AddMilliseconds(999);
        Applied = new RecordFilters(start, end, min, max, fields.ToArray());
        message.Text = "";
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Clear()
    {
        from.Checked = through.Checked = false;
        minimum.Clear();
        maximum.Clear();
        foreach (var (_, input) in inputs)
            input.Clear();
        forwarded.SelectedIndex = 0;
        Apply();
    }
}
