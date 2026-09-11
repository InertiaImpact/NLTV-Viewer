using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NltvViewer.Tests;

internal static class SyntheticNielsenWorkbook
{
    public static void CreateCurrent(
        string path,
        DateOnly reportDate,
        IReadOnlyList<SyntheticSegment> segments,
        bool includeUnsupportedSheet = false,
        DateOnly? rangeEnd = null)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        if (includeUnsupportedSheet)
        {
            AddSheet(workbookPart, sheets, "Instructions", [
                CreateRow(1, TextCell("A1", "Synthetic instructions only"))
            ]);
        }

        var endDate = rangeEnd ?? reportDate;
        var rows = new List<Row>
        {
            CreateRow(1, TextCell("A1", "Synthetic Audience Report")),
            CreateRow(10, TextCell(
                "A10",
                $"Dates: {reportDate:MM/dd/yyyy} - {endDate:MM/dd/yyyy}")),
            CreateRow(
                13,
                TextCell("A13", "Daypart"),
                TextCell("B13", "Viewing Source"),
                TextCell("C13", "ALPHA 1.1"),
                TextCell("H13", "ALPHA 1.2")),
            CreateRow(
                14,
                TextCell("A14", "Daypart"),
                TextCell("B14", "Demo"),
                TextCell("C14", "P18+"),
                TextCell("H14", "P18+")),
            CreateRow(
                15,
                TextCell("A15", "Daypart"),
                TextCell("B15", "Time / Metrics"),
                TextCell("C15", "Dom Pgm Name"),
                TextCell("D15", "RTG % (X.X)"),
                TextCell("E15", "SHR %"),
                TextCell("F15", "IMP"),
                TextCell("G15", "HUT/PUT % (X.X)"),
                TextCell("H15", "Dom Pgm Name"),
                TextCell("I15", "RTG % (X.X)"),
                TextCell("J15", "SHR %"),
                TextCell("K15", "IMP"),
                TextCell("L15", "HUT/PUT % (X.X)"))
        };

        uint rowNumber = 16;
        foreach (var segment in segments)
        {
            var cells = new List<Cell>
            {
                TextCell($"B{rowNumber}", segment.TimeLabel)
            };
            AddSourceCells(cells, rowNumber, "C", "F", segment.SourceOneProgram, segment.SourceOneImpressions);
            AddSourceCells(cells, rowNumber, "H", "K", segment.SourceTwoProgram, segment.SourceTwoImpressions);
            rows.Add(CreateRow(rowNumber, [.. cells]));
            rowNumber++;
        }

        AddSheet(workbookPart, sheets, $"{reportDate:M.d.yy} Daily", rows);
        workbookPart.Workbook.Save();
    }

    public static void CreateUnsupported(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        AddSheet(workbookPart, sheets, "Program Summary", [
            CreateRow(1, TextCell("A1", "Viewing Source"), TextCell("B1", "Program Average")),
            CreateRow(2, TextCell("A2", "ALPHA 1.1"), TextCell("B2", "SYNTHETIC NEWS"))
        ]);
        workbookPart.Workbook.Save();
    }

    private static void AddSourceCells(
        List<Cell> cells,
        uint rowNumber,
        string programColumn,
        string impressionColumn,
        string? program,
        decimal? impressions)
    {
        if (program is not null)
        {
            cells.Add(TextCell($"{programColumn}{rowNumber}", program));
        }

        if (impressions is not null)
        {
            cells.Add(NumberCell($"{impressionColumn}{rowNumber}", impressions.Value));
        }
    }

    private static void AddSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        string name,
        IEnumerable<Row> rows)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        sheetData.Append(rows);
        worksheetPart.Worksheet = new Worksheet(sheetData);
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = (uint)sheets.Count() + 1,
            Name = name
        });
    }

    private static Row CreateRow(uint rowNumber, params Cell[] cells)
    {
        var row = new Row { RowIndex = rowNumber };
        row.Append(cells);
        return row;
    }

    private static Cell TextCell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value))
    };

    private static Cell NumberCell(string reference, decimal value) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
    };
}

internal sealed record SyntheticSegment(
    string TimeLabel,
    string? SourceOneProgram,
    decimal? SourceOneImpressions,
    string? SourceTwoProgram = null,
    decimal? SourceTwoImpressions = null);

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"NltvViewerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
