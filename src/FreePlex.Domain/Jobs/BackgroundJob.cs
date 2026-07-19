using FreePlex.Domain.Enums;

namespace FreePlex.Domain.Jobs;

/// <summary>Persisted journal entry for a background task (scan/import/metadata/...).</summary>
public class BackgroundJob
{
    private BackgroundJob() { }

    public BackgroundJob(JobType type, DateTimeOffset now, Guid? libraryId = null, string? payloadJson = null)
    {
        Id = Guid.CreateVersion7();
        Type = type;
        State = JobState.Queued;
        LibraryId = libraryId;
        PayloadJson = payloadJson;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public JobType Type { get; private set; }
    public JobState State { get; private set; }
    public double Progress { get; private set; }
    public string? Message { get; private set; }
    public string? PayloadJson { get; private set; }
    public Guid? LibraryId { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void Start(DateTimeOffset now)
    {
        State = JobState.Running;
        StartedAt = now;
        Progress = 0;
    }

    public void Report(double progress, string? message)
    {
        Progress = Math.Clamp(progress, 0, 1);
        Message = message;
    }

    public void Succeed(DateTimeOffset now, string? message = null)
    {
        State = JobState.Succeeded;
        Progress = 1;
        Message = message ?? Message;
        FinishedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        State = JobState.Failed;
        Error = error;
        FinishedAt = now;
    }

    /// <summary>Only pending/running jobs can be cancelled; finished ones keep their state.</summary>
    public bool Cancel(DateTimeOffset now)
    {
        if (State is not (JobState.Queued or JobState.Running))
            return false;
        State = JobState.Cancelled;
        FinishedAt = now;
        return true;
    }
}
