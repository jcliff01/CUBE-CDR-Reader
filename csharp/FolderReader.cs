using System.Runtime.InteropServices;
using System.Text;

namespace CubeCdrReader;

internal sealed class ImportResult
{
    internal List<string[]> Rows { get; } = [];
    internal int Files { get; set; }
    internal int Skipped { get; set; }
    internal int Errors { get; set; }
    internal bool Limited { get; set; }
}

internal static class FolderReader
{
    private const int MaxRows = 250_000;
    private const int MaxLine = 262_143;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Fallback = LegacyEncoding();

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private static Encoding LegacyEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(OperatingSystem.IsWindows() ? (int)GetACP() : 1252);
    }

    internal static ImportResult Read(string folder)
    {
        var result = new ImportResult();
        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            foreach (string path in Directory.EnumerateFiles(folder, "*", options))
            {
                if (!Candidate(Path.GetFileName(path)))
                    continue;
                try
                {
                    ReadFile(path, result);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    result.Errors++;
                }
                if (result.Limited)
                    break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.Errors++;
        }
        return result;
    }

    private static bool Candidate(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0)
            return true;
        string suffix = name[(dot + 1)..];
        return suffix.ToLowerInvariant() is "csv" or "cdr" or "log" or "txt" ||
            (suffix.Length > 0 && suffix.All(char.IsAsciiDigit));
    }

    private static void ReadFile(string path, ImportResult result)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BufferedStream(file, 64 * 1024);
        result.Files++;
        var buffer = new byte[MaxLine];
        int lineNumber = 0;
        string name = Path.GetFileName(path);
        Cdr.Schema? schema = null;

        while (true)
        {
            int length = 0;
            bool oversized = false;
            int next;
            while ((next = reader.ReadByte()) >= 0 && next != '\n')
            {
                if (length < buffer.Length)
                    buffer[length++] = (byte)next;
                else
                    oversized = true;
            }
            if (next < 0 && length == 0)
                break;

            lineNumber++;
            if (oversized || length == MaxLine)
            {
                result.Skipped++;
                continue;
            }

            ReadOnlySpan<byte> bytes = buffer.AsSpan(0, length);
            int carriageReturn = bytes.IndexOf((byte)'\r');
            if (carriageReturn >= 0)
                bytes = bytes[..carriageReturn];
            if (lineNumber == 1 && bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                bytes = bytes[3..];

            string line;
            try
            {
                line = Utf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                line = Fallback.GetString(bytes);
            }
            if (line.TrimStart(' ', '\t').Length == 0)
                continue;

            string[]? fields = Cdr.Split(line);
            if (fields is not null && Cdr.Header(fields) is { } header)
            {
                schema = header;
                continue;
            }
            string[]? record = fields is null ? null : Cdr.Record(fields, schema);
            if (record is null)
            {
                result.Skipped++;
                continue;
            }
            if (result.Rows.Count == MaxRows)
            {
                result.Limited = true;
                break;
            }
            record[(int)Column.Source] = $"{name} : {lineNumber}";
            result.Rows.Add(record);
        }
    }
}
