using System.Buffers.Binary;
using System.Text;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Archive;

public sealed class SonyRtmdMp4CompatibilityTests
{
    [Fact]
    public void OnlyKnownSonyRtmdAudioAndVideoLayoutsEnterTheCompatibilityPath()
    {
        var media = SonyMedia();
        Assert.True(SonyRtmdMp4Compatibility.HasRtmd(media));
        Assert.Null(SonyRtmdMp4Compatibility.LayoutProblem(media, "/camera/HH6547.MP4"));
        Assert.Equal("sony_rtmd_mp4_required", SonyRtmdMp4Compatibility.LayoutProblem(media, "/camera/HH6547.MOV"));
        foreach (var pixelFormat in new[] { "yuv420p", "yuv420p10le" })
        {
            var changedPixel = SonyMedia();
            changedPixel.PixelFormat = pixelFormat;
            changedPixel.ProbeJson = changedPixel.ProbeJson.Replace("yuv422p10le", pixelFormat, StringComparison.Ordinal);
            Assert.Null(SonyRtmdMp4Compatibility.LayoutProblem(changedPixel, "/camera/HH6547.MP4"));
        }
        var unsupportedPixel = SonyMedia();
        unsupportedPixel.PixelFormat = "yuv422p12le";
        Assert.Equal("sony_rtmd_pixel_format_unsupported", SonyRtmdMp4Compatibility.LayoutProblem(unsupportedPixel, "/camera/HH6547.MP4"));

        var extra = MediaWithExtraStream("""{"index":3,"codec_type":"attachment","codec_tag_string":"lens"}""");
        Assert.Equal("sony_rtmd_track_layout_unsupported", SonyRtmdMp4Compatibility.LayoutProblem(extra, "/camera/HH6547.MP4"));

        var changedAudio = SonyMedia().ProbeJson.Replace("pcm_s16be", "aac", StringComparison.Ordinal);
        Assert.Equal("sony_rtmd_audio_unsupported", SonyRtmdMp4Compatibility.LayoutProblem(MediaFromProbe(changedAudio), "/camera/HH6547.MP4"));

        var supportedAac = SonyMedia().ProbeJson
            .Replace("pcm_s16be", "aac", StringComparison.Ordinal)
            .Replace("\"twos\"", "\"mp4a\"", StringComparison.Ordinal)
            .Replace("\"channels\":2", "\"channels\":2,\"profile\":\"LC\",\"channel_layout\":\"stereo\"", StringComparison.Ordinal);
        Assert.Null(SonyRtmdMp4Compatibility.LayoutProblem(MediaFromProbe(supportedAac), "/camera/HH6547.MP4"));
        Assert.Equal("sony_rtmd_audio_unsupported", SonyRtmdMp4Compatibility.LayoutProblem(
            MediaFromProbe(supportedAac.Replace("48000", "44100", StringComparison.Ordinal)), "/camera/HH6547.MP4"));
    }

