using System.Text;
using Xunit;

namespace CubeCdrReader.Tests;

public class LogReaderTests
{
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "samples", "sample-cdrs.csv");

    [Fact]
    public void ReadsTheSampleFile()
    {
        var result = LogReader.ReadFile(SamplePath);
        Assert.Equal(1, result.Files);
        Assert.Equal(48, result.Rows.Count);
        Assert.Equal(0, result.Errors);
        Assert.False(result.Limited);
        Assert.All(result.Rows, row => Assert.StartsWith("sample-cdrs.csv : ", row[(int)Column.Source]));
    }

    [Fact]
    public void ReadsOnlyRecordFilesInAFolder()
    {
        string folder = Directory.CreateTempSubdirectory("cdr-test-").FullName;
        try
        {
            File.Copy(SamplePath, Path.Combine(folder, "a.csv"));
            File.Copy(SamplePath, Path.Combine(folder, "cdr.20261117"));   // numeric suffix: read
            File.Copy(SamplePath, Path.Combine(folder, "notes.docx"));     // other extension: ignored
            var result = LogReader.ReadFolder(folder);
            Assert.Equal(2, result.Files);
            Assert.Equal(96, result.Rows.Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void HandlesByteOrderMarkCrLfAndBlankLines()
    {
        string path = Path.GetTempFileName();
        try
        {
            string text = "h323-setup-time,clid,dnis\r\n\r\n2026-11-17 09:00:00,+1555,+1666\r\n";
            File.WriteAllText(path, text, new UTF8Encoding(true));
            var result = LogReader.ReadFile(path);
            Assert.Single(result.Rows);
            Assert.Equal("+1666", result.Rows[0][(int)Column.Called]);
            Assert.Equal(0, result.Skipped);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CountsAMissingFileAsAnError()
    {
        var result = LogReader.ReadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv"));
        Assert.Equal(1, result.Errors);
        Assert.Empty(result.Rows);
    }
}

public class FilterTests
{
    private static readonly List<string[]> Rows = LogReader.ReadFile(
        Path.Combine(AppContext.BaseDirectory, "samples", "sample-cdrs.csv")).Rows;

    [Fact]
    public void EmptyFilterMatchesEverything()
    {
        Assert.Equal(Rows.Count, Rows.Count(new RecordFilters().Matches));
    }

    [Fact]
    public void TextFiltersMatchPartOfAValueIgnoringCase()
    {
        var filter = new RecordFilters(Fields: [(Column.Codec, "G711")]);
        var matches = Rows.Where(filter.Matches).ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, row => Assert.Contains("g711", row[(int)Column.Codec], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForwardedFilterMatchesExactly()
    {
        var filter = new RecordFilters(Fields: [(Column.Forwarded, "yes")]);
        Assert.All(Rows.Where(filter.Matches), row => Assert.Equal("Yes", row[(int)Column.Forwarded]));
    }

    [Fact]
    public void DurationRangeIsInclusive()
    {
        var filter = new RecordFilters(MinimumDuration: 459, MaximumDuration: 459);
        var matches = Rows.Where(filter.Matches).ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, row => Assert.Equal("459", row[(int)Column.Duration]));
    }

    [Fact]
    public void SetupTimeRangeUsesTheRecordedClock()
    {
        var from = new DateTime(2026, 11, 17, 9, 1, 0);
        var through = new DateTime(2026, 11, 17, 9, 2, 59);
        var filter = new RecordFilters(SetupFrom: from, SetupThrough: through);
        var matches = Rows.Where(filter.Matches).ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, row =>
        {
            var clock = Cdr.Timestamp(row[(int)Column.Setup])!.Value.Clock;
            Assert.True(clock >= from && clock <= through);
        });
    }
}

public class CsvExportTests
{
    [Fact]
    public void WritesHeaderAndQuotesWhenNeeded()
    {
        var row = new string[Cdr.ColumnCount];
        Array.Fill(row, "");
        row[(int)Column.Calling] = "+15551234";
        row[(int)Column.Reason] = "busy, try later";
        row[(int)Column.CallId] = "say \"hi\"";
        using var writer = new StringWriter();
        CsvExport.Write(writer, [row]);
        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Setup time,Calling number,", lines[0]);
        string[] cells = Cdr.Split(lines[1])!;
        Assert.Equal("+15551234", cells[(int)Column.Calling]);
        Assert.Equal("busy, try later", cells[(int)Column.Reason]);
        Assert.Equal("say \"hi\"", cells[(int)Column.CallId]);
    }

    [Fact]
    public void HasATitleForEveryColumn()
    {
        Assert.Equal(Cdr.ColumnCount, CsvExport.ColumnTitles.Count);
    }

    [Fact]
    public void NeutralisesSpreadsheetFormulasButKeepsPhoneNumbers()
    {
        Assert.Equal("'=SUM(A1)", CsvExport.Escape("=SUM(A1)"));
        Assert.Equal("'@cmd", CsvExport.Escape("@cmd"));
        Assert.Equal("+15551234", CsvExport.Escape("+15551234"));
        Assert.Equal("-5", CsvExport.Escape("-5"));
        Assert.Equal("'+cmd", CsvExport.Escape("+cmd"));
    }

    [Fact]
    public void ClipboardTextIsTabSeparated()
    {
        var row = new string[Cdr.ColumnCount];
        Array.Fill(row, "x");
        string text = CsvExport.ToClipboardText([row]);
        Assert.Contains("Setup time\tCalling number", text);
        Assert.Contains("x\tx\tx", text);
    }
}
