using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// Video quality as people say it: 4K, HEVC, Dolby Vision. Same names and limits as JellyBadge.
/// </summary>
/// <param name="Resolution">4K, 1080p, 720p, SD, or empty when unknown.</param>
/// <param name="Codec">AV1, HEVC, VP9, H.264, VC-1, MPEG-2, MPEG-4, or empty.</param>
/// <param name="Hdr">Dolby Vision, HDR10+, HDR10, HLG, or empty for SDR.</param>
public record Quality(string Resolution, string Codec, string Hdr)
{
    /// <summary>Gets an unknown quality.</summary>
    public static Quality None { get; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Reads the quality of a video stream.
    /// </summary>
    /// <param name="video">The video stream, or null.</param>
    /// <returns>The quality.</returns>
    public static Quality Of(MediaStream? video)
    {
        if (video is null)
        {
            return None;
        }

        // Width first so scope films (1920x800) still count as 1080p, height for 4:3 content.
        int w = video.Width ?? 0, h = video.Height ?? 0;
        var resolution = w == 0 && h == 0 ? string.Empty
            : w >= 3200 || h >= 2000 ? "4K"
            : w >= 1700 || h >= 1000 ? "1080p"
            : w >= 1200 || h >= 700 ? "720p"
            : "SD";

        var codec = (video.Codec ?? string.Empty).ToLowerInvariant() switch
        {
            "av1" => "AV1",
            "hevc" or "h265" => "HEVC",
            "vp9" => "VP9",
            "h264" or "avc" => "H.264",
            "vc1" => "VC-1",
            "mpeg2video" => "MPEG-2",
            "mpeg4" or "msmpeg4v3" => "MPEG-4",
            var other => other.ToUpperInvariant(),
        };

        var hdr = video.VideoRangeType switch
        {
            VideoRangeType.DOVI or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithHLG or VideoRangeType.DOVIWithSDR
                or VideoRangeType.DOVIWithEL or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus => "Dolby Vision",
            VideoRangeType.HDR10Plus => "HDR10+",
            VideoRangeType.HDR10 => "HDR10",
            VideoRangeType.HLG => "HLG",

            // Broken Dolby Vision metadata: fall back to what the base layer signals.
            VideoRangeType.DOVIInvalid when string.Equals(video.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) => "HDR10",
            VideoRangeType.DOVIInvalid when string.Equals(video.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase) => "HLG",
            _ => string.Empty,
        };

        return new(resolution, codec, hdr);
    }

    /// <summary>
    /// The most common quality of a season's or series' episodes.
    /// </summary>
    /// <param name="episodes">The episodes.</param>
    /// <returns>The most common value of each part.</returns>
    public static Quality MostCommon(IReadOnlyCollection<CatalogItem> episodes)
    {
        static string Top(IEnumerable<string> values) => values.Where(v => v.Length > 0).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;
        return new(Top(episodes.Select(e => e.Resolution)), Top(episodes.Select(e => e.Codec)), Top(episodes.Select(e => e.Hdr)));
    }
}
