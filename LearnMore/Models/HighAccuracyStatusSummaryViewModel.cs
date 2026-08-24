namespace LearnMore.Models;

public class HighAccuracyStatusSummaryViewModel
{
    public string? Status { get; set; }
    public string? ReasonText { get; set; }
    public string ContainerCssClass { get; set; } = "d-flex flex-column gap-1";
    public string ReasonCssClass { get; set; } = "text-muted";
    public string? BadgeTitle { get; set; }
}
