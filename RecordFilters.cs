using System.Globalization;

namespace CubeCdrReader;

internal sealed record RecordFilters(
    DateTime? SetupFrom = null,
    DateTime? SetupThrough = null,
    double? MinimumDuration = null,
    double? MaximumDuration = null,
    (Column Column, string Value)[]? Fields = null)
{
    internal bool Matches(string[] row)
    {
        if (Fields is not null)
        {
            foreach (var (column, value) in Fields)
            {
                string cell = row[(int)column];
                bool matches = column == Column.Forwarded
                    ? cell.Equals(value, StringComparison.OrdinalIgnoreCase)
                    : cell.Contains(value, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                    return false;
            }
        }

        if (SetupFrom.HasValue || SetupThrough.HasValue)
        {
            if (Cdr.Timestamp(row[(int)Column.Setup]) is not { } timestamp ||
                timestamp.Clock < SetupFrom || timestamp.Clock > SetupThrough)
                return false;
        }

        if (MinimumDuration.HasValue || MaximumDuration.HasValue)
        {
            if (!double.TryParse(row[(int)Column.Duration], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double duration) || !double.IsFinite(duration) ||
                duration < MinimumDuration || duration > MaximumDuration)
                return false;
        }

        return true;
    }
}
