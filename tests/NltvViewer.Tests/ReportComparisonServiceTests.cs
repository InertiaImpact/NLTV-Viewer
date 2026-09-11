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
