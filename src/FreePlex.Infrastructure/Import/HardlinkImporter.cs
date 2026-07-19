using FreePlex.Application.Abstractions;

namespace FreePlex.Infrastructure.Import;

/// <summary>
/// Placeholder file importer (folder-watch import is roadmap phase P4). Kept behind the port
/// so the pipeline can be wired later without touching the application layer.
/// </summary>
public sealed class HardlinkImporter : IFileImporter
{
    public Task<ImportResult> ImportAsync(string sourcePath, CancellationToken ct) =>
        Task.FromResult(new ImportResult(false, null, "Import pipeline is not implemented in this phase."));
}
