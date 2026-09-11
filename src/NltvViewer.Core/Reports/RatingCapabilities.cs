using System.Text;
using System.Text.RegularExpressions;

namespace NltvViewer.Core.Reports;

public sealed record RatingCapabilities(
    IReadOnlyList<RatingDemographicDefinition> Demographics,
    IReadOnlyList<RatingMetricDefinition> Metrics,
    IReadOnlyList<RatingSelection> AvailableSelections,
    string? DefaultDemographicId,
    string? DefaultMetricId)
{
    public static RatingCapabilities Empty { get; } = new([], [], [], null, null);

    public static RatingCapabilities LegacyP18PlusImpressions { get; } = new(
        [new RatingDemographicDefinition("p18-plus", "P18+")],
        [new RatingMetricDefinition("imp", "IMP", "impressions", "number", 0)],
        [new RatingSelection("p18-plus", "imp")],
        "p18-plus",
        "imp");

    public RatingSelection Resolve(RatingSelection? requested = null)
    {
        var demographicId = requested?.DemographicId ?? DefaultDemographicId;
        var metricId = requested?.MetricId ?? DefaultMetricId;
        if (demographicId is null || metricId is null)
        {
            throw new InvalidOperationException("The report does not expose any selectable rating fields.");
        }

        if (!Demographics.Any(item =>
                string.Equals(item.Id, demographicId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Unknown demographic id '{demographicId}'.",
                nameof(requested));
        }

        if (!Metrics.Any(item => string.Equals(item.Id, metricId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown metric id '{metricId}'.", nameof(requested));
        }

        if (!AvailableSelections.Any(selection =>
                string.Equals(selection.DemographicId, demographicId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(selection.MetricId, metricId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Metric '{metricId}' is not available for demographic '{demographicId}'.",
                nameof(requested));
        }

        return new RatingSelection(demographicId, metricId);
    }
}

public sealed record RatingDemographicDefinition(string Id, string Label);

public sealed record RatingMetricDefinition(
    string Id,
    string Label,
    string Unit,
    string Format,
    int Decimals);

public sealed record RatingSelection(string DemographicId, string MetricId);

public static partial class RatingFieldDefinition
{
    public static RatingDemographicDefinition CreateDemographic(string header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        var label = NormalizeLabel(header);
        return new RatingDemographicDefinition(CreateId(label), label);
    }

    public static RatingMetricDefinition CreateMetric(string header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        var label = NormalizeLabel(header);
        var idLabel = PrecisionHintRegex().Replace(label, string.Empty).Trim();
        var id = CreateId(idLabel.Replace("%", string.Empty, StringComparison.Ordinal));
        var isPercentage = label.Contains('%', StringComparison.Ordinal);
        var isImpressions = string.Equals(id, "imp", StringComparison.OrdinalIgnoreCase);
        var decimals = FindPrecision(label) ?? (isImpressions ? 0 : isPercentage ? 1 : 2);
        return new RatingMetricDefinition(
            id,
            label,
            isImpressions ? "impressions" : isPercentage ? "percent" : "number",
            "number",
            decimals);
    }

    public static string CreateId(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var builder = new StringBuilder();
        var pendingSeparator = false;
        foreach (var character in label.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingSeparator = false;
            }
            else if (character == '+')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }

                builder.Append("plus");
                pendingSeparator = true;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string NormalizeLabel(string header) => WhitespaceRegex().Replace(header.Trim(), " ");

    private static int? FindPrecision(string label)
    {
        var match = PrecisionHintRegex().Match(label);
        if (!match.Success)
        {
            return null;
        }

        var dot = match.Value.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return 0;
        }

        return match.Value.Count(character => character is 'X' or 'x') - 1;
    }

    [GeneratedRegex(@"\([^)]*[xX](?:\.[xX]+)?[^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex PrecisionHintRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
