using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Archive;

public sealed class ArchiveMediaServiceArgumentTests
{
    [Fact]
    public void M4vUsesMp4MuxerWithoutChangingOtherMp4ArgumentsOrOutputSuffix()
    {
        var media = new ArchiveMediaInfo
        {
            Encoder = "libx265",
            PixelFormat = "yuv420p",
            VideoIndex = 0,
            ProbeJson = """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","codec_tag_string":"avc1","pix_fmt":"yuv420p","time_base":"1/60000"}]}"""
        };
        var entry = new ArchiveEntry { InputPath = "/source/input.mp4", TargetMbps = 3.4230186 };
        var service = new ArchiveMediaService();

        var mp4 = service.BuildArguments(entry, media, "/work/output.mp4").Arguments.ToList();
        var m4v = service.BuildArguments(entry, media, "/work/output.m4v").Arguments.ToList();

        Assert.DoesNotContain("-f", mp4);
        Assert.Contains("-f", m4v);
        Assert.Equal("mp4", m4v[m4v.IndexOf("-f") + 1]);
        Assert.Contains("hvc1", m4v);
        Assert.EndsWith(".m4v", m4v[^1], StringComparison.OrdinalIgnoreCase);
        var m4vWithoutExplicitMuxer = m4v.Where((_, index) => index != m4v.IndexOf("-f") && index != m4v.IndexOf("-f") + 1).ToList();
        m4vWithoutExplicitMuxer[^1] = "/work/output.mp4";
        Assert.Equal(mp4, m4vWithoutExplicitMuxer);
    }
}
