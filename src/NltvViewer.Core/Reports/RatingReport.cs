namespace NltvViewer.Core.Reports;

public sealed record RatingReport(
    DateOnly ReportDate,
    string SourcePath,
    string WorksheetName,
    DateTimeOffset SourceLastModifiedUtc,
    IReadOnlyList<RatingSegment> Segments);

public sealed record RatingSegment(
    string ViewingSource,
    string ProgramName,
    string TimeLabel,
    int StartMinute,
    int EndMinute,
    int BroadcastOrder,
    decimal? P18PlusImpressions);

public enum ReportParseStatus
{
    Success,
    UnsupportedFormat,
    InvalidFile,
    Unavailable
}

public sealed record ReportParseResult(
    ReportParseStatus Status,
    IReadOnlyList<RatingReport> Reports,
    string? ErrorCode = null,
    string? Message = null,
    IReadOnlyList<string>? Warnings = null)
{
    public bool IsSuccess => Status == ReportParseStatus.Success;

    public static ReportParseResult Success(
        IReadOnlyList<RatingReport> reports,
        IReadOnlyList<string>? warnings = null) =>
        new(ReportParseStatus.Success, reports, Warnings: warnings);

    public static ReportParseResult Failure(
        ReportParseStatus status,
        string errorCode,
        string message) =>
        new(status, Array.Empty<RatingReport>(), errorCode, message);
}
