using NltvViewer.Core.Comparisons;
using NltvViewer.Core.Configuration;
using NltvViewer.Core.Filtering;
using NltvViewer.Core.Reports;

namespace NltvViewer.Tests;

public sealed class ReportComparisonServiceTests
{
    private readonly ReportComparisonService _service = new();

    [Fact]
    public void BuildUsesAliasesAndSegmentOrdinalWhenAShowMoves()
    {
        var selectedDate = new DateOnly(2026, 9, 5);
        var selected = Report(
            selectedDate,
            Segment("ALPHA 1.1", "ALPHA NWS-AT 6", "6:00pm - 6:15pm", 18 * 60, 0, 200m),
            Segment("ALPHA 1.1", "ALPHA NWS-AT 6", "6:15pm - 6:30pm", 18 * 60 + 15, 1, 300m));
        var priorWeek = Report(
            selectedDate.AddDays(-7),
            Segment("ALPHA 1.1", "ALPHA NEWS NOW AT 6", "6:30pm - 6:45pm", 18 * 60 + 30, 0, 100m),
            Segment("ALPHA 1.1", "ALPHA NEWS NOW AT 6", "6:45pm - 7:00pm", 18 * 60 + 45, 1, 250m));
        var group = new ShowGroupSettings
        {
            Id = "evening-six",
            DisplayName = "Evening News",
            SourcePattern = "ALPHA 1.?",
            Aliases = ["ALPHA NWS-AT 6", "ALPHA NEWS NOW AT 6"]
        };

        var result = _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport> { [priorWeek.ReportDate] = priorWeek },
            [group]);

