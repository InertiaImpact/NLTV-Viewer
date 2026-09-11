namespace NltvViewer.Core.Configuration;

public sealed class ViewerSettings
{
    public string ReportRoot { get; set; } = string.Empty;

    public int PollIntervalSeconds { get; set; } = 60;

    public List<ShowGroupSettings> ShowGroups { get; set; } = [];
}

public sealed class ShowGroupSettings
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string? SourcePattern { get; set; }

    public TimeOnly? StartTime { get; set; }

    public TimeOnly? EndTime { get; set; }
}
