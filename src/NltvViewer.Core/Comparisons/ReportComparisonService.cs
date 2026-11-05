using NltvViewer.Core.Configuration;
using NltvViewer.Core.Filtering;
using NltvViewer.Core.Reports;

namespace NltvViewer.Core.Comparisons;

public interface IReportComparisonService
{
    ComparisonView Build(
        RatingReport selected,
        IReadOnlyDictionary<DateOnly, RatingReport> comparisonReports,
        IReadOnlyList<ShowGroupSettings> showGroups,
        RatingSelection? selection = null,
        IReadOnlyList<ComparisonTarget>? comparisonTargets = null);
}

public sealed class ReportComparisonService : IReportComparisonService
{
    public static IReadOnlyList<ComparisonPeriod> Periods { get; } =
    [
        new("last-week", "Last week", 7),
        new("two-weeks-ago", "Two weeks ago", 14),
        new("last-year", "Same weekday last year", 364)
    ];

    public static IReadOnlyList<ComparisonTarget> CreateDefaultTargets(DateOnly selectedDate) =>
        Periods.Select(period => new ComparisonTarget(
            period.Id,
            period.Label,
            selectedDate.AddDays(-period.DaysBack))).ToArray();

    public ComparisonView Build(
        RatingReport selected,
        IReadOnlyDictionary<DateOnly, RatingReport> comparisonReports,
        IReadOnlyList<ShowGroupSettings> showGroups,
        RatingSelection? selection = null,
        IReadOnlyList<ComparisonTarget>? comparisonTargets = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(comparisonReports);
        ArgumentNullException.ThrowIfNull(showGroups);
        var capabilities = selected.EffectiveCapabilities;
        var resolvedSelection = capabilities.Resolve(selection);
        var targets = ResolveTargets(selected.ReportDate, comparisonTargets);

        var reportMap = targets.ToDictionary(
            target => target.Id,
            target => comparisonReports.GetValueOrDefault(target.TargetDate),
            StringComparer.OrdinalIgnoreCase);

        var output = new List<ShowComparison>();
        foreach (var group in showGroups)
        {
            if (!IsUsable(group))
            {
                output.Add(new ShowComparison(
                    group.Id,
                    string.IsNullOrWhiteSpace(group.DisplayName) ? "Unnamed show" : group.DisplayName.Trim(),
                    group.SourcePattern ?? string.Empty,
                    string.Empty,
                    [],
                    ShowComparisonState.InvalidConfiguration,
                    "This show has no identifier or usable wildcard aliases."));
                continue;
            }

            var currentRuns = FindRuns(selected, group);
            if (currentRuns.Count == 0)
            {
                output.Add(new ShowComparison(
                    group.Id,
                    string.IsNullOrWhiteSpace(group.DisplayName) ? group.Id : group.DisplayName.Trim(),
                    group.SourcePattern ?? string.Empty,
                    string.Empty,
                    [],
                    ShowComparisonState.MissingOrPreempted,
                    $"No matching quarter-hour segments were found for {selected.ReportDate:yyyy-MM-dd}; the show may be preempted or missing from the report."));
                continue;
            }

            var comparisonRuns = reportMap.ToDictionary(
                pair => pair.Key,
                pair => pair.Value is null ? [] : FindRuns(pair.Value, group));

            foreach (var currentRun in currentRuns)
            {
                var referenceRuns = comparisonRuns.ToDictionary(
                    pair => pair.Key,
                    pair => FindBestRun(currentRun, pair.Value));

                var segmentResults = new List<SegmentComparison>(currentRun.Segments.Count);
                for (var index = 0; index < currentRun.Segments.Count; index++)
                {
                    var current = currentRun.Segments[index];
                    var currentValue = selected.GetValue(
                        current,
                        resolvedSelection.DemographicId,
                        resolvedSelection.MetricId);
                    var values = targets.Select(target =>
                    {
                        var referenceReport = reportMap[target.Id];
                        var referenceRun = referenceRuns[target.Id];
                        var reference = referenceRun?.Segments.ElementAtOrDefault(index);
                        var referenceValue = reference is null || referenceReport is null
                            ? null
                            : referenceReport.GetValue(
                                reference,
                                resolvedSelection.DemographicId,
                                resolvedSelection.MetricId);
                        var state = referenceReport is null
                            ? ComparisonTargetState.MissingReport
                            : referenceRun is null
                                ? ComparisonTargetState.MissingShow
                                : reference is null
                                    ? ComparisonTargetState.MissingSegment
                                    : referenceValue is null
                                        ? ComparisonTargetState.MissingValue
                                        : ComparisonTargetState.Available;
                        return new PeriodComparison(
                            new ComparisonPeriod(
                                target.Id,
                                target.Label,
                                selected.ReportDate.DayNumber - target.TargetDate.DayNumber),
                            target.TargetDate,
                            reference?.TimeLabel,
                            reference?.ProgramName,
                            string.Equals(resolvedSelection.MetricId, "imp", StringComparison.OrdinalIgnoreCase)
                                ? referenceValue
                                : null,
                            CalculateDelta(currentValue, referenceValue),
                            referenceValue,
                            target,
                            state);
                    }).ToArray();

                    segmentResults.Add(new SegmentComparison(index + 1, current, values, currentValue));
                }

                output.Add(new ShowComparison(
                    group.Id,
                    string.IsNullOrWhiteSpace(group.DisplayName) ? currentRun.Segments[0].ProgramName : group.DisplayName.Trim(),
                    currentRun.Segments[0].ViewingSource,
                    currentRun.Segments[0].ProgramName,
                    segmentResults,
                    ShowComparisonState.Available));
            }
        }

        return new ComparisonView(
            selected.ReportDate,
            selected.SourceLastModifiedUtc,
            output,
            capabilities,
            resolvedSelection,
            targets);
    }

