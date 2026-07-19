using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Contracts;

public sealed record JobDto
{
    public required Guid Id { get; init; }
    public required JobType Type { get; init; }
    public required JobState State { get; init; }
    public double Progress { get; init; }
    public string? Message { get; init; }
    public Guid? LibraryId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Error { get; init; }
}

public sealed record ImportJobDto
{
    public required Guid Id { get; init; }
    public required string SourcePath { get; init; }
    public required ImportStatus Status { get; init; }
    public object? Parsed { get; init; }
    public object? Candidates { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
