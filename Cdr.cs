using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CubeCdrReader;

internal enum Column
{
    Setup, Calling, Called, Duration, Codec, Release, Cause, Reason,
    Forwarded, ForwardedTo, RemoteIp, RemoteSipPort, RemoteUdpPort,
    RemoteMediaIp, RemoteMediaPort, LocalIp, LocalSipPort, LocalMediaIp, LocalMediaPort,
    Connect, End, Leg, CallId, Conference, Source
}

internal static class Cdr
{
    internal const int ColumnCount = (int)Column.Source + 1;

    private static readonly string[][] Keys =
    [
        ["h323-setup-time", "setup-time", "start-time"],
        ["clid", "calling-station-id", "calling-number"],
        ["dnis", "called-station-id", "called-number"],
        ["duration-seconds", "acct-session-time", "duration"],
        ["codec-type-rate", "codec", "negotiated-codec"],
        ["release-source", "disconnect-party", "disconnect-initiator"],
        ["h323-disconnect-cause", "disconnect-cause", "reason-code"],
        ["disconnect-text", "disconnect-reason", "reason-text"],
        ["forwarded", "call-forwarded", "is-forwarded"],
        ["forwarded-to", "forward-to", "fwdto"],
        ["remote-signaling-ip", "h323-remote-address", "remote-ip"],
        ["remote-signaling-port", "remote-sip-port"],
        ["remote-udp-port"],
        ["remote-media-address", "remote-media-ip", "remote-rtp-ip"],
        ["remote-media-udp-port", "remote-media-port", "remote-rtp-port"],
        ["local-signaling-ip", "local-ip", "local-address"],
        ["local-signaling-port", "local-sip-port"],
        ["local-media-address", "local-media-ip", "local-rtp-ip"],
        ["local-media-udp-port", "local-media-port", "local-rtp-port"],
        ["h323-connect-time", "connect-time"],
        ["h323-disconnect-time", "disconnect-time", "end-time"],
        ["h323-call-origin", "call-origin", "leg"],
        ["call-id", "sip-call-id"],
        ["h323-conf-id", "conference-id"]
    ];
    private static readonly string[][] NormalizedKeys = Keys
        .Select(aliases => aliases.Select(Key).ToArray()).ToArray();

    private static readonly string[] ReleaseSources =
    [
        "Unknown", "Calling party (PSTN)", "Calling party (VoIP)",
        "Called party (PSTN)", "Called party (VoIP)", "Internal POTS leg",
        "Internal VoIP leg", "Internal call-control application", "Internal VoIP AAA",
        "Console command", "External RADIUS server", "External management application",
        "External call-control agent"
    ];

    internal sealed record Schema(int[] Columns, int Feature, int FeatureStatus, int FeatureTarget);

    internal static string[]? Split(string line)
    {
        var fields = new List<string>();
        int position = 0;
        while (fields.Count < 512)
        {
            while (position < line.Length && line[position] is ' ' or '\t')
                position++;

            if (position < line.Length && line[position] == '"')
            {
                var value = new StringBuilder();
                position++;
                while (true)
                {
                    if (position == line.Length)
                        return null;
                    char current = line[position++];
                    if (current == '"')
                    {
                        if (position == line.Length || line[position] != '"')
                            break;
                        position++;
                    }
                    value.Append(current);
                }
                while (position < line.Length && line[position] is ' ' or '\t')
                    position++;
                if (position < line.Length && line[position] != ',')
                    return null;
                fields.Add(value.ToString());
            }
            else
            {
                int comma = line.IndexOf(',', position);
                int end = comma < 0 ? line.Length : comma;
                fields.Add(line[position..end].TrimEnd());
                position = end;
            }

            if (position == line.Length)
                return fields.ToArray();
            position++;
        }
        return null;
    }

