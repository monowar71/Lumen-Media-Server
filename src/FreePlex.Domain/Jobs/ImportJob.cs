using FreePlex.Domain.Enums;

namespace FreePlex.Domain.Jobs;

public class ImportJob
{
    private ImportJob() { }

    public ImportJob(string sourcePath, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path is required", nameof(sourcePath));

        Id = Guid.CreateVersion7();
        SourcePath = sourcePath;
        Status = ImportStatus.Pending;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string SourcePath { get; private set; } = null!;
    public ImportStatus Status { get; private set; }
    public string? ParsedJson { get; private set; }
    public string? CandidatesJson { get; private set; }
    public Guid? LinkedMediaItemId { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void SetParsed(string parsedJson, DateTimeOffset now)
    {
        ParsedJson = parsedJson;
        UpdatedAt = now;
    }

    public void SetStatus(ImportStatus status, DateTimeOffset now, string? error = null)
    {
        Status = status;
        Error = error;
        UpdatedAt = now;
    }

    public void LinkTo(Guid mediaItemId, DateTimeOffset now)
    {
        LinkedMediaItemId = mediaItemId;
        Status = ImportStatus.Imported;
        UpdatedAt = now;
    }
}
