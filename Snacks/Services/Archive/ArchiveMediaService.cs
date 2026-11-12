using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>Probe, preservation policy, and literal FFmpeg arguments for the existing transcoder.</summary>
public sealed class ArchiveMediaService
{
    public string FfmpegPath => Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
    public string FfprobePath => Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";
    public string Mp4BoxPath => Environment.GetEnvironmentVariable("MP4BOX_PATH") ?? "MP4Box";
    private readonly SemaphoreSlim _probeGate = new(2, 2);
    private string? _encoders;
    public static double Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    public static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
    public static double Rational(string s)
    {
        var p = s.Split('/');
        return double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && (p.Length == 1 || double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            ? p.Length == 1 ? a : double.Parse(p[1], CultureInfo.InvariantCulture) is var b && b != 0 ? a / b : 0 : 0;
    }
    public static string N(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);
    public async Task<ArchiveMediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        await _probeGate.WaitAsync(ct);
        try
        {
            var json = await RunAsync(FfprobePath, ["-v", "error", "-probesize", "1048576", "-analyzeduration", "1000000", "-show_streams", "-show_format", "-show_chapters", "-of", "json", path], ct);
            using var doc = JsonDocument.Parse(json);
            var videos = doc.RootElement.GetProperty("streams").EnumerateArray().Where(s => Text(s, "codec_type") == "video" && !(s.TryGetProperty("disposition", out var d) && Number(d, "attached_pic") == 1)).ToList();
            if (videos.Count != 1) throw new ArchiveException("video_layout_unsupported", "需要且仅支持一条主视频流；多视角或无视频素材未配置归档规则。");
            var v = videos[0];
            var info = new ArchiveMediaInfo
            {
                Codec = Text(v, "codec_name"), Profile = Text(v, "profile"), PixelFormat = Text(v, "pix_fmt"),
                VideoIndex = (int)Number(v, "index"), Width = (int)Number(v, "width"), Height = (int)Number(v, "height"),
                Duration = Number(v, "duration"), Fps = Rational(Text(v, "avg_frame_rate")), Bytes = new FileInfo(path).Length,
                ProbeJson = json,
            };
            info.Alpha = info.PixelFormat.StartsWith("yuva") || info.PixelFormat.StartsWith("gbrap") || new[] { "rgba", "bgra", "argb", "abgr", "ya8", "ya16be", "ya16le", "rgba64le", "rgba64be" }.Contains(info.PixelFormat)
                || (v.TryGetProperty("tags", out var tags) && Text(tags, "alpha_mode") == "1");
            if (info.Alpha) return info; // protection takes priority and needs no packet scan
            // Read container/stream headers only. No packet walk, full decode or content hash.
            var format = doc.RootElement.TryGetProperty("format", out var f) ? f : default;
            if (info.Duration <= 0 && format.ValueKind == JsonValueKind.Object) info.Duration = Number(format, "duration");
            var bitsPerSecond = Number(v, "bit_rate");
            if (bitsPerSecond <= 0 && v.TryGetProperty("tags", out var streamTags))
                bitsPerSecond = Math.Max(Number(streamTags, "BPS"), Number(streamTags, "BPS-eng"));
            if (bitsPerSecond <= 0 && info.Duration > 0)
            {
                var otherBitrates = doc.RootElement.GetProperty("streams").EnumerateArray()
                    .Where(stream => Number(stream, "index") != info.VideoIndex).Sum(stream => Number(stream, "bit_rate"));
                var containerRate = format.ValueKind == JsonValueKind.Object ? Number(format, "bit_rate") : 0;
                if (containerRate <= 0) containerRate = info.Bytes * 8.0 / info.Duration;
                bitsPerSecond = Math.Max(0, containerRate - otherBitrates);
                info.VideoBitrateEstimated = true;
            }
            info.VideoMbps = bitsPerSecond / 1_000_000;
            if (info.Fps <= 0) info.Fps = Rational(Text(v, "r_frame_rate"));
            return info;
        }
        finally { _probeGate.Release(); }
    }
    public async Task<string?> UnsupportedReasonAsync(ArchiveMediaInfo m, string path, CancellationToken ct)
    {
        if (m.Alpha) return "alpha_channel";
        if (m.Width <= 0 || m.Height <= 0 || m.Fps <= 0) return "video_specs_unmeasurable";
        if (!new[] { ".mkv", ".mov", ".mp4", ".m4v", ".ts", ".m2ts", ".mts" }.Contains(Path.GetExtension(path).ToLowerInvariant())) return "container_unsupported";
        if (!new[] { "yuv420p", "yuv420p10le", "yuv420p12le", "yuv422p", "yuv422p10le", "yuv422p12le", "yuv444p", "yuv444p10le", "yuv444p12le", "gray", "gray10le", "gray12le" }.Contains(m.PixelFormat)) return "pixel_format_unsupported";
        var mp4TrackProblem = SonyRtmdMp4Compatibility.LayoutProblem(m, path);
        if (mp4TrackProblem != null) return mp4TrackProblem;
        var hasRtmd = SonyRtmdMp4Compatibility.HasRtmd(m);
        var hasTmcd = SonyRtmdMp4Compatibility.HasTmcd(m);
        if (hasRtmd)
        {
            var rootMetaProblem = SonyRtmdMp4Compatibility.RootMetaProblem(path);
            if (rootMetaProblem != null) return rootMetaProblem;
        }
        else if (hasTmcd)
        {
            var rootMetaProblem = SonyRtmdMp4Compatibility.RootMetaIfPresentProblem(path);
            if (rootMetaProblem != null) return rootMetaProblem;
            var referenceProblem = SonyRtmdMp4Compatibility.TimecodeReferenceProblem(path, m);
            if (referenceProblem != null) return referenceProblem;
        }
        if (hasRtmd || hasTmcd)
        {
            var toolProblem = await SonyRtmdMp4Compatibility.Mp4BoxProblemAsync(Mp4BoxPath, ct);
            if (toolProblem != null) return toolProblem;
        }
        using var doc = JsonDocument.Parse(m.ProbeJson);
        var video = doc.RootElement.GetProperty("streams").EnumerateArray().First(s => Number(s, "index") == m.VideoIndex);
        var side = video.TryGetProperty("side_data_list", out var sd) ? sd.ToString() : "";
        if (Text(video,"field_order") is "tt" or "bb" or "tb" or "bt") return "interlaced_not_supported";
        if (side.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || side.Contains("HDR Dynamic", StringComparison.OrdinalIgnoreCase)) return "dynamic_hdr_unsupported";
        _encoders ??= await RunAsync(FfmpegPath, ["-hide_banner", "-encoders"], ct);
        var preservesSourceTrackLayout = SonyRtmdMp4Compatibility.RequiresTrackPreservation(m);
        var videoToolboxPixelFormatEligible = preservesSourceTrackLayout
            ? SonyRtmdMp4Compatibility.CanUseVideoToolbox(m.PixelFormat, m.Duration)
            : m.PixelFormat is "yuv420p" or "yuv420p10le";
        var hardware = OperatingSystem.IsMacOS() && videoToolboxPixelFormatEligible && !side.Contains("Mastering display", StringComparison.OrdinalIgnoreCase) && Text(video,"color_transfer") is not ("smpte2084" or "arib-std-b67") && _encoders.Contains("hevc_videotoolbox");
        m.Encoder = hardware ? "hevc_videotoolbox" : "libx265";
        if (!_encoders.Contains(m.Encoder)) return "encoder_unavailable";
        return null;
    }
    public static (double Target, string? Skip) CalculateTarget(string codec, string profile, double source, double ceiling)
    {
        if (!double.IsFinite(source) || source <= 0) return (0, "video_bitrate_unmeasurable");
        double d;
        switch (codec.ToLowerInvariant())
        {
            case "hevc": if (source < 1.25 * ceiling) return (0, "hevc_below_threshold"); d = ceiling; break;
            case "h264": d = Math.Min(source / 2, ceiling); break;
            case "mpeg2video": d = Math.Min(source / 4, ceiling); break;
            case "prores": d = Math.Min(source / 34, ceiling); break;
            case "dnxhd" when profile.Contains("DNXHR", StringComparison.OrdinalIgnoreCase): d = Math.Min(source / 33, ceiling); break;
            default: return (0, "codec_unconfigured");
        }
        return d >= source ? (d, "no_bitrate_reduction") : (d, null);
    }
    public FfmpegArgumentList BuildArguments(ArchiveEntry entry, ArchiveMediaInfo m, string output,
        bool sonyVideoOnly = false, int? x265Pass = null, string? x265StatsPath = null)
    {
        var sonyVideoToolbox = sonyVideoOnly && m.Encoder == "hevc_videotoolbox";
        if (sonyVideoOnly && (!SonyRtmdMp4Compatibility.SupportsPixelFormat(m.PixelFormat) ||
            m.Encoder is not ("libx265" or "hevc_videotoolbox") ||
            (m.Encoder == "libx265" && (x265Pass is not (1 or 2) || string.IsNullOrWhiteSpace(x265StatsPath))) ||
            (sonyVideoToolbox && (x265Pass != null || !string.IsNullOrWhiteSpace(x265StatsPath)))))
            throw new ArchiveException("sony_rtmd_encoder_unsupported", "Sony rtmd 兼容路径仅支持原采样格式、VideoToolbox 或两遍软件 HEVC。");
        using var doc = JsonDocument.Parse(m.ProbeJson);
        var v = doc.RootElement.GetProperty("streams").EnumerateArray().First(s => Number(s, "index") == m.VideoIndex);
        var outputPixelFormat = sonyVideoToolbox && m.PixelFormat == "yuv422p10le" ? "p210le" : m.PixelFormat;
        var a = new FfmpegArgumentList().Add("-hide_banner").Add("-nostdin").Add("-n").Add("-noautorotate").Add("-i", entry.InputPath)
            .Add("-map", sonyVideoOnly ? "0:v:0" : "0");
        if (sonyVideoOnly) a.Add("-an").Add("-sn").Add("-dn").Add("-map_metadata", "0");
        else a.Add("-map_metadata", "0").Add("-map_chapters", "0").Add("-c", "copy");
        a.Add(sonyVideoOnly ? "-c:v" : $"-c:{m.VideoIndex}", m.Encoder)
            .Add(sonyVideoOnly ? "-pix_fmt" : $"-pix_fmt:{m.VideoIndex}", sonyVideoOnly ? outputPixelFormat : m.PixelFormat)
            .Add("-fps_mode:v:0", "passthrough").Add(sonyVideoOnly ? "-b:v" : "-b:v:0", N(entry.TargetMbps * .98 * 1_000_000));
        if (sonyVideoOnly)
        {
            var profile = m.PixelFormat switch
            {
                "yuv420p" => "main",
                "yuv420p10le" => "main10",
                "yuv422p10le" => sonyVideoToolbox ? "main42210" : "main422-10",
                _ => throw new ArchiveException("sony_rtmd_pixel_format_unsupported", "Sony 视频原采样格式不受支持。")
            };
            a.Add("-profile:v", profile);
            if (sonyVideoToolbox) a.Add("-allow_sw", "0");
            else a.Add("-maxrate", N(entry.TargetMbps * 1_000_000)).Add("-bufsize", N(entry.TargetMbps * 2_000_000));
        }
        foreach (var (key, flag) in new[] { ("color_range", "-color_range:v:0"), ("color_space", "-colorspace:v:0"), ("color_transfer", "-color_trc:v:0"), ("color_primaries", "-color_primaries:v:0"), ("chroma_location", "-chroma_sample_location:v:0") })
            if (Text(v, key) is { Length: > 0 } value && value != "unknown" && value != "unspecified") a.Add(flag, value);
        if (m.Encoder == "libx265")
        {
            a.Add("-preset", "fast");
            var parameters = sonyVideoOnly
                ? new List<string> { "pools=4", "frame-threads=2", "repeat-headers=0", $"pass={x265Pass}", $"stats={x265StatsPath}" }
                : new List<string> { "pools=1", "frame-threads=1", "repeat-headers=1" };
            if (v.TryGetProperty("side_data_list", out var side))
            foreach (var item in side.EnumerateArray())
            {
                if (Text(item,"side_data_type").Contains("Mastering display"))
                {
                    long C(string name) => (long)Math.Round(Rational(Text(item, name)) * 50000);
                    long L(string name) => (long)Math.Round(Rational(Text(item, name)) * 10000);
                    parameters.Add($"master-display=G({C("green_x")},{C("green_y")})B({C("blue_x")},{C("blue_y")})R({C("red_x")},{C("red_y")})WP({C("white_point_x")},{C("white_point_y")})L({L("max_luminance")},{L("min_luminance")})");
                }
                if (Text(item,"side_data_type").Contains("Content light")) parameters.Add($"max-cll={Text(item,"max_content")},{Text(item,"max_average")}");
            }
            a.Add("-x265-params", string.Join(':', parameters));
        }
        else if (!sonyVideoToolbox) a.Add("-allow_sw", "0");
        if (Path.GetExtension(output).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v")
        {
            a.Add(sonyVideoOnly ? "-tag:v" : "-tag:v:0", "hvc1")
                .Add("-movflags", sonyVideoOnly ? "+write_colr" : "+use_metadata_tags");
            if (sonyVideoOnly)
            {
                a.Add("-write_tmcd", "0");
                if (v.TryGetProperty("time_base", out var timeBase) && TryVideoTrackTimescale(timeBase.GetString() ?? "") is { } timescale)
                    a.Add("-video_track_timescale", timescale);
            }
            if (Path.GetExtension(output).Equals(".m4v", StringComparison.OrdinalIgnoreCase))
                a.Add("-f", "mp4");
        }
        return a.Add(output);
    }
    private static string? TryVideoTrackTimescale(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && parts[0] == "1" && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var scale) && scale > 0
            ? scale.ToString(CultureInfo.InvariantCulture)
            : null;
    }
    public static ProcessStartInfo StartInfo(string binary, IEnumerable<string> args)
    {
        var p = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) p.ArgumentList.Add(arg); return p;
    }
    public static async Task<string> RunAsync(string binary, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = Process.Start(StartInfo(binary,args)) ?? throw new IOException("无法启动媒体工具");
        using var reg = ct.Register(() => { try { process.Kill(true); } catch { } });
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct); var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new ArchiveException("media_tool_failed", error.Length > 4000 ? error[^4000..] : error);
        return output.Length > 0 ? output : error;
    }
}
