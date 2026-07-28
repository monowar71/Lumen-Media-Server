namespace LumenMedia.Application.Playback;

/// <summary>Shared parsing of device-profile resolution caps (e.g. <c>1080p</c> → 1080).</summary>
public static class ResolutionLimits
{
    public static int? ParseMaxHeight(string? maxResolution)
    {
        if (string.IsNullOrWhiteSpace(maxResolution))
            return null;
        var res = maxResolution.Trim().ToLowerInvariant();
        return res switch
        {
            "4k" or "uhd" or "2160p" => 2160,
            "1440p" or "qhd" => 1440,
            "1080p" or "fhd" => 1080,
            "720p" or "hd" => 720,
            "480p" or "sd" => 480,
            "360p" => 360,
            _ => int.TryParse(res.TrimEnd('p'), out var h) ? h : null,
        };
    }
}
