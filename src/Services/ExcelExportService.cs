using System.IO;
using System.Text;
using ClosedXML.Excel;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Services;

public interface IExcelExportService
{
    /// <summary>
    /// Regenerate the snapshot workbook from the CSVs in <paramref name="dataDir"/>. Returns each sheet's
    /// row count, in sheet order. Throws <see cref="IOException"/> when the file is open in Excel.
    /// </summary>
    IReadOnlyList<(string Sheet, int Rows)> Export(string dataDir, string xlsxPath);
}

/// <summary>
/// A one-way snapshot of the CSVs into one workbook, for backup, review or sharing. The app is the
/// editor; nothing reads the workbook back in, which is why its first sheet says so in bold.
/// </summary>
public sealed class ExcelExportService : IExcelExportService
{
    public const string FileName = "deadlock_advisor_data.xlsx";

    private static readonly XLColor _headerFill = XLColor.FromHtml("#2F5233");
    private static readonly XLColor _warning = XLColor.FromHtml("#B03A2E");

    private static readonly (string Sheet, string File, int[] Widths)[] _sheets =
    [
        ("Categories", DataStore.CategoriesFile, [32, 34, 11, 11, 80]),
        ("Heroes", DataStore.HeroesFile, [20, 24, 12]),
        ("Items", DataStore.ItemsFile, [26, 30, 12, 8, 14, 8]),
        ("HeroCategoryScores", DataStore.HeroScoresFile, [20, 34, 10]),
        ("ItemFormulaCoefficients", DataStore.ItemCoefficientsFile, [28, 34, 14, 14, 12]),
        ("TraitWeights", DataStore.TraitWeightsFile, [34, 14, 10]),
        ("StatRules", DataStore.StatRulesFile, [28, 34, 14, 10, 18, 80]),
        ("ItemStats", DataStore.ItemStatsFile, [26, 30, 24, 10, 8, 12]),
    ];

    public IReadOnlyList<(string Sheet, int Rows)> Export(string dataDir, string xlsxPath)
    {
        using var workbook = new XLWorkbook();
        var readMe = workbook.Worksheets.Add("Read Me");
        var counts = new List<(string, int)>();
        foreach (var (sheet, file, widths) in _sheets)
        {
            var path = Path.Combine(dataDir, file);
            var records = File.Exists(path) ? CsvReader.ReadRecords(File.ReadAllText(path, Encoding.UTF8)) : [];
            counts.Add((sheet, WriteTable(workbook.Worksheets.Add(sheet), records, widths)));
        }
        WriteReadMe(readMe, counts);
        workbook.SaveAs(xlsxPath);
        return counts;
    }

    private static int WriteTable(IXLWorksheet sheet, List<List<string>> records, int[] widths)
    {
        if (records.Count == 0)
            return 0;
        var header = records[0];
        var rows = records.Skip(1).ToList();

        for (var column = 0; column < header.Count; column++)
        {
            var cell = sheet.Cell(1, column + 1);
            cell.Value = header[column];
            cell.Style.Font.SetFontName("Calibri").Font.SetFontSize(11).Font.SetBold().Font.SetFontColor(XLColor.White);
            cell.Style.Fill.SetBackgroundColor(_headerFill);
            cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        }

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Count; column++)
                sheet.Cell(row + 2, column + 1).Value = Coerce(rows[row][column]);
            for (var column = 0; column < header.Count; column++)
                sheet.Cell(row + 2, column + 1).Style.Font.SetFontName("Calibri").Font.SetFontSize(11);
        }

        for (var column = 0; column < widths.Length; column++)
            sheet.Column(column + 1).Width = widths[column];
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(2, rows.Count + 1), header.Count).SetAutoFilter();
        return rows.Count;
    }

    /// <summary>Numbers as numbers, so the workbook sorts and filters properly; blanks as empty cells.</summary>
    private static XLCellValue Coerce(string value)
    {
        if (value.Length == 0)
            return Blank.Value;
        if (!NumberFormat.TryParseFloat(value, out var number) || !double.IsFinite(number))
            return value;
        return number;
    }

    private static void WriteReadMe(IXLWorksheet sheet, List<(string Sheet, int Rows)> counts)
    {
        sheet.ShowGridLines = false;
        var lines = new List<(string Text, double Size, bool Bold, XLColor? Color)>
        {
            ("Deadlock Item Advisor — data snapshot", 16, true, null),
            ($"Exported {DateTime.Now:yyyy-MM-dd HH:mm}", 11, false, null),
            ("", 11, false, null),
            ("THIS WORKBOOK IS READ-ONLY. Editing it changes nothing.", 11, true, _warning),
            ("", 11, false, null),
            ("The CSVs under data/ are the source of truth, and the app edits them", 11, false, null),
            ("directly: use the Hero Traits and Item Formulas tabs. This file is just", 11, false, null),
            ("a snapshot for backup, review, or sharing.", 11, false, null),
            ("", 11, false, null),
            ("Contents", 12, true, null),
        };
        lines.AddRange(counts.Select(count => ($"    {count.Sheet}: {count.Rows} rows", 11.0, false, (XLColor?)null)));
        lines.AddRange(
        [
            ("", 11, false, null),
            ("Reminder on the relation column", 12, true, null),
            ("    against — buy the item when an ENEMY has the trait", 11, false, null),
            ("    with    — buy the item when an ALLY has the trait", 11, false, null),
            ("    as      — buy the item when YOUR OWN hero has the trait", 11, false, null),
        ]);

        for (var row = 0; row < lines.Count; row++)
        {
            var (text, size, bold, color) = lines[row];
            var cell = sheet.Cell(row + 1, 1);
            cell.Value = text;
            cell.Style.Font.SetFontName("Calibri").Font.SetFontSize(size).Font.SetBold(bold);
            if (color is not null)
                cell.Style.Font.SetFontColor(color);
        }
        sheet.Column(1).Width = 95;
    }
}
