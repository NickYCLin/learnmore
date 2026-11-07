namespace LearnMore.Models;

public sealed record ManagementQueuePagination(string Path, string Label, int Page, int PageSize, long TotalPages);
