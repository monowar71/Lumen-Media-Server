using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;

namespace FreePlex.Application.Settings;

public sealed class SettingsService(ISettingsStore store)
{
    public ServerSettingsDto Get() => store.Get();

    public ServerSettingsDto Update(ServerSettingsDto patch) => store.Update(patch);
}
