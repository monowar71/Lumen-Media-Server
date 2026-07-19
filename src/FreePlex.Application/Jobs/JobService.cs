using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Domain.Jobs;

namespace FreePlex.Application.Jobs;

public static class JobMapper
{
    public static JobDto Map(BackgroundJob job) => new()
    {
        Id = job.Id,
        Type = job.Type,
        State = job.State,
        Progress = job.Progress,
        Message = job.Message,
        LibraryId = job.LibraryId,
        StartedAt = job.StartedAt,
        FinishedAt = job.FinishedAt,
        Error = job.Error,
    };
}

public sealed class JobService(IUnitOfWork uow, TimeProvider clock)
{
    public async Task<PagedResult<JobDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var paged = await uow.Jobs.ListAsync(page, pageSize, ct);
        return new PagedResult<JobDto>(
            paged.Items.Select(JobMapper.Map).ToList(),
            paged.Page,
            paged.PageSize,
            paged.Total);
    }

    public async Task<JobDto> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await uow.Jobs.GetByIdAsync(id, ct)
                  ?? throw new NotFoundException("Job not found.");
        return JobMapper.Map(job);
    }

    public async Task<JobDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var job = await uow.Jobs.GetByIdAsync(id, ct)
                  ?? throw new NotFoundException("Job not found.");
        if (!job.Cancel(clock.GetUtcNow()))
            throw new ConflictException($"Job is already {job.State} and cannot be cancelled.");
        await uow.SaveChangesAsync(ct);
        return JobMapper.Map(job);
    }
}
