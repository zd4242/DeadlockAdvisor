using System.IO;
using System.Text;

namespace DeadlockAdvisor.Services.Formats;

/// <summary>One CSV record keyed by the header row, like a row from Python's <c>csv.DictReader</c>.</summary>
public sealed class CsvRow(IReadOnlyDictionary<string, string> values)
{
    /// <summary>The field, or null where the row is too short to have one (DictReader's None).</summary>
    public string? Get(string column) => values.GetValueOrDefault(column);

    /// <summary>The field where a missing one is an error, as <c>row["column"]</c> is in Python.</summary>
    public string Required(string column) =>
        values.TryGetValue(column, out var value) ? value : throw new FormatException($"CSV row has no '{column}' column");

    /// <summary>Python's <c>row.get(column) or fallback</c>: blank counts as missing.</summary>
    public string Or(string column, string fallback) =>
        values.GetValueOrDefault(column) is { Length: > 0 } value ? value : fallback;

    /// <summary>Whether the field is present and non-empty, as <c>if row.get(column)</c> tests it.</summary>
    public bool Has(string column) => !string.IsNullOrEmpty(values.GetValueOrDefault(column));
}

/// <summary>
/// Reads CSVs the way Python's csv module does with its default dialect: comma-separated, "-quoted,
/// "" inside quotes for a quote, and \r\n, \n or \r between records.
/// </summary>
public static class CsvReader
{
    private const char _bom = '﻿';

    public static List<CsvRow> ReadFile(string path) => Read(File.ReadAllText(path, Encoding.UTF8));

    public static List<CsvRow> Read(string text)
    {
        if (text.Length > 0 && text[0] == _bom)
            text = text[1..];

        var records = ParseRecords(text);
        var rows = new List<CsvRow>();
        if (records.Count == 0)
            return rows;

        var header = records[0];
        foreach (var record in records.Skip(1))
        {
            // DictReader skips blank lines rather than yielding a row of Nones.
            if (record.Count == 0)
                continue;

            var values = new Dictionary<string, string>();
            for (var i = 0; i < header.Count && i < record.Count; i++)
                values[header[i]] = record[i];
            rows.Add(new CsvRow(values));
        }
        return rows;
    }

    private static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        var i = 0;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            // A line with nothing on it is an empty record, not one empty field.
            if (fieldStarted || record.Count > 0)
                EndField();
            records.Add(record);
            record = [];
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
                i++;
                continue;
            }

            switch (c)
            {
                case ',':
                    fieldStarted = true;
                    EndField();
                    // A trailing comma still means one more (empty) field follows.
                    fieldStarted = true;
                    break;
                case '"' when field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case '\r':
                    EndRecord();
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
            i++;
        }

        if (fieldStarted || record.Count > 0 || field.Length > 0)
            EndRecord();
        return records;
    }
}

/// <summary>
/// Writes CSVs byte-for-byte like Python's <c>csv.writer</c> defaults: fields quoted only when they
/// hold a comma, quote, \r or \n; quotes doubled; every record ending \r\n; UTF-8 without a BOM.
/// </summary>
public static class CsvWriter
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] ToBytes(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        AppendRecord(builder, header);
        foreach (var row in rows)
            AppendRecord(builder, row);
        return _utf8NoBom.GetBytes(builder.ToString());
    }

    private static void AppendRecord(StringBuilder builder, IReadOnlyList<string> fields)
    {
        // A lone empty field is written quoted so the line doesn't read back as blank.
        if (fields.Count == 1 && fields[0].Length == 0)
        {
            builder.Append("\"\"\r\n");
            return;
        }

        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            AppendField(builder, fields[i]);
        }
        builder.Append("\r\n");
    }

    private static void AppendField(StringBuilder builder, string field)
    {
        if (field.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            builder.Append(field);
            return;
        }

        builder.Append('"');
        builder.Append(field.Replace("\"", "\"\""));
        builder.Append('"');
    }
}
