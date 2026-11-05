using System.Diagnostics;
using System.Globalization;
using NltvViewer.Core.Comparisons;

namespace NltvViewer.App.Comparisons;

public sealed record ResolvedComparisonSpec(string Spec, ComparisonTarget Target);

public sealed class ComparisonRequestException : Exception
{
    public ComparisonRequestException(string message)
        : base(message)
    {
    }
}

public static class ComparisonRequestParser
{
    public const int MaximumComparisons = 4;

    public static IReadOnlyList<string> LegacyDefaults { get; } =
    [
        "last-week",
        "two-weeks-ago",
        "last-year",
    ];

    public static IReadOnlyList<ResolvedComparisonSpec> Resolve(
        DateOnly selectedDate,
        IReadOnlyList<string>? requestedSpecs)
    {
        var specs = NormalizeAndValidate(
            requestedSpecs is null || requestedSpecs.Count == 0
                ? LegacyDefaults
                : requestedSpecs);

        var resolved = new List<ResolvedComparisonSpec>(specs.Count);
        var usedDates = new HashSet<DateOnly>();
        foreach (var spec in specs)
        {
            ResolvedComparisonSpec item;
            try
            {
                item = ResolveOne(selectedDate, spec);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ComparisonRequestException(
                    $"Comparison '{spec}' cannot be resolved from selected date "
                    + $"{selectedDate:yyyy-MM-dd}.");
            }

            if (!usedDates.Add(item.Target.TargetDate))
            {
                throw new ComparisonRequestException(
                    $"More than one comparison resolves to {item.Target.TargetDate:yyyy-MM-dd}.");
            }

            resolved.Add(item);
        }

        return resolved;
    }

    public static IReadOnlyList<string> NormalizeAndValidate(IReadOnlyList<string>? specs)
    {
        if (specs is null || specs.Count == 0)
        {
            throw new ComparisonRequestException(
                "At least one comparison must be specified.");
        }

        if (specs.Count > MaximumComparisons)
        {
            throw new ComparisonRequestException(
                $"A maximum of {MaximumComparisons} comparisons can be requested.");
        }

        var normalized = new List<string>(specs.Count);
        var usedSpecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawSpec in specs)
        {
            var spec = NormalizeAndValidateOne(rawSpec);
            if (!usedSpecs.Add(spec))
            {
                throw new ComparisonRequestException(
                    $"Comparison '{spec}' was requested more than once.");
            }

            normalized.Add(spec);
        }

        return normalized;
    }

    private static ResolvedComparisonSpec ResolveOne(DateOnly selectedDate, string spec)
    {
        return spec switch
        {
            "previous-day" => CreatePreset(spec, "Previous day", selectedDate.AddDays(-1)),
            "last-week" => CreatePreset(spec, "Last week", selectedDate.AddDays(-7)),
            "two-weeks-ago" => CreatePreset(spec, "Two weeks ago", selectedDate.AddDays(-14)),
            "last-month" => CreatePreset(spec, "Last month", selectedDate.AddMonths(-1)),
            "last-year" => CreatePreset(
                spec,
                "Same weekday last year",
                selectedDate.AddDays(-364)),
            _ when spec.StartsWith("date:", StringComparison.Ordinal) => ResolveExactDate(spec),
            _ => throw new UnreachableException(),
        };
    }

    private static string NormalizeAndValidateOne(string? rawSpec)
    {
        var spec = rawSpec?.Trim().ToLowerInvariant() ?? string.Empty;
        if (spec is "previous-day" or "last-week" or "two-weeks-ago" or "last-month" or "last-year")
        {
            return spec;
        }

        if (spec.StartsWith("date:", StringComparison.Ordinal))
        {
            var dateText = spec["date:".Length..];
            if (DateOnly.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                return spec;
            }

            throw new ComparisonRequestException(
                $"Comparison '{spec}' must use date:YYYY-MM-DD with a valid calendar date.");
        }

        throw new ComparisonRequestException(
            $"Unknown comparison '{rawSpec}'. Use previous-day, last-week, "
            + "two-weeks-ago, last-month, last-year, or date:YYYY-MM-DD.");
    }

    private static ResolvedComparisonSpec ResolveExactDate(string spec)
    {
        var dateText = spec["date:".Length..];
        if (!DateOnly.TryParseExact(
                dateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var targetDate))
        {
            throw new ComparisonRequestException(
                $"Comparison '{spec}' must use date:YYYY-MM-DD with a valid calendar date.");
        }

        return new ResolvedComparisonSpec(
            spec,
            new ComparisonTarget(
                $"date-{targetDate:yyyy-MM-dd}",
                $"Specific date ({targetDate:yyyy-MM-dd})",
                targetDate));
    }

    private static ResolvedComparisonSpec CreatePreset(
        string spec,
        string label,
        DateOnly targetDate) =>
        new(spec, new ComparisonTarget(spec, label, targetDate));
}