    [Fact]
    public void OnlyExactH264AacTmcdLayoutIsSupportedAndUnknownDataStaysRefused()
    {
        var probe = """
            {"streams":[
              {"index":0,"id":"0x1","codec_type":"video","codec_name":"h264","codec_tag_string":"avc1","pix_fmt":"yuv420p","tags":{"timecode":"00:22:53;13"}},
              {"index":1,"id":"0x2","codec_type":"audio","codec_name":"aac","codec_tag_string":"mp4a","profile":"LC","sample_rate":"48000","channels":2,"channel_layout":"stereo"},
              {"index":2,"id":"0x3","codec_type":"data","codec_tag_string":"tmcd","tags":{"timecode":"00:22:53;13"}}
            ]}
            """;
        var media = MediaFromProbe(probe);
        media.Codec = "h264";
        media.Profile = "High";
        media.PixelFormat = "yuv420p";

        Assert.True(SonyRtmdMp4Compatibility.HasTmcd(media));
        Assert.False(SonyRtmdMp4Compatibility.HasRtmd(media));
        Assert.True(SonyRtmdMp4Compatibility.RequiresTrackPreservation(media));
        Assert.Null(SonyRtmdMp4Compatibility.LayoutProblem(media, "/camera/program005.mp4"));
        media.Encoder = "hevc_videotoolbox";
        var args = new ArchiveMediaService().BuildArguments(
            new ArchiveEntry { InputPath = "/camera/program005.mp4", TargetMbps = 7.434 },
            media, "/work/video.mp4", sonyVideoOnly: true).Arguments;
        var writeTmcd = args.ToList().IndexOf("-write_tmcd");
        Assert.True(writeTmcd >= 0);
        Assert.Equal("0", args[writeTmcd + 1]);

        var unknownData = MediaFromProbe(probe.Replace("\"tmcd\"", "\"gpmd\"", StringComparison.Ordinal));
        Assert.Equal("data_track_unsupported",
            SonyRtmdMp4Compatibility.LayoutProblem(unknownData, "/camera/unknown-data.mp4"));
        var extraData = MediaFromProbe(probe.Replace("]}",
            ",{\"index\":3,\"id\":\"0x4\",\"codec_type\":\"data\",\"codec_tag_string\":\"gpmd\"}]}"
            , StringComparison.Ordinal));
        Assert.Equal("mp4_tmcd_track_layout_unsupported",
            SonyRtmdMp4Compatibility.LayoutProblem(extraData, "/camera/extra-data.mp4"));

        var path = Path.Combine(Path.GetTempPath(), "snacks-tmcd-ref-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            File.WriteAllBytes(path, BuildTimecodeReferenceMovie(3));
            Assert.Null(SonyRtmdMp4Compatibility.TimecodeReferenceProblem(path, media));
            File.WriteAllBytes(path, BuildTimecodeReferenceMovie(2));
            Assert.Equal("mp4_tmcd_reference_unsupported",
                SonyRtmdMp4Compatibility.TimecodeReferenceProblem(path, media));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Mp4BoxTrackIdsComeFromInfoLinesAndPreserveOrdinalSeparately()
    {
        var info = """
            # Movie Info - 3 tracks - TimeScale 60000
            # Track 1 Info - ID 41 - TimeScale 60000
            Media Type: vide:hvc1
            # Track 2 Info - ID 8 - TimeScale 48000
            Media Type: soun:twos
            # Track 3 Info - ID 17 - TimeScale 60000
            Media Type: meta:rtmd
            """;

        var tracks = SonyRtmdMp4Compatibility.ParseMp4BoxInfo(info);

        Assert.Collection(tracks,
            t => { Assert.Equal(1, t.Ordinal); Assert.Equal(41, t.Id); Assert.Equal("vide", t.Type); },
            t => { Assert.Equal(2, t.Ordinal); Assert.Equal(8, t.Id); Assert.Equal("soun", t.Type); Assert.Equal("twos", t.Tag); },
            t => { Assert.Equal(3, t.Ordinal); Assert.Equal(17, t.Id); Assert.Equal("meta", t.Type); Assert.Equal("rtmd", t.Tag); });
    }

    [Fact]
    public void RootMetaIsAcceptedOnlyWhenEveryItemResolvesInsideItsOwnIdat()
    {
        var meta = BuildRootMeta(constructionMethod: 1, dataReference: 0, extentOffset: 0, extentLength: 4);

        Assert.Contains(1, SonyRtmdMp4Compatibility.ValidateSelfContainedRootMeta(meta));
        Assert.Throws<ArchiveException>(() => SonyRtmdMp4Compatibility.ValidateSelfContainedRootMeta(
            BuildRootMeta(constructionMethod: 1, dataReference: 1, extentOffset: 0, extentLength: 4)));
        Assert.Throws<ArchiveException>(() => SonyRtmdMp4Compatibility.ValidateSelfContainedRootMeta(
            BuildRootMeta(constructionMethod: 0, dataReference: 0, extentOffset: 0, extentLength: 4)));
        Assert.Throws<ArchiveException>(() => SonyRtmdMp4Compatibility.ValidateSelfContainedRootMeta(
            BuildRootMeta(constructionMethod: 1, dataReference: 0, extentOffset: 2, extentLength: 4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RtmdReferenceRestorationUsesActualTrackIdsAndRetainsAllCdscTargets(bool multipleTargets)
    {
        var targets = multipleTargets ? new uint[] { 8, 41 } : new uint[] { 41 };
        WithReferenceMovie(targets, path =>
            Assert.Equal(targets.Select(id => (int)id), SonyRtmdMp4Compatibility.GetRtmdCdscTargetsToRestore(path, 17, 41)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RtmdWithoutVideoCdscDoesNotGainAnInventedReference(bool referencesOnlyAudio)
    {
        WithReferenceMovie(referencesOnlyAudio ? new uint[] { 8 } : [], path =>
            Assert.Empty(SonyRtmdMp4Compatibility.GetRtmdCdscTargetsToRestore(path, 17, 41)));
    }

    [Fact]
    public void RtmdReferenceRestorationRejectsDanglingTargetsInsteadOfDroppingThem()
    {
        WithReferenceMovie([41, 99], path =>
            Assert.Equal("sony_rtmd_reference_unsupported", Assert.Throws<ArchiveException>(() =>
                SonyRtmdMp4Compatibility.GetRtmdCdscTargetsToRestore(path, 17, 41)).Code));
    }

    private static void WithReferenceMovie(uint[] cdscTargets, Action<string> assert)
    {
        byte[] Header(uint id)
        {
            var payload = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), id);
            return Box("tkhd", payload);
        }
        byte[] Reference(string type, uint[] ids)
        {
            var payload = new List<byte>();
            foreach (var id in ids) Append32(payload, id);
            return Box(type, payload.ToArray());
        }
        // The unrelated sync reference must never be mistaken for cdsc.
        var groups = Reference("sync", [41]);
        if (cdscTargets.Length > 0) groups = groups.Concat(Reference("cdsc", cdscTargets)).ToArray();
        var bytes = Box("moov", Box("trak", Header(41)).Concat(Box("trak", Header(8)))
            .Concat(Box("trak", Header(17).Concat(Box("tref", groups)).ToArray())).ToArray());
        var path = Path.Combine(Path.GetTempPath(), "snacks-rtmd-ref-" + Guid.NewGuid().ToString("N") + ".mp4");
        try { File.WriteAllBytes(path, bytes); assert(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SourceRootMetaCanBeRejectedBeforeEncoding()
    {
        var path = Path.Combine(Path.GetTempPath(), "snacks-sony-meta-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            File.WriteAllBytes(path, BuildRootMeta(constructionMethod: 1, dataReference: 0, extentOffset: 0, extentLength: 4));
            Assert.Null(SonyRtmdMp4Compatibility.RootMetaProblem(path));

            File.WriteAllBytes(path, BuildRootMeta(constructionMethod: 0, dataReference: 0, extentOffset: 0, extentLength: 4));
            Assert.Equal("sony_root_meta_external_item", SonyRtmdMp4Compatibility.RootMetaProblem(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SonyVideoPassUsesTwoPass422TenBitAndDoesNotMapAudioOrPrivateDataIntoFfmpeg()
    {
        var media = SonyMedia();
        media.Encoder = "libx265";
        var entry = new ArchiveEntry { InputPath = "/camera/HH6547.MP4", TargetMbps = 20 };
        var args = new ArchiveMediaService().BuildArguments(entry, media, "/work/video.mp4",
            sonyVideoOnly: true, x265Pass: 1, x265StatsPath: "/work/x265-two-pass.stats").Arguments;

        Assert.Contains("0:v:0", args);
        Assert.Contains("-an", args);
        Assert.Contains("-dn", args);
        Assert.Contains("-profile:v", args);
        Assert.Contains("main422-10", args);
        Assert.Contains("yuv422p10le", args);
        Assert.Contains("20000000", args);
        Assert.Contains("40000000", args);
        Assert.Contains(args, value => value.StartsWith("pools=4:frame-threads=2:repeat-headers=0:pass=1:stats=", StringComparison.Ordinal));
        Assert.Single(args, a => a == "-map");
    }

    [Fact]
    public void VideoToolboxRoutingPreserves420FormatsAndOnlyUses422AfterThirtySeconds()
    {
        Assert.True(SonyRtmdMp4Compatibility.CanUseVideoToolbox("yuv420p", .5));
        Assert.True(SonyRtmdMp4Compatibility.CanUseVideoToolbox("yuv420p10le", .5));
        Assert.False(SonyRtmdMp4Compatibility.CanUseVideoToolbox("yuv422p10le", 29.999));
        Assert.True(SonyRtmdMp4Compatibility.CanUseVideoToolbox("yuv422p10le", 30));
        Assert.False(SonyRtmdMp4Compatibility.CanUseVideoToolbox("yuv444p10le", 60));

        var media = SonyMedia();
        media.Encoder = "hevc_videotoolbox";
        var entry = new ArchiveEntry { InputPath = "/camera/HH6455.MP4", TargetMbps = 20 };
        var args = new ArchiveMediaService().BuildArguments(entry, media, "/work/video.mp4", sonyVideoOnly: true).Arguments;
        Assert.Equal("p210le", args[args.ToList().IndexOf("-pix_fmt") + 1]);
        Assert.Contains(args, pair => pair == "main42210");
        var allowSoftwareIndex = args.ToList().IndexOf("-allow_sw");
        Assert.True(allowSoftwareIndex >= 0);
        Assert.Equal("0", args[allowSoftwareIndex + 1]);
        Assert.DoesNotContain(args, pair => pair.StartsWith("pass=", StringComparison.Ordinal));

        media.PixelFormat = "yuv420p10le";
        media.ProbeJson = media.ProbeJson.Replace("yuv422p10le", "yuv420p10le", StringComparison.Ordinal);
        args = new ArchiveMediaService().BuildArguments(entry, media, "/work/video420.mp4", sonyVideoOnly: true).Arguments;
        Assert.Equal("yuv420p10le", args[args.ToList().IndexOf("-pix_fmt") + 1]);
        Assert.Contains(args, pair => pair == "main10");
    }

    private static ArchiveMediaInfo SonyMedia()
        => MediaWithExtraStream(null);

    private static ArchiveMediaInfo MediaWithExtraStream(string? extraStream)
    {
        var streams = new List<string>
        {
            """{"index":0,"codec_type":"video","codec_name":"hevc","codec_tag_string":"hvc1","pix_fmt":"yuv422p10le","time_base":"1/60000"}""",
            """{"index":1,"codec_type":"audio","codec_name":"pcm_s16be","codec_tag_string":"twos","sample_rate":"48000","channels":2,"bits_per_sample":16}""",
            """{"index":2,"codec_type":"data","codec_tag_string":"rtmd"}""",
        };
        if (extraStream != null) streams.Add(extraStream);
        return MediaFromProbe("{\"streams\":[" + string.Join(',', streams) + "]}");
    }

    private static ArchiveMediaInfo MediaFromProbe(string probeJson)
        => new() { ProbeJson = probeJson, PixelFormat = "yuv422p10le", VideoIndex = 0, Encoder = "libx265" };

    private static byte[] BuildRootMeta(ushort constructionMethod, ushort dataReference, uint extentOffset, uint extentLength)
    {
        var handlerPayload = new byte[24];
        Encoding.ASCII.GetBytes("nrtm").CopyTo(handlerPayload, 8);
        var hdlr = Box("hdlr", handlerPayload);

        var infePayload = new List<byte> { 0, 0, 0, 0 };
        Append16(infePayload, 1);
        Append16(infePayload, 0);
        infePayload.AddRange(Encoding.ASCII.GetBytes("Lens profile\0application/octet-stream\0"));
        var infe = Box("infe", infePayload.ToArray());
        var iinfPayload = new List<byte> { 0, 0, 0, 0 };
        Append16(iinfPayload, 1);
        iinfPayload.AddRange(infe);
        var iinf = Box("iinf", iinfPayload.ToArray());

        var ilocPayload = new List<byte> { 1, 0, 0, 0, 0x44, 0 };
        Append16(ilocPayload, 1);
        Append16(ilocPayload, 1);
        Append16(ilocPayload, constructionMethod);
        Append16(ilocPayload, dataReference);
        Append16(ilocPayload, 1);
        Append32(ilocPayload, extentOffset);
        Append32(ilocPayload, extentLength);
        var iloc = Box("iloc", ilocPayload.ToArray());
        var idat = Box("idat", Encoding.ASCII.GetBytes("data"));

        var metaPayload = new List<byte> { 0, 0, 0, 0 };
        metaPayload.AddRange(hdlr);
        metaPayload.AddRange(iinf);
        metaPayload.AddRange(iloc);
        metaPayload.AddRange(idat);
        return Box("meta", metaPayload.ToArray());
    }

    private static byte[] BuildTimecodeReferenceMovie(uint timecodeTrackId)
    {
        byte[] TrackHeader(uint id)
        {
            var payload = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), id);
            return Box("tkhd", payload);
        }

        var referencePayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(referencePayload, timecodeTrackId);
        var videoTrack = Box("trak", TrackHeader(1).Concat(Box("tref", Box("tmcd", referencePayload))).ToArray());
        var timecodeTrack = Box("trak", TrackHeader(3));
        return Box("moov", videoTrack.Concat(timecodeTrack).ToArray());
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static void Append16(List<byte> target, ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        target.AddRange(bytes);
    }

    private static void Append32(List<byte> target, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        target.AddRange(bytes);
    }
}