    public static MetricDelta? CalculateDelta(decimal? current, decimal? reference)
    {
        if (current is null || reference is null)
        {
            return null;
        }

        var absolute = current.Value - reference.Value;
        decimal? percentage = reference.Value == 0
            ? null
            : decimal.Round(absolute / reference.Value * 100m, 1, MidpointRounding.AwayFromZero);
        return new MetricDelta(absolute, percentage);
    }

    private static IReadOnlyList<ComparisonTarget> ResolveTargets(
        DateOnly selectedDate,
        IReadOnlyList<ComparisonTarget>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return CreateDefaultTargets(selectedDate);
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in requested)
        {
            if (string.IsNullOrWhiteSpace(target.Id))
            {
                throw new ArgumentException("Comparison target ids cannot be empty.", nameof(requested));
            }

            if (string.IsNullOrWhiteSpace(target.Label))
            {
                throw new ArgumentException("Comparison target labels cannot be empty.", nameof(requested));
            }

            if (!ids.Add(target.Id))
            {
                throw new ArgumentException(
                    $"Comparison target id '{target.Id}' is duplicated.",
                    nameof(requested));
            }
        }

        return requested.ToArray();
    }

    private static bool IsUsable(ShowGroupSettings group) =>
        !string.IsNullOrWhiteSpace(group.Id) && group.Aliases.Any(alias => !string.IsNullOrWhiteSpace(alias));

    private static List<SegmentRun> FindRuns(RatingReport report, ShowGroupSettings group)
    {
        var matched = report.Segments
            .Where(segment => Matches(segment, group))
            .ToArray();

        var runs = new List<SegmentRun>();
        foreach (var sourceSegments in matched
                     .GroupBy(segment => segment.ViewingSource, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(grouping => grouping.Key, StringComparer.OrdinalIgnoreCase))
        {
            SegmentRun? last = null;
            foreach (var segment in sourceSegments.OrderBy(item => item.BroadcastOrder))
            {
                if (last is not null && last.Segments[^1].BroadcastOrder + 1 == segment.BroadcastOrder)
                {
                    last.Segments.Add(segment);
                }
                else
                {
                    last = new SegmentRun(segment.ViewingSource, [segment]);
                    runs.Add(last);
                }
            }
        }

        return runs.OrderBy(run => run.Segments[0].BroadcastOrder).ToList();
    }

    private static bool Matches(RatingSegment segment, ShowGroupSettings group)
    {
        if (!string.IsNullOrWhiteSpace(group.SourcePattern) &&
            !GlobMatcher.IsMatch(segment.ViewingSource, group.SourcePattern))
        {
            return false;
        }

        if (!group.Aliases.Any(alias => GlobMatcher.IsMatch(segment.ProgramName, alias)))
        {
            return false;
        }

        if (group.StartTime is null || group.EndTime is null || group.StartTime == group.EndTime)
        {
            return true;
        }

        var minute = segment.StartMinute;
        var start = group.StartTime.Value.Hour * 60 + group.StartTime.Value.Minute;
        var end = group.EndTime.Value.Hour * 60 + group.EndTime.Value.Minute;
        return start < end ? minute >= start && minute < end : minute >= start || minute < end;
    }

    private static SegmentRun? FindBestRun(SegmentRun current, IReadOnlyList<SegmentRun> candidates) =>
        candidates
            .Where(candidate => string.Equals(candidate.Source, current.Source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => CircularDistance(
                current.Segments[0].StartMinute,
                candidate.Segments[0].StartMinute))
            .FirstOrDefault();

    private static int CircularDistance(int left, int right)
    {
        var distance = Math.Abs(left - right);
        return Math.Min(distance, 1440 - distance);
    }

    private sealed record SegmentRun(string Source, List<RatingSegment> Segments);
}

public sealed record ComparisonPeriod(string Id, string Label, int DaysBack);

public sealed record ComparisonTarget(string Id, string Label, DateOnly TargetDate);

public sealed record ComparisonView(
    DateOnly SelectedDate,
    DateTimeOffset SourceLastModifiedUtc,
    IReadOnlyList<ShowComparison> Shows,
    RatingCapabilities Capabilities,
    RatingSelection Selection,
    IReadOnlyList<ComparisonTarget> Targets);

public sealed record ShowComparison(
    string GroupId,
    string DisplayName,
    string ViewingSource,
    string ProgramName,
    IReadOnlyList<SegmentComparison> Segments,
    ShowComparisonState State = ShowComparisonState.Available,
    string? StateMessage = null);

public enum ShowComparisonState
{
    Available,
    MissingOrPreempted,
    InvalidConfiguration
}

public sealed record SegmentComparison(
    int SegmentNumber,
    RatingSegment Current,
    IReadOnlyList<PeriodComparison> Comparisons,
    decimal? Value);

public sealed record PeriodComparison(
    ComparisonPeriod Period,
    DateOnly TargetDate,
    string? ActualTimeLabel,
    string? ActualProgramName,
    decimal? Impressions,
    MetricDelta? Delta,
    decimal? Value,
    ComparisonTarget Target,
    ComparisonTargetState State);

public enum ComparisonTargetState
{
    Available,
    MissingReport,
    MissingShow,
    MissingSegment,
    MissingValue
}

public sealed record MetricDelta(decimal Absolute, decimal? Percentage);