    private static string Key(string value) =>
        string.Concat(value.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();

    private static string Field(string[] fields, int index) =>
        (uint)index < fields.Length ? fields[index] : "";

    internal static Schema? Header(string[] fields)
    {
        string[] names = fields.Select(Key).ToArray();
        int Find(string[] aliases)
        {
            foreach (string alias in aliases)
            {
                int index = Array.IndexOf(names, alias);
                if (index >= 0)
                    return index;
            }
            return -1;
        }

        int[] columns = NormalizedKeys.Select(Find).ToArray();
        if (columns[(int)Column.Setup] < 0 && columns[(int)Column.End] < 0)
            return null;
        if (columns[(int)Column.Calling] < 0 && columns[(int)Column.Called] < 0 &&
            columns[(int)Column.CallId] < 0)
            return null;
        return new Schema(columns, Find(["featureidfield1", "featurename", "feature"]),
            Find(["featureidfield3", "featurestatus", "frs"]), Find(["featureidfield11"]));
    }

    private static bool Digits(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    private static bool ClockLike(string value)
    {
        var clock = value.AsSpan();
        if (clock.StartsWith("*"))
            clock = clock[1..];
        return !clock.IsEmpty && char.IsAsciiDigit(clock[0]) && clock.Contains(':');
    }

    private static bool CauseLike(string value)
    {
        if (value.Length == 0)
            return true;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return value.Length > 0 && value.All(char.IsAsciiHexDigit);
    }

    private static bool ForwardFeature(string value) => Key(value) is "cfa" or "cfna" or "cfby";

    private static Schema? Positional(string[] fields)
    {
        if (fields.Length < 21 || !Digits(fields[0]) || !Digits(fields[1]) ||
            fields[2] != "1" || fields[3] != "2")
            return null;

        // The native accounting examples use several layouts before the disconnect cause.
        int cause = 0;
        for (int index = 9; index <= 12; index++)
        {
            if (ClockLike(fields[index - 1]) && CauseLike(fields[index]) &&
                (fields[index + 1].Length == 0 || !CauseLike(fields[index + 1])) &&
                (fields[index - 2].Length == 0 || ClockLike(fields[index - 2])))
            {
                cause = index;
                break;
            }
        }
        if (cause == 0 || fields.Length <= cause + 11)
            return null;

        int setup = cause == 9 || ClockLike(fields[6]) ? 6 : 7;
        if (fields[setup].Length > 0 && !ClockLike(fields[setup]))
            return null;
        int codec = cause + 23;
        if (fields.Length > cause + 24 && (fields[codec].Length == 0 || Digits(fields[codec])) &&
            fields[cause + 24].Length > 0 && char.IsAsciiLetter(fields[cause + 24][0]))
            codec++;
        bool compactFeature = fields.Length > cause + 13 && fields[cause + 13].Contains(':') &&
            fields[cause + 12].Length > 0 && !Digits(fields[cause + 12]);
        bool detailed = !compactFeature && fields.Length > cause + 23;
        int ports = cause + 25 + (cause != 9 && codec == cause + 24 ? 1 : 0);
        if (!detailed || Digits(Field(fields, codec)))
            codec = -1;

        int feature = -1;
        for (int index = cause + 12; index + 1 < fields.Length; index = Math.Max(index + 1, 100))
        {
            if (ForwardFeature(fields[index]) && fields[index + 1].Contains(':'))
            {
                feature = index;
                break;
            }
        }

        return new Schema(
            [setup, cause + 10, cause + 11, -1, codec, -1, cause, cause + 1, -1, -1,
             -1, -1, detailed ? ports : -1, detailed ? cause + 60 : -1, detailed ? ports + 1 : -1,
             -1, -1, -1, -1, cause - 2, cause - 1, cause + 2, 1, 4],
            feature, feature < 0 ? -1 : feature + 2, feature < 0 ? -1 : feature + 10);
    }

    private static readonly Regex IsoTime = new(
        @"^\*?(?<date>[0-9]{1,4}-[0-9]{1,2}-[0-9]{1,2})[T ](?<clock>[0-9]{1,2}:[0-9]{1,2}:[0-9]{1,2})(?:\.(?<fraction>[0-9]*))?(?<zone>.{0,15})\z");
    private static readonly Regex CiscoTime = new(
        @"^\*?(?<clock>[0-9]{1,2}:[0-9]{1,2}:[0-9]{1,2})(?:\.(?<fraction>[0-9]*))?\s*(?<zone>\S{1,15})\s+\S{1,15}\s+(?<month>[A-Za-z]{3})\s+(?<day>[0-9]{1,2})\s+(?<year>[0-9]{4})\z");

    internal static (DateTime Clock, string Zone)? Timestamp(string value)
    {
        Match match = IsoTime.Match(value);
        string text;
        string format;
        if (match.Success)
        {
            text = match.Groups["date"].Value + " " + match.Groups["clock"].Value;
            format = "yyyy-M-d H:m:s";
        }
        else
        {
            match = CiscoTime.Match(value);
            if (!match.Success)
                return null;
            text = $"{match.Groups["year"]} {match.Groups["month"]} {match.Groups["day"]} {match.Groups["clock"]}";
            format = "yyyy MMM d H:m:s";
        }

        if (!DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out DateTime clock) || clock.Year < 1970)
            return null;
        string fraction = match.Groups["fraction"].Value.PadRight(3, '0');
        return (clock.AddMilliseconds(int.Parse(fraction.AsSpan(0, 3), CultureInfo.InvariantCulture)),
            match.Groups["zone"].Value);
    }

    private static string ReleaseParty(string value)
    {
        if (value is "" or "0")
            return "Unknown";
        if (!Digits(value))
            return value;
        string name = int.TryParse(value, out int code) && code < ReleaseSources.Length
            ? ReleaseSources[code] : "Unknown";
        return $"{name} [{value}]";
    }

    private static bool PortLike(string value) =>
        Digits(value) && ushort.TryParse(value, out ushort port) && port > 0;

    private static bool IpLike(string value) => value.Contains(':')
        ? value.All(character => char.IsAsciiHexDigit(character) || character is ':' or '.')
        : value.Split('.') is { Length: 4 } octets &&
          octets.All(octet => octet.Length <= 3 && Digits(octet) && byte.TryParse(octet, out _));

    internal static string[]? Record(string[] fields, Schema? header)
    {
        Schema? schema = header ?? Positional(fields);
        if (schema is null)
            return null;
        var cells = new string[ColumnCount];
        for (int index = 0; index < (int)Column.Source; index++)
            cells[index] = Field(fields, schema.Columns[index]);
        cells[(int)Column.Source] = "";

        if (cells[(int)Column.Setup].Length == 0 && cells[(int)Column.CallId].Length == 0 &&
            cells[(int)Column.End].Length == 0)
            return null;

        if (header is null)
        {
            if (!IpLike(cells[(int)Column.RemoteMediaIp]))
                cells[(int)Column.RemoteMediaIp] = "";
            if (!PortLike(cells[(int)Column.RemoteUdpPort]))
                cells[(int)Column.RemoteUdpPort] = "";
            if (!PortLike(cells[(int)Column.RemoteMediaPort]))
                cells[(int)Column.RemoteMediaPort] = "";
        }

        foreach (string field in fields)
        {
            int equals = field.IndexOf('=');
            if (equals < 0 || equals >= 64 || equals == field.Length - 1)
                continue;
            string attribute = Key(field[..equals]);
            if (cells[(int)Column.Release].Length == 0 && field.StartsWith("release-source=", StringComparison.Ordinal))
                cells[(int)Column.Release] = field[(equals + 1)..];
            for (int column = (int)Column.RemoteIp; column <= (int)Column.LocalMediaPort; column++)
            {
                if (cells[column].Length == 0 && NormalizedKeys[column].Contains(attribute))
                    cells[column] = field[(equals + 1)..];
            }
        }

        bool forwardedFeature = ForwardFeature(Field(fields, schema.Feature));
        cells[(int)Column.Forwarded] = Key(cells[(int)Column.Forwarded]) switch
        {
            "yes" or "true" or "1" => "Yes",
            "no" or "false" or "0" => "No",
            _ when forwardedFeature => Field(fields, schema.FeatureStatus) == "0" ? "Yes" : "Forwarding event",
            _ => "Unknown"
        };
        if (cells[(int)Column.ForwardedTo].Length == 0 && forwardedFeature)
            cells[(int)Column.ForwardedTo] = Field(fields, schema.FeatureTarget);
        cells[(int)Column.Release] = ReleaseParty(cells[(int)Column.Release]);

        if (cells[(int)Column.Duration].Length == 0 &&
            Timestamp(cells[(int)Column.Connect]) is { } start &&
            Timestamp(cells[(int)Column.End]) is { } end && start.Zone == end.Zone && end.Clock >= start.Clock)
            cells[(int)Column.Duration] = (end.Clock - start.Clock).TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

        return cells;
    }
}
