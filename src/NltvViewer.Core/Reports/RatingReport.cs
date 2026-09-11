namespace NltvViewer.Core.Reports;

public sealed record RatingReport(
    DateOnly ReportDate,
    string SourcePath,
    string WorksheetName,
    DateTimeOffset SourceLastModifiedUtc,
    IReadOnlyList<RatingSegment> Segments,
    RatingCapabilities? Capabilities = null,
    bool IsDetailComplete = true)
{
    public RatingCapabilities EffectiveCapabilities =>
        Capabilities ?? (Segments.Any(segment => segment.P18PlusImpressions is not null)
            ? RatingCapabilities.LegacyP18PlusImpressions
            : RatingCapabilities.Empty);

    public decimal? GetValue(
        RatingSegment segment,
        string demographicId,
        string metricId)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var capabilities = EffectiveCapabilities;
        for (var index = 0; index < capabilities.AvailableSelections.Count; index++)
        {
            var selection = capabilities.AvailableSelections[index];
            if (string.Equals(selection.DemographicId, demographicId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(selection.MetricId, metricId, StringComparison.OrdinalIgnoreCase))
            {
                return segment.Values is not null && index < segment.Values.Count
                    ? segment.Values[index]
                    : FallbackValue(segment, demographicId, metricId);
            }
        }

        return FallbackValue(segment, demographicId, metricId);
    }

    private static decimal? FallbackValue(
        RatingSegment segment,
        string demographicId,
        string metricId) =>
        string.Equals(demographicId, "p18-plus", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(metricId, "imp", StringComparison.OrdinalIgnoreCase)
            ? segment.P18PlusImpressions
            : null;
}

public sealed record RatingSegment(
    string ViewingSource,
    string ProgramName,
    string TimeLabel,
    int StartMinute,
    int EndMinute,
    int BroadcastOrder,
    decimal? P18PlusImpressions,
    IReadOnlyList<decimal?>? Values = null);

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
