using System;
using System.Linq;
using Jellyfin.Plugin.JellyClean.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyClean.Tests;

public class QualityTests
{
    [Fact]
    public void Reads_resolution_codec_and_hdr()
    {
        Assert.Equal(new Quality("4K", "HEVC", "Dolby Vision"), Quality.Of(new MediaStream { Type = MediaStreamType.Video, Width = 3840, Height = 1600, Codec = "hevc", ColorSpace = "bt2020nc", ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", DvProfile = 8, DvBlSignalCompatibilityId = 1, RpuPresentFlag = 1, BlPresentFlag = 1 }));
        Assert.Equal(new Quality("1080p", "H.264", string.Empty), Quality.Of(new MediaStream { Type = MediaStreamType.Video, Width = 1920, Height = 800, Codec = "h264" }));
        Assert.Equal(new Quality("SD", "MPEG-2", string.Empty), Quality.Of(new MediaStream { Type = MediaStreamType.Video, Width = 720, Height = 576, Codec = "mpeg2video" }));
        Assert.Equal(Quality.None, Quality.Of(null));
    }

    [Fact]
    public void Series_show_their_most_common_quality()
    {
        var episodes = new[] { "1080p", "1080p", "720p" }.Select(r => new CatalogItem { Resolution = r, Codec = "HEVC" }).ToList();
        Assert.Equal(new Quality("1080p", "HEVC", string.Empty), Quality.MostCommon(episodes));
    }

    [Fact]
    public void Finds_copies_of_the_same_movie_across_libraries()
    {
        var hd = new CatalogItem { Type = "Movie", Library = "Movies" };
        var uhd = new CatalogItem { Type = "Movie", Library = "Movies 4K" };
        var other = new CatalogItem { Type = "Movie", Library = "Movies" };
        var series = new CatalogItem { Type = "Series", Library = "Shows" };

        Catalog.FindCopies([(hd, "tmdb348"), (uhd, "tmdb348"), (other, "tmdb1"), (series, "tmdb348"), (new CatalogItem { Type = "Movie" }, null)]);

        Assert.Equal(["Movies 4K"], hd.CopiesIn);
        Assert.Equal(["Movies"], uhd.CopiesIn);
        Assert.Empty(other.CopiesIn);
        Assert.Empty(series.CopiesIn);
    }

    [Fact]
    public void Filters_on_quality_and_copies()
    {
        var old = new CatalogItem { Name = "Old", Resolution = "720p", Codec = "H.264" };
        var sd = new CatalogItem { Name = "Sd", Resolution = "SD", Codec = "MPEG-2" };
        var uhd = new CatalogItem { Name = "Uhd", Resolution = "4K", Codec = "HEVC", Hdr = "HDR10", CopiesIn = ["Movies"] };
        CatalogItem[] all = [old, sd, uhd];

        string[] Names(Filter f) => f.Apply(all, 1).Select(i => i.Name).ToArray();

        Assert.Equal(["Old", "Sd"], Names(new Filter { Resolutions = ["720p", "SD"], Sort = "name" }));
        Assert.Equal(["Old"], Names(new Filter { Resolutions = ["720p", "SD"], Codec = "H.264" }));
        Assert.Equal(["Uhd"], Names(new Filter { Hdr = "hdr" }));
        Assert.Equal(["Old", "Sd"], Names(new Filter { Hdr = "sdr", Sort = "name" }));
        Assert.Equal(["Uhd"], Names(new Filter { CopiesOnly = true }));
        Assert.Equal(["Sd", "Old", "Uhd"], Names(new Filter { Sort = "quality" }));
    }
}
