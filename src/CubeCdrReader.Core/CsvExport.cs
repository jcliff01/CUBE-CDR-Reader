using System.Text;

namespace CubeCdrReader;

/// <summary>Writes records as CSV with a header row, quoting values when needed.</summary>
public static class CsvExport
{
    /// <summary>Column headings, in <see cref="Column"/> order.</summary>
    public static readonly IReadOnlyList<string> ColumnTitles =
    [
        "Setup time", "Calling number", "Called number", "Duration (s)", "Codec",
        "Disconnected by", "Cause code", "Disconnect reason", "Forwarded", "Forwarded to",
        "Remote signaling IP", "Remote SIP port", "Remote UDP port", "Remote media IP",
        "Remote media port", "Local signaling IP", "Local SIP port", "Local media IP",
        "Local media port", "Connect time", "Disconnect time", "Call origin", "Call ID",
        "Conference ID", "Source file / line"
    ];

    public static void Write(TextWriter writer, IEnumerable<string[]> rows, char separator = ',')
    {
        writer.WriteLine(Line(ColumnTitles, separator));
        foreach (string[] row in rows)
            writer.WriteLine(Line(row, separator));
    }

    public static void WriteFile(string path, IEnumerable<string[]> rows)
    {
        // UTF-8 with a byte-order mark so Excel shows non-ASCII characters correctly.
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        Write(writer, rows);
    }

    /// <summary>Tab-separated text for pasting into a spreadsheet.</summary>
    public static string ToClipboardText(IEnumerable<string[]> rows)
    {
        using var writer = new StringWriter();
        Write(writer, rows, '\t');
        return writer.ToString();
    }

    public static string Line(IEnumerable<string> values, char separator = ',') =>
        string.Join(separator, values.Select(value => Escape(value, separator)));

    public static string Escape(string value, char separator = ',')
    {
        value ??= "";
        // A leading =, +, - or @ makes spreadsheets treat the cell as a formula. Phone numbers often
        // start with +, so those are kept as text by prefixing an apostrophe only for = and @,
        // and for + or - followed by something other than a digit.
        if (value.Length > 0 && (value[0] is '=' or '@' ||
            (value[0] is '+' or '-' && (value.Length == 1 || !char.IsAsciiDigit(value[1])))))
            value = "'" + value;
        bool quote = value.Contains(separator) || value.Contains('"') ||
            value.Contains('\n') || value.Contains('\r');
        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
