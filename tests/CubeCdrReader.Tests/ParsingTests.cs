using Xunit;

namespace CubeCdrReader.Tests;

public class SplitTests
{
    [Fact]
    public void SplitsPlainFields()
    {
        Assert.Equal(new[] { "a", "b", "", "d" }, Cdr.Split("a, b,,d"));
    }

    [Fact]
    public void KeepsCommasAndDoubledQuotesInsideQuotes()
    {
        Assert.Equal(new[] { "x", "say \"hi\", bye", "z" }, Cdr.Split("x,\"say \"\"hi\"\", bye\",z"));
    }

    [Fact]
    public void RejectsUnterminatedQuote()
    {
        Assert.Null(Cdr.Split("a,\"open"));
    }

    [Fact]
    public void RejectsTextAfterClosingQuote()
    {
        Assert.Null(Cdr.Split("\"a\"b,c"));
    }
}

public class TimestampTests
{
    [Fact]
    public void ReadsCiscoClockFormat()
    {
        var value = Cdr.Timestamp("*09:01:24.128 UTC Tue Nov 17 2026");
        Assert.NotNull(value);
        Assert.Equal(new DateTime(2026, 11, 17, 9, 1, 24, 128), value!.Value.Clock);
        Assert.Equal("UTC", value.Value.Zone);
    }

    [Fact]
    public void ReadsIsoFormat()
    {
        var value = Cdr.Timestamp("2026-11-17 09:01:24.5");
        Assert.NotNull(value);
        Assert.Equal(new DateTime(2026, 11, 17, 9, 1, 24, 500), value!.Value.Clock);
    }

    [Fact]
    public void RejectsNonsense()
    {
        Assert.Null(Cdr.Timestamp("yesterday"));
        Assert.Null(Cdr.Timestamp(""));
    }
}

public class HeaderTests
{
    [Fact]
    public void RecognisesNamedColumnsInAnyCaseOrPunctuation()
    {
        var schema = Cdr.Header(Cdr.Split("H323_Setup_Time,CLID,DNIS,Duration-Seconds")!);
        Assert.NotNull(schema);
        Assert.Equal(0, schema!.Columns[(int)Column.Setup]);
        Assert.Equal(1, schema.Columns[(int)Column.Calling]);
        Assert.Equal(2, schema.Columns[(int)Column.Called]);
        Assert.Equal(3, schema.Columns[(int)Column.Duration]);
    }

    [Fact]
    public void IgnoresRowsThatAreNotHeaders()
    {
        Assert.Null(Cdr.Header(new[] { "foo", "bar" }));
        // A time column alone isn't enough; it also needs a number or call ID column.
        Assert.Null(Cdr.Header(new[] { "setup-time", "codec" }));
    }
}

public class RecordTests
{
    private static Cdr.Schema NamedSchema() =>
        Cdr.Header(Cdr.Split("h323-setup-time,h323-connect-time,h323-disconnect-time,clid,dnis,release-source,forwarded")!)!;

    [Fact]
    public void WorksOutDurationFromConnectAndDisconnect()
    {
        var row = Cdr.Record(Cdr.Split(
            "*09:00:00.000 UTC Tue Nov 17 2026,*09:00:10.000 UTC Tue Nov 17 2026,*09:01:10.500 UTC Tue Nov 17 2026,+1555,+1666,1,yes")!,
            NamedSchema());
        Assert.NotNull(row);
        Assert.Equal("60.500", row![(int)Column.Duration]);
        Assert.Equal("Calling party (PSTN) [1]", row[(int)Column.Release]);
        Assert.Equal("Yes", row[(int)Column.Forwarded]);
    }

    [Fact]
    public void LeavesDurationEmptyWhenZonesDiffer()
    {
        var row = Cdr.Record(Cdr.Split(
            "*09:00:00 UTC Tue Nov 17 2026,*09:00:10 UTC Tue Nov 17 2026,*09:01:10 EST Tue Nov 17 2026,+1555,+1666,,")!,
            NamedSchema());
        Assert.NotNull(row);
        Assert.Equal("", row![(int)Column.Duration]);
        Assert.Equal("Unknown", row[(int)Column.Release]);
        Assert.Equal("Unknown", row[(int)Column.Forwarded]);
    }

    [Fact]
    public void SkipsRowsWithNoTimeOrCallId()
    {
        Assert.Null(Cdr.Record(Cdr.Split(",,,+1555,+1666,,")!, NamedSchema()));
    }

    [Fact]
    public void ReadsHeaderlessCompactRecord()
    {
        // Cisco file accounting without a header: two numeric IDs, then "1,2", then times and the cause code.
        string[] fields =
        [
            "123", "456", "1", "2", "CONF-1", "",
            "*10:00:00.000 UTC Mon Nov 16 2026",   // setup
            "*10:00:05.000 UTC Mon Nov 16 2026",   // connect
            "*10:01:05.000 UTC Mon Nov 16 2026",   // disconnect
            "10", "normal call clearing", "answer",
            "", "", "", "", "", "", "",
            "+15550001", "+15550002", ""
        ];
        var row = Cdr.Record(fields, null);
        Assert.NotNull(row);
        Assert.Equal(fields[6], row![(int)Column.Setup]);
        Assert.Equal("+15550001", row[(int)Column.Calling]);
        Assert.Equal("+15550002", row[(int)Column.Called]);
        Assert.Equal("10", row[(int)Column.Cause]);
        Assert.Equal("normal call clearing", row[(int)Column.Reason]);
        Assert.Equal("answer", row[(int)Column.Leg]);
        Assert.Equal("456", row[(int)Column.CallId]);
        Assert.Equal("CONF-1", row[(int)Column.Conference]);
        Assert.Equal("60.000", row[(int)Column.Duration]);
    }

    [Fact]
    public void RejectsHeaderlessLinesThatDontLookLikeRecords()
    {
        Assert.Null(Cdr.Record(Cdr.Split("hello,world,this,is,not,a,record")!, null));
    }
}
