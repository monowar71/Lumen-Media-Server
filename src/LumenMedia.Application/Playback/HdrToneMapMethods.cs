using LumenMedia.Application.Contracts;

namespace LumenMedia.Application.Playback;

/// <summary>
/// HDR→SDR tonemap method ids for playback decision / set-quality.
/// <c>vaapi</c> uses GPU VPP; the rest are software <c>zscale</c>+<c>tonemap=…</c>.
/// </summary>
public static class HdrToneMapMethods
{
    public const string Vaapi = "vaapi";
    public const string Hable = "hable";
    public const string Mobius = "mobius";
    public const string Reinhard = "reinhard";
    public const string Bt2390 = "bt2390";

    private static readonly (string Id, string Label, bool Software)[] All =
    [
        (Vaapi, "Hardware (VAAPI)", false),
        (Hable, "Hable", true),
        (Mobius, "Mobius", true),
        (Reinhard, "Reinhard", true),
        (Bt2390, "BT.2390", true),
    ];

    private static readonly HashSet<string> SoftwareIds = new(StringComparer.OrdinalIgnoreCase)
    {
        Hable, Mobius, Reinhard, Bt2390,
    };

    public static bool IsKnown(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && All.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static bool IsSoftware(string? id) =>
        !string.IsNullOrWhiteSpace(id) && SoftwareIds.Contains(id);

    /// <summary>True when tonemap should stay on the VAAPI VPP path.</summary>
    public static bool UsesVaapi(string? id, string hardwareAccel) =>
        hardwareAccel.Equals("vaapi", StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(id)
            || id.Equals(Vaapi, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<HdrToneMapMethodOption> AvailableFor(string hardwareAccel)
    {
        var vaapi = hardwareAccel.Equals("vaapi", StringComparison.OrdinalIgnoreCase);
        return All
            .Where(x => vaapi || x.Software)
            .Select(x => new HdrToneMapMethodOption { Id = x.Id, Label = x.Label, Hardware = !x.Software })
            .ToList();
    }

    /// <summary>
    /// Picks an effective method when tonemap is active. Unknown / unavailable requests
    /// fall back to VAAPI (if configured) else the admin/software default.
    /// </summary>
    public static string Resolve(string? requested, string hardwareAccel, string? adminDefault)
    {
        var available = AvailableFor(hardwareAccel);
        if (requested is not null && IsKnown(requested)
            && available.Any(a => a.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)))
        {
            return available.First(a => a.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)).Id;
        }

        if (hardwareAccel.Equals("vaapi", StringComparison.OrdinalIgnoreCase)
            && available.Any(a => a.Id == Vaapi))
            return Vaapi;

        var fallback = string.IsNullOrWhiteSpace(adminDefault) ? Hable : adminDefault.Trim();
        if (SoftwareIds.Contains(fallback)
            && available.Any(a => a.Id.Equals(fallback, StringComparison.OrdinalIgnoreCase)))
            return available.First(a => a.Id.Equals(fallback, StringComparison.OrdinalIgnoreCase)).Id;

        return available.FirstOrDefault(a => a.Id == Hable)?.Id
               ?? available.FirstOrDefault()?.Id
               ?? Hable;
    }

    /// <summary>Normalize a software tonemap algorithm name for ffmpeg <c>tonemap=</c>.</summary>
    public static string NormalizeSoftwareAlgorithm(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !SoftwareIds.Contains(id))
            return Hable;
        return id.ToLowerInvariant();
    }
}