        var show = Assert.Single(result.Shows);
        Assert.Equal("Evening News", show.DisplayName);
        Assert.Equal(2, show.Segments.Count);
        var firstComparison = Assert.Single(show.Segments[0].Comparisons, item =>
            item.Period.Id == "last-week");
        Assert.Equal("6:30pm - 6:45pm", firstComparison.ActualTimeLabel);
        Assert.Equal(100m, firstComparison.Impressions);
        Assert.Equal(new MetricDelta(100m, 100m), firstComparison.Delta);
    }

    [Fact]
    public void BuildUsesSevenFourteenAndThreeHundredSixtyFourDayTargets()
    {
        var selectedDate = new DateOnly(2026, 9, 4);
        var selected = Report(
            selectedDate,
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 5585m));
        var reports = new[]
        {
            Report(selectedDate.AddDays(-7), Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 5140m)),
            Report(selectedDate.AddDays(-14), Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 7806m)),
            Report(selectedDate.AddDays(-364), Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 3764m))
        };
        var group = new ShowGroupSettings
        {
            Id = "news",
            DisplayName = "News",
            Aliases = ["ALPHA NEWS*"]
        };

        var result = _service.Build(selected, reports.ToDictionary(report => report.ReportDate), [group]);

        var comparisons = Assert.Single(Assert.Single(result.Shows).Segments).Comparisons;
        Assert.Collection(
            comparisons,
            item =>
            {
                Assert.Equal(selectedDate.AddDays(-7), item.TargetDate);
                Assert.Equal(445m, item.Delta?.Absolute);
                Assert.Equal(8.7m, item.Delta?.Percentage);
            },
            item =>
            {
                Assert.Equal(selectedDate.AddDays(-14), item.TargetDate);
                Assert.Equal(-2221m, item.Delta?.Absolute);
                Assert.Equal(-28.5m, item.Delta?.Percentage);
            },
            item =>
            {
                Assert.Equal(selectedDate.AddDays(-364), item.TargetDate);
                Assert.Equal(1821m, item.Delta?.Absolute);
                Assert.Equal(48.4m, item.Delta?.Percentage);
            });
    }

    [Fact]
    public void BuildDistinguishesZeroBaselineFromMissingComparison()
    {
        var selectedDate = new DateOnly(2026, 9, 4);
        var selected = Report(
            selectedDate,
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 100m));
        var zeroBaseline = Report(
            selectedDate.AddDays(-7),
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 0m));
        var group = new ShowGroupSettings { Id = "news", Aliases = ["alpha news*"] };

        var result = _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport> { [zeroBaseline.ReportDate] = zeroBaseline },
            [group]);

        var comparisons = Assert.Single(Assert.Single(result.Shows).Segments).Comparisons;
        Assert.Equal(0m, comparisons[0].Impressions);
        Assert.Equal(100m, comparisons[0].Delta?.Absolute);
        Assert.Null(comparisons[0].Delta?.Percentage);
        Assert.Null(comparisons[1].Impressions);
        Assert.Null(comparisons[1].Delta);
        Assert.Equal(ComparisonTargetState.Available, comparisons[0].State);
        Assert.Equal(ComparisonTargetState.MissingReport, comparisons[1].State);
    }

    [Fact]
    public void BuildEmitsExplicitMissingStateForEveryConfiguredShow()
    {
        var selected = Report(
            new DateOnly(2026, 9, 4),
            Segment("ALPHA 1.1", "A DIFFERENT PROGRAM", "6:00pm - 6:15pm", 1080, 0, 100m));
        var configured = new ShowGroupSettings
        {
            Id = "evening-news",
            DisplayName = "Evening News",
            SourcePattern = "ALPHA*",
            Aliases = ["ALPHA NEWS*"]
        };

        var result = _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport>(),
            [configured]);

        var show = Assert.Single(result.Shows);
        Assert.Equal(ShowComparisonState.MissingOrPreempted, show.State);
        Assert.Empty(show.Segments);
        Assert.Contains("preempted or missing", show.StateMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildComparesAnExplicitDynamicSelection()
    {
        var capabilities = new RatingCapabilities(
            [new("hh", "HH"), new("p18-plus", "P18+")],
            [
                new("rtg", "RTG % (X.X)", "percent", "number", 1),
                new("imp", "IMP", "impressions", "number", 0)
            ],
            [new("hh", "rtg"), new("p18-plus", "imp")],
            "p18-plus",
            "imp");
        var selectedDate = new DateOnly(2026, 9, 4);
        var currentSegment = Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 100m)
            with { Values = [2m, 100m] };
        var priorSegment = Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 50m)
            with { Values = [1m, 50m] };
        var selected = Report(selectedDate, currentSegment) with { Capabilities = capabilities };
        var prior = Report(selectedDate.AddDays(-7), priorSegment) with { Capabilities = capabilities };
        var group = new ShowGroupSettings { Id = "news", Aliases = ["ALPHA NEWS*"] };

        var result = _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport> { [prior.ReportDate] = prior },
            [group],
            new RatingSelection("hh", "rtg"));

        var segment = Assert.Single(Assert.Single(result.Shows).Segments);
        Assert.Equal(new RatingSelection("hh", "rtg"), result.Selection);
        Assert.Equal(2m, segment.Value);
        Assert.Equal(1m, segment.Comparisons[0].Value);
        Assert.Null(segment.Comparisons[0].Impressions);
        Assert.Equal(new MetricDelta(1m, 100m), segment.Comparisons[0].Delta);
    }

    [Fact]
    public void BuildUsesOrderedExactComparisonTargetsAcrossDateBoundaries()
    {
        var selectedDate = new DateOnly(2026, 1, 1);
        var previousDay = new DateOnly(2025, 12, 31);
        var priorMonthEdge = new DateOnly(2025, 11, 30);
        var selected = Report(
            selectedDate,
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 100m));
        var dayReport = Report(
            previousDay,
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 80m));
        var monthReport = Report(
            priorMonthEdge,
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 50m));
        var targets = new[]
        {
            new ComparisonTarget("previous-day", "Previous day", previousDay),
            new ComparisonTarget("custom-month-edge", "Month-end comparison", priorMonthEdge)
        };
        var group = new ShowGroupSettings { Id = "news", Aliases = ["ALPHA NEWS*"] };

        var result = _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport>
            {
                [previousDay] = dayReport,
                [priorMonthEdge] = monthReport
            },
            [group],
            comparisonTargets: targets);

        Assert.Equal(targets, result.Targets);
        var comparisons = Assert.Single(Assert.Single(result.Shows).Segments).Comparisons;
        Assert.Collection(
            comparisons,
            comparison =>
            {
                Assert.Equal("previous-day", comparison.Target.Id);
                Assert.Equal("Previous day", comparison.Target.Label);
                Assert.Equal(previousDay, comparison.TargetDate);
                Assert.Equal(20m, comparison.Delta?.Absolute);
                Assert.Equal(ComparisonTargetState.Available, comparison.State);
            },
            comparison =>
            {
                Assert.Equal("custom-month-edge", comparison.Target.Id);
                Assert.Equal(priorMonthEdge, comparison.TargetDate);
                Assert.Equal(50m, comparison.Delta?.Absolute);
            });
    }

    [Fact]
    public void BuildRepresentsMissingExactTargetAsMissingNeverZero()
    {
        var selectedDate = new DateOnly(2025, 3, 31);
        var selected = Report(
            selectedDate,
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 100m));
        var target = new ComparisonTarget(
            "last-month",
            "Previous month",
            new DateOnly(2025, 2, 28));
        var group = new ShowGroupSettings { Id = "news", Aliases = ["ALPHA NEWS*"] };

        var result = _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport>(),
            [group],
            comparisonTargets: [target]);

        var comparison = Assert.Single(Assert.Single(Assert.Single(result.Shows).Segments).Comparisons);
        Assert.Equal(ComparisonTargetState.MissingReport, comparison.State);
        Assert.Null(comparison.Value);
        Assert.Null(comparison.Impressions);
        Assert.Null(comparison.Delta);
    }

    [Fact]
    public void BuildUsesLegacyTargetsWhenCustomTargetsAreOmitted()
    {
        var selectedDate = new DateOnly(2024, 2, 29);
        var selected = Report(
            selectedDate,
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 100m));
        var group = new ShowGroupSettings { Id = "news", Aliases = ["ALPHA NEWS*"] };

        var result = _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport>(),
            [group]);

        Assert.Equal(
            [selectedDate.AddDays(-7), selectedDate.AddDays(-14), selectedDate.AddDays(-364)],
            result.Targets.Select(target => target.TargetDate));
        Assert.Equal(["last-week", "two-weeks-ago", "last-year"], result.Targets.Select(target => target.Id));
    }

    [Fact]
    public void BuildRejectsDuplicateExactTargetIds()
    {
        var selected = Report(
            new DateOnly(2026, 1, 1),
            Segment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 0, 100m));
        var group = new ShowGroupSettings { Id = "news", Aliases = ["ALPHA NEWS*"] };

        Assert.Throws<ArgumentException>(() => _service.Build(
            selected,
            new Dictionary<DateOnly, RatingReport>(),
            [group],
            comparisonTargets:
            [
                new("same", "First", new DateOnly(2025, 12, 31)),
                new("SAME", "Second", new DateOnly(2025, 12, 1))
            ]));
    }

    [Theory]
    [InlineData("  Alpha   News-At 6  ", "alpha news*")]
    [InlineData("ALPHA NEWS-AT 6", "ALPHA NEWS-AT ?")]
    public void GlobMatcherIsCaseInsensitiveAndNormalizesWhitespace(string value, string pattern)
    {
        Assert.True(GlobMatcher.IsMatch(value, pattern));
    }

    [Fact]
    public void GlobMatcherTreatsRegexCharactersAsLiterals()
    {
        Assert.True(GlobMatcher.IsMatch("NEWS [LIVE]", "NEWS [LIVE]"));
        Assert.False(GlobMatcher.IsMatch("NEWS L", "NEWS [LIVE]"));
    }

    private static RatingReport Report(DateOnly date, params RatingSegment[] segments) =>
        new(date, "anonymous.xlsx", "Daily", DateTimeOffset.UnixEpoch, segments);

    private static RatingSegment Segment(
        string source,
        string program,
        string time,
        int startMinute,
        int order,
        decimal? impressions) =>
        new(source, program, time, startMinute, (startMinute + 15) % 1440, order, impressions);
}
