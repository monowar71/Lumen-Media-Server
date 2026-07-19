using FreePlex.Application.Contracts;

namespace FreePlex.Application.Abstractions;

/// <summary>Holds the mutable server settings (seeded from configuration on startup).</summary>
public interface ISettingsStore
{
    ServerSettingsDto Get();
    ServerSettingsDto Update(ServerSettingsDto patch);
}
