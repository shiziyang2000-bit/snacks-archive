using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
/// Narrow fail-closed adapter for the verified Sony rtmd and H.264/AAC/tmcd MP4 layouts.
/// The source is copied to task scratch before MP4Box edits it. Unknown track layouts and
/// external root-meta items are rejected rather than silently discarded.
/// </summary>
internal static class SonyRtmdMp4Compatibility
{
    private const string PrivateTag = "rtmd";
    private const string TimecodeTag = "tmcd";
    private const string RequiredMp4BoxVersion = "26.07";
    private const int MaximumRootMetaBytes = 64 * 1024 * 1024;

    internal sealed record Track(int Ordinal, int Id, string Type, string Tag);
    private sealed record Box(string Type, long Offset, long Size, int HeaderSize)
    {
        public long PayloadOffset => Offset + HeaderSize;
        public long End => Offset + Size;
    }

    private sealed record SourceRootMeta(Box? MetaBox, byte[] Bytes, HashSet<int> ItemIds);
    private sealed record TrackReference(string Type, int TrackId);

    internal static bool HasRtmd(ArchiveMediaInfo media)
    {
        return HasDataTrack(media, PrivateTag);
    }

    internal static bool HasTmcd(ArchiveMediaInfo media) => HasDataTrack(media, TimecodeTag);

    internal static bool RequiresTrackPreservation(ArchiveMediaInfo media)
        => HasRtmd(media) || HasTmcd(media);

    private static bool HasDataTrack(ArchiveMediaInfo media, string tag)
    {
        using var doc = JsonDocument.Parse(media.ProbeJson);
        return doc.RootElement.GetProperty("streams").EnumerateArray().Any(s =>
            ArchiveMediaService.Text(s, "codec_type") == "data" &&
            ArchiveMediaService.Text(s, "codec_tag_string").Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? LayoutProblem(ArchiveMediaInfo media, string path)
    {
        using var doc = JsonDocument.Parse(media.ProbeJson);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var hasRtmd = HasRtmd(media);
        var hasTmcd = HasTmcd(media);
        if (!hasRtmd && !hasTmcd)
            return Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                   streams.Any(s => ArchiveMediaService.Text(s, "codec_type") == "data")
                ? "data_track_unsupported"
                : null;
        if (hasRtmd && hasTmcd) return "sony_rtmd_track_layout_unsupported";
        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            return hasRtmd ? "sony_rtmd_mp4_required" : "mp4_tmcd_required";

        var video = streams.Where(s => ArchiveMediaService.Text(s, "codec_type") == "video" &&
            !(s.TryGetProperty("disposition", out var d) && ArchiveMediaService.Number(d, "attached_pic") == 1)).ToList();
        var audio = streams.Where(s => ArchiveMediaService.Text(s, "codec_type") == "audio").ToList();
        var dataTag = hasRtmd ? PrivateTag : TimecodeTag;
        var privateTracks = streams.Where(s => ArchiveMediaService.Text(s, "codec_type") == "data" &&
            ArchiveMediaService.Text(s, "codec_tag_string").Equals(dataTag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (video.Count != 1 || audio.Count != 1 || privateTracks.Count != 1 || streams.Count != 3)
            return hasRtmd ? "sony_rtmd_track_layout_unsupported" : "mp4_tmcd_track_layout_unsupported";

        if (hasRtmd)
        {
            if (!SupportsPixelFormat(media.PixelFormat)) return "sony_rtmd_pixel_format_unsupported";
            if (!SupportedAudioLayout(audio[0])) return "sony_rtmd_audio_unsupported";
            return null;
        }

        if (!ArchiveMediaService.Text(video[0], "codec_name").Equals("h264", StringComparison.OrdinalIgnoreCase) ||
            !ArchiveMediaService.Text(video[0], "codec_tag_string").Equals("avc1", StringComparison.OrdinalIgnoreCase) ||
            media.PixelFormat != "yuv420p")
            return "mp4_tmcd_video_unsupported";
        if (!IsAacLcStereo(audio[0])) return "mp4_tmcd_audio_unsupported";
        var videoTimecode = StreamTag(video[0], "timecode");
        var dataTimecode = StreamTag(privateTracks[0], "timecode");
        if (string.IsNullOrWhiteSpace(videoTimecode) || videoTimecode != dataTimecode)
            return "mp4_tmcd_timecode_unreadable";
        return null;
    }

    private static bool IsAacLcStereo(JsonElement audio)
        => ArchiveMediaService.Text(audio, "codec_name").Equals("aac", StringComparison.OrdinalIgnoreCase) &&
           ArchiveMediaService.Text(audio, "codec_tag_string").Equals("mp4a", StringComparison.OrdinalIgnoreCase) &&
           ArchiveMediaService.Text(audio, "profile").Equals("LC", StringComparison.OrdinalIgnoreCase) &&
           ArchiveMediaService.Text(audio, "channel_layout").Equals("stereo", StringComparison.OrdinalIgnoreCase) &&
           SupportedAudioLayout(audio);

    private static string StreamTag(JsonElement stream, string key)
        => stream.TryGetProperty("tags", out var tags) ? ArchiveMediaService.Text(tags, key) : "";

    private static bool SupportedAudioLayout(JsonElement audio)
    {
        var codec = ArchiveMediaService.Text(audio, "codec_name");
        var tag = ArchiveMediaService.Text(audio, "codec_tag_string");
        var sampleRate = ArchiveMediaService.Text(audio, "sample_rate");
        var channels = ArchiveMediaService.Number(audio, "channels");
        if (sampleRate != "48000" || channels != 2) return false;
        if (codec.Equals("pcm_s16be", StringComparison.OrdinalIgnoreCase) &&
            tag.Equals("twos", StringComparison.OrdinalIgnoreCase))
            return ArchiveMediaService.Number(audio, "bits_per_sample") == 16;
        return codec.Equals("aac", StringComparison.OrdinalIgnoreCase) &&
            tag.Equals("mp4a", StringComparison.OrdinalIgnoreCase) &&
            ArchiveMediaService.Text(audio, "profile").Equals("LC", StringComparison.OrdinalIgnoreCase) &&
            ArchiveMediaService.Text(audio, "channel_layout").Equals("stereo", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SupportsPixelFormat(string pixelFormat)
        => pixelFormat is "yuv420p" or "yuv420p10le" or "yuv422p10le";

    internal static bool CanUseVideoToolbox(string pixelFormat, double durationSeconds)
        => pixelFormat is "yuv420p" or "yuv420p10le" ||
            pixelFormat == "yuv422p10le" && double.IsFinite(durationSeconds) && durationSeconds >= 30;

    internal static string? RootMetaProblem(string path)
    {
        try
        {
            _ = ReadAndValidateSourceRootMeta(path);
            return null;
        }
        catch (ArchiveException ex)
        {
            return ex.Code;
        }
    }

    internal static string? RootMetaIfPresentProblem(string path)
    {
        try
        {
            _ = ReadOptionalSourceRootMeta(path);
            return null;
        }
        catch (ArchiveException ex)
        {
            return ex.Code;
        }
    }

    internal static string? TimecodeReferenceProblem(string path, ArchiveMediaInfo media)
    {
        try
        {
            using var doc = JsonDocument.Parse(media.ProbeJson);
            var streams = doc.RootElement.GetProperty("streams").EnumerateArray();
            var video = streams.Single(s => ArchiveMediaService.Text(s, "codec_type") == "video" &&
                !(s.TryGetProperty("disposition", out var d) && ArchiveMediaService.Number(d, "attached_pic") == 1));
            var timecode = streams.Single(s => ArchiveMediaService.Text(s, "codec_type") == "data" &&
                ArchiveMediaService.Text(s, "codec_tag_string").Equals(TimecodeTag, StringComparison.OrdinalIgnoreCase));
            if (!TryStreamTrackId(video, out var videoId) || !TryStreamTrackId(timecode, out var timecodeId) || videoId == timecodeId ||
                !HasExactTimecodeReference(path, videoId, timecodeId, allowMissing: false))
                return "mp4_tmcd_reference_unsupported";
            return null;
        }
        catch (ArchiveException ex)
        {
            return ex.Code;
        }
        catch (InvalidOperationException)
        {
            return "mp4_tmcd_reference_unsupported";
        }
    }

    internal static async Task<string?> Mp4BoxProblemAsync(string path, CancellationToken ct)
    {
        try
        {
            var version = await RunToolAsync(path, ["-version"], ct);
            return version.Contains(RequiredMp4BoxVersion, StringComparison.Ordinal)
                ? null
                : "mp4box_version_unsupported";
        }
        catch (OperationCanceledException) { throw; }
        catch { return "mp4box_unavailable"; }
    }

    internal static List<Track> ParseMp4BoxInfo(string text)
    {
        var clean = Regex.Replace(text, @"\x1b\[[0-9;]*m", "");
        var tracks = new List<Track>();
        (int Ordinal, int Id)? pending = null;
        foreach (var raw in clean.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var head = Regex.Match(line.Trim(), @"^#\s*Track\s+(\d+)\s+Info\s+-\s+ID\s+(\d+)(?:\s|$)");
            if (head.Success)
            {
                pending = (int.Parse(head.Groups[1].Value), int.Parse(head.Groups[2].Value));
                continue;
            }
            var media = Regex.Match(line, @"^\s*Media Type:\s*([A-Za-z0-9_]+):([A-Za-z0-9_.-]+)\s*$");
            if (!media.Success || pending == null) continue;
            tracks.Add(new Track(pending.Value.Ordinal, pending.Value.Id,
                media.Groups[1].Value.ToLowerInvariant(), media.Groups[2].Value.ToLowerInvariant()));
            pending = null;
        }
        return tracks;
    }

    internal static string ScratchPath(string outputPath, string entryId)
        => Path.Combine(Path.GetDirectoryName(outputPath)!, ".snacks-archive-" + entryId + ".sony");

    internal static async Task PreserveAsync(
        string mp4BoxPath,
        string ffmpegPath,
        string sourcePath,
        string encodedVideoPath,
        string outputPath,
        string scratchPath,
        Func<string, Task> log,
        CancellationToken ct)
    {
        if (File.Exists(scratchPath)) throw new ArchiveException("temporary_conflict", "Sony MP4 暂存路径被同名文件占用。", scratchPath);
        Directory.CreateDirectory(scratchPath);
        if ((File.GetAttributes(scratchPath)&FileAttributes.ReparsePoint)!=0)
            throw new ArchiveException("temporary_conflict", "Sony MP4 暂存目录是符号链接，拒绝使用。", scratchPath);

        var sourceCopy = Path.Combine(scratchPath, "source.mp4");
        var withoutVideo = Path.Combine(scratchPath, "source-no-video.mp4");
        await log("读取 MP4 原始轨道 ID、时间码引用与容器信息。");
        var sourceInfo = await RunToolAsync(mp4BoxPath, ["-info", sourcePath], ct);
        var sourceTracks = ParseMp4BoxInfo(sourceInfo);
        var sourceVideo = RequireOne(sourceTracks, t => t.Type == "vide", "sony_video_track_unreadable");
        var sourceAudio = RequireOne(sourceTracks, t => t.Type == "soun" && (t.Tag is "twos" or "mp4a"), "sony_audio_track_unreadable");
        var rtmdTracks = sourceTracks.Where(t => t.Type == "meta" && t.Tag == PrivateTag).ToList();
        var timecodeTracks = sourceTracks.Where(t => t.Type == TimecodeTag && t.Tag == TimecodeTag).ToList();
        if (sourceTracks.Count != 3 || sourceTracks.Select(t => t.Id).Distinct().Count() != sourceTracks.Count ||
            sourceTracks.Select(t => t.Ordinal).Distinct().Count() != sourceTracks.Count ||
            rtmdTracks.Count + timecodeTracks.Count != 1)
            throw new ArchiveException("sony_track_set_unsupported", "MP4 轨道 ID、类型或数量无法可靠识别。");
        var sourcePrivate = rtmdTracks.SingleOrDefault();
        var sourceTimecodeTrack = timecodeTracks.SingleOrDefault();
        var sourceRootMeta = sourcePrivate != null ? ReadAndValidateSourceRootMeta(sourcePath) : ReadOptionalSourceRootMeta(sourcePath);
        var sourceMeta = sourceRootMeta.Bytes;
        var metaItemIds = sourceRootMeta.ItemIds;

        var tkhd = ReadTrackHeaders(sourcePath);
        if (!tkhd.TryGetValue(sourceVideo.Id, out var sourceVideoFlags))
            throw new ArchiveException("sony_video_track_id_unverified", "MP4Box video track_ID 与源 tkhd 不匹配。");
        if (sourceTimecodeTrack != null)
            RequireVideoTimecodeReference(sourcePath, sourceVideo.Id, sourceTimecodeTrack.Id);
        var rtmdCdscTargets = sourcePrivate == null
            ? Array.Empty<int>()
            : GetRtmdCdscTargetsToRestore(sourcePath, sourcePrivate.Id, sourceVideo.Id);
        await log(sourceTimecodeTrack == null
            ? $"原轨道 video={sourceVideo.Id}/{sourceVideo.Ordinal}, audio={sourceAudio.Id}/{sourceAudio.Ordinal}/{sourceAudio.Tag}, rtmd={sourcePrivate!.Id}/{sourcePrivate.Ordinal}, tkhd flags={sourceVideoFlags}；root meta items={metaItemIds.Count}。"
            : $"原轨道 video={sourceVideo.Id}/{sourceVideo.Ordinal}, audio={sourceAudio.Id}/{sourceAudio.Ordinal}/{sourceAudio.Tag}, tmcd={sourceTimecodeTrack.Id}/{sourceTimecodeTrack.Ordinal}, 保留 video→tmcd 引用；root meta items={metaItemIds.Count}。");

        File.Copy(sourcePath, sourceCopy, overwrite: false);
        var encodedInfo = await RunToolAsync(mp4BoxPath, ["-info", encodedVideoPath], ct);
        var encodedTracks = ParseMp4BoxInfo(encodedInfo);
        var encodedVideo = RequireOne(encodedTracks, t => t.Type == "vide", "sony_encoded_video_unreadable");
        if (encodedTracks.Count != 1)
            throw new ArchiveException("sony_encoded_video_layout", "HEVC 视频中间文件包含预期之外的轨道。");
        if (!encodedVideo.Tag.Equals("hvc1", StringComparison.OrdinalIgnoreCase))
            throw new ArchiveException("sony_encoded_video_tag_unsupported", "HEVC 视频候选没有使用已验证的 hvc1 sample entry。");

        await log($"在任务副本上移除原视频轨 ID {sourceVideo.Id}；保留其余原始轨与容器信息。");
        await RunToolAsync(mp4BoxPath,
            ["-quiet", "-keep-utc", "-tmp", scratchPath, "-rem", sourceVideo.Id.ToString(), sourceCopy, "-out", withoutVideo], ct);
        EnsureOutput(withoutVideo, "sony_remove_video_failed");
        File.Delete(sourceCopy);

        await log($"导入 HEVC 视频候选并绑定源 video track_ID {sourceVideo.Id} / ordinal {sourceVideo.Ordinal}。");
        var videoSpec = $"{encodedVideoPath}#trackID={encodedVideo.Id}:ID={sourceVideo.Id}:tkidx={sourceVideo.Ordinal}:tkhd={sourceVideoFlags}";
        var addVideoArgs = new List<string> { "-quiet", "-keep-utc", "-tmp", scratchPath, "-add", videoSpec };
        if (sourcePrivate != null && rtmdCdscTargets.Length > 0)
        {
            // Removing video also removes incoming cdsc references. Rebuild this
            // source group, including its other targets, in the existing add-video
            // operation rather than rewriting the whole output a second time.
            addVideoArgs.AddRange(["-ref", $"{sourcePrivate.Id}:cdsc:0"]);
            foreach (var target in rtmdCdscTargets)
                addVideoArgs.AddRange(["-ref", $"{sourcePrivate.Id}:cdsc:{target}"]);
            await log($"按源 track_ID 恢复 rtmd {sourcePrivate.Id} 的 cdsc 引用：{string.Join(",", rtmdCdscTargets)}。");
        }
        addVideoArgs.AddRange([withoutVideo, "-out", outputPath]);
        await RunToolAsync(mp4BoxPath, addVideoArgs, ct);
        EnsureOutput(outputPath, "sony_add_video_failed");
        if (sourceTimecodeTrack != null)
        {
            var referencedOutput = Path.Combine(scratchPath, "output-with-tmcd-reference.mp4");
            await log($"按源 track_ID 恢复 video {sourceVideo.Id} → tmcd {sourceTimecodeTrack.Id} 引用。");
            await RunToolAsync(mp4BoxPath,
                ["-quiet", "-keep-utc", "-tmp", scratchPath, "-ref", $"{sourceVideo.Id}:tmcd:{sourceTimecodeTrack.Id}", outputPath, "-out", referencedOutput], ct);
            EnsureOutput(referencedOutput, "mp4_tmcd_reference_restore_failed");
            File.Delete(outputPath);
            File.Move(referencedOutput, outputPath);
        }
        File.Delete(withoutVideo);

        var boxesBeforeMetaRestore = ReadTopLevelBoxes(outputPath);
        if (sourceRootMeta.MetaBox is { } sourceMetaBox)
        {
            var targetMetaBox = RequireSingle(boxesBeforeMetaRestore, "meta", "sony_target_meta_unsupported");
            await RestoreSelfContainedRootMetaAsync(sourcePath, outputPath, sourceMeta,
                targetMetaBox, boxesBeforeMetaRestore, ct);
        }
        else if (boxesBeforeMetaRestore.Any(b => b.Type == "meta"))
        {
            throw new ArchiveException("sony_root_meta_unsupported", "源 MP4 没有文件级 meta，但候选意外新增了 root meta。");
        }

        await log("原音轨、数据轨与容器信息封装完成；不提取验证载荷、不比较内容。");
    }

    internal static HashSet<int> ValidateSelfContainedRootMeta(byte[] metaBox)
    {
        var root = ReadSingleMemoryBox(metaBox, 0, metaBox.Length);
        if (root.Type != "meta" || root.End != metaBox.Length || root.HeaderSize != 8)
            throw new ArchiveException("sony_root_meta_unsupported", "源文件级 meta box 格式无法安全保留。");
        var children = ParseMemoryBoxes(metaBox, checked((int)root.PayloadOffset + 4), checked((int)root.End));
        var iloc = RequireSingle(children, "iloc", "sony_root_meta_unsupported");
        var iinf = RequireSingle(children, "iinf", "sony_root_meta_unsupported");
        var idat = RequireSingle(children, "idat", "sony_root_meta_unsupported");
        var hdlr = RequireSingle(children, "hdlr", "sony_root_meta_unsupported");
        var handlerOffset = checked((int)hdlr.PayloadOffset + 8);
        if (hdlr.End - handlerOffset < 4 || Encoding.ASCII.GetString(metaBox, handlerOffset, 4) != "nrtm")
            throw new ArchiveException("sony_root_meta_unsupported", "源文件级 meta 不是已验证的 nrtm 容器。");
        var infoIds = ParseInfoItemIds(metaBox, iinf);
        var locations = ParseItemLocations(metaBox, iloc, idat);
        if (infoIds.Count == 0 || infoIds.Count != locations.Count || !infoIds.SetEquals(locations.Keys))
            throw new ArchiveException("sony_root_meta_external_item", "文件级 meta 的 item 与 iloc 条目不一致，拒绝搬移。");
        return infoIds;
    }

    private static SourceRootMeta ReadAndValidateSourceRootMeta(string path)
    {
        var rootMeta = ReadOptionalSourceRootMeta(path);
        if (rootMeta.MetaBox == null)
            throw new ArchiveException("sony_root_meta_unsupported", "Sony MP4 缺少必须保留的文件级 meta box。");
        return rootMeta;
    }

    private static SourceRootMeta ReadOptionalSourceRootMeta(string path)
    {
        var boxes = ReadTopLevelBoxes(path);
        var matches = boxes.Where(b => b.Type == "meta").ToList();
        if (matches.Count == 0)
            return new SourceRootMeta(null, Array.Empty<byte>(), []);
        if (matches.Count != 1)
            throw new ArchiveException("sony_root_meta_unsupported", "MP4 必须最多包含一个文件级 meta box。");
        var metaBox = matches[0];
        var bytes = ReadBox(path, metaBox);
        var itemIds = ValidateSelfContainedRootMeta(bytes);
        return new SourceRootMeta(metaBox, bytes, itemIds);
    }

    private static async Task RestoreSelfContainedRootMetaAsync(
        string sourcePath,
        string targetPath,
        byte[] sourceMeta,
        Box targetMetaBox,
        List<Box> boxesBefore,
        CancellationToken ct)
    {
        var replacement = Encoding.ASCII.GetBytes("free");
        await using (var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            stream.Position = targetMetaBox.Offset + 4;
            await stream.WriteAsync(replacement, ct);
            stream.Position = stream.Length;
            await stream.WriteAsync(sourceMeta, ct);
            await stream.FlushAsync(ct);
            stream.Flush(true);
        }

    }

    private static async Task<string> RunToolAsync(string binary, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = binary, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new IOException("无法启动 MP4Box。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        using var cancel = deadline.Token.Register(() => { try { process.Kill(true); } catch { } });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ArchiveException("mp4box_timeout", "MP4Box 处理超过两小时，拒绝替换。"); }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            var detail = (error + "\n" + output).Trim();
            throw new ArchiveException("mp4box_failed", detail.Length > 3000 ? detail[^3000..] : detail);
        }
        return output + "\n" + error;
    }

    private static List<Box> ReadTopLevelBoxes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var boxes = new List<Box>();
        long offset = 0;
        var header = new byte[16];
        while (offset < stream.Length)
        {
            if (stream.Length - offset < 8) throw new ArchiveException("sony_mp4_box_truncated", "MP4 顶层 box 头不完整。");
            stream.Position = offset;
            stream.ReadExactly(header.AsSpan(0, 8));
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                if (stream.Length - offset < 16) throw new ArchiveException("sony_mp4_box_truncated", "MP4 扩展 box 头不完整。");
                stream.ReadExactly(header.AsSpan(8, 8));
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                headerSize = 16;
            }
            else if (size == 0) size = (ulong)(stream.Length - offset);
            if (size < (ulong)headerSize || size > long.MaxValue || offset + (long)size > stream.Length)
                throw new ArchiveException("sony_mp4_box_invalid", "MP4 顶层 box 长度无效。");
            boxes.Add(new Box(type, offset, (long)size, headerSize));
            offset += (long)size;
        }
        return boxes;
    }

    private static byte[] ReadBox(string path, Box box)
    {
        if (box.Size > MaximumRootMetaBytes || box.Size > int.MaxValue)
            throw new ArchiveException("sony_mp4_box_too_large", "需要保留的 MP4 元数据 box 超出安全大小上限。");
        var data = new byte[(int)box.Size];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        stream.Position = box.Offset;
        stream.ReadExactly(data);
        return data;
    }

    private static Dictionary<int, int> ReadTrackHeaders(string path)
    {
        var top = ReadTopLevelBoxes(path);
        var moov = RequireSingle(top, "moov", "sony_moov_unreadable");
        var moovBytes = ReadBox(path, moov);
        var tracks = new Dictionary<int, int>();
        foreach (var trak in ParseMemoryBoxes(moovBytes, moov.HeaderSize, moovBytes.Length).Where(b => b.Type == "trak"))
        {
            var tkhd = ParseMemoryBoxes(moovBytes, checked((int)trak.PayloadOffset), checked((int)trak.End)).SingleOrDefault(b => b.Type == "tkhd");
            if (tkhd == null) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track header 缺失。");
            var payload = moovBytes.AsSpan(checked((int)tkhd.PayloadOffset), checked((int)(tkhd.Size - tkhd.HeaderSize)));
            if (payload.Length < 16) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track header 被截断。");
            var version = payload[0];
            var flags = (payload[1] << 16) | (payload[2] << 8) | payload[3];
            var idOffset = version == 1 ? 20 : version == 0 ? 12 : -1;
            if (idOffset < 0 || payload.Length < idOffset + 4) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track header 版本不受支持。");
            var id = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(idOffset, 4)));
            if (id <= 0 || !tracks.TryAdd(id, flags)) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track_ID 重复或无效。");
        }
        return tracks;
    }

    private static Dictionary<int, List<TrackReference>> ReadTrackReferences(string path)
    {
        var top = ReadTopLevelBoxes(path);
        var moov = RequireSingle(top, "moov", "sony_moov_unreadable");
        var moovBytes = ReadBox(path, moov);
        var tracks = new Dictionary<int, List<TrackReference>>();
        foreach (var trak in ParseMemoryBoxes(moovBytes, moov.HeaderSize, moovBytes.Length).Where(b => b.Type == "trak"))
        {
            var children = ParseMemoryBoxes(moovBytes, checked((int)trak.PayloadOffset), checked((int)trak.End));
            var tkhd = RequireSingle(children, "tkhd", "sony_tkhd_unreadable");
            var payload = moovBytes.AsSpan(checked((int)tkhd.PayloadOffset), checked((int)(tkhd.Size - tkhd.HeaderSize)));
            if (payload.Length < 16) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track header 被截断。");
            var version = payload[0];
            var idOffset = version == 1 ? 20 : version == 0 ? 12 : -1;
            if (idOffset < 0 || payload.Length < idOffset + 4) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track header 版本不受支持。");
            var id = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(idOffset, 4)));
            if (id <= 0) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track_ID 无效。");
            var references = new List<TrackReference>();
            foreach (var tref in children.Where(b => b.Type == "tref"))
            foreach (var reference in ParseMemoryBoxes(moovBytes, checked((int)tref.PayloadOffset), checked((int)tref.End)))
            {
                var bytes = checked((int)(reference.Size - reference.HeaderSize));
                if (bytes <= 0 || bytes % 4 != 0)
                    throw new ArchiveException("mp4_track_reference_unsupported", "MP4 track reference 的目标 ID 列表无效。");
                for (var offset = checked((int)reference.PayloadOffset); offset < reference.End; offset += 4)
                {
                    var targetId = checked((int)BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(offset, 4)));
                    if (targetId <= 0) throw new ArchiveException("mp4_track_reference_unsupported", "MP4 track reference 含无效 track_ID。");
                    references.Add(new TrackReference(reference.Type, targetId));
                }
            }
            if (!tracks.TryAdd(id, references)) throw new ArchiveException("sony_tkhd_unreadable", "MP4 track_ID 重复。");
        }
        return tracks;
    }

    private static bool TryStreamTrackId(JsonElement stream, out int trackId)
    {
        trackId = 0;
        var value = ArchiveMediaService.Text(stream, "id");
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out trackId) && trackId > 0;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out trackId) && trackId > 0;
    }

    private static bool HasExactTimecodeReference(string path, int videoTrackId, int timecodeTrackId, bool allowMissing)
    {
        var references = ReadTrackReferences(path);
        if (!references.TryGetValue(videoTrackId, out var videoReferences) || !references.ContainsKey(timecodeTrackId)) return false;
        if (allowMissing && videoReferences.Count == 0) return true;
        return videoReferences.Count == 1 && videoReferences[0].Type == TimecodeTag &&
            videoReferences[0].TrackId == timecodeTrackId;
    }

    internal static int[] GetRtmdCdscTargetsToRestore(string path, int rtmdTrackId, int videoTrackId)
    {
        // Read moov references only, as part of the existing container-preservation
        // operation. This does not read media samples or add content verification.
        var references = ReadTrackReferences(path);
        if (!references.TryGetValue(rtmdTrackId, out var rtmdReferences) || !references.ContainsKey(videoTrackId))
            throw new ArchiveException("sony_rtmd_reference_unsupported", "源 rtmd 或视频 track_ID 与 moov 不匹配。");
        var targets = rtmdReferences.Where(r => r.Type == "cdsc").Select(r => r.TrackId).ToArray();
        if (!targets.Contains(videoTrackId)) return [];
        if (targets.Any(id => !references.ContainsKey(id)))
            throw new ArchiveException("sony_rtmd_reference_unsupported", "源 rtmd cdsc 引用了不存在的轨道，拒绝改写引用。");
        return targets;
    }

    private static void RequireVideoTimecodeReference(string path, int videoTrackId, int timecodeTrackId)
    {
        if (!HasExactTimecodeReference(path, videoTrackId, timecodeTrackId, allowMissing: false))
            throw new ArchiveException("mp4_tmcd_reference_mismatch", $"video track {videoTrackId} 没有唯一指向 tmcd track {timecodeTrackId} 的引用。");
    }

    private static List<Box> ParseMemoryBoxes(byte[] bytes, int start, int end)
    {
        var boxes = new List<Box>();
        var offset = start;
        while (offset < end)
        {
            if (end - offset < 8) throw new ArchiveException("sony_meta_box_invalid", "MP4 meta 子 box 头不完整。");
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (end - offset < 16) throw new ArchiveException("sony_meta_box_invalid", "MP4 meta 扩展子 box 头不完整。");
                size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8));
                header = 16;
            }
            else if (size == 0) size = (ulong)(end - offset);
            if (size < (ulong)header || size > int.MaxValue || offset + (long)size > end)
                throw new ArchiveException("sony_meta_box_invalid", "MP4 meta 子 box 长度无效。");
            boxes.Add(new Box(type, offset, (long)size, header));
            offset += (int)size;
        }
        return boxes;
    }

    private static Box ReadSingleMemoryBox(byte[] bytes, int start, int end)
        => RequireSingle(ParseMemoryBoxes(bytes, start, end), Encoding.ASCII.GetString(bytes, start + 4, 4), "sony_meta_box_invalid");

    private static HashSet<int> ParseInfoItemIds(byte[] bytes, Box iinf)
    {
        var payloadStart = checked((int)iinf.PayloadOffset);
        var payloadEnd = checked((int)iinf.End);
        if (payloadEnd - payloadStart < 6) throw new ArchiveException("sony_root_meta_unsupported", "文件级 iinf box 被截断。");
        var version = bytes[payloadStart];
        var countOffset = payloadStart + 4;
        uint count;
        int entriesOffset;
        if (version == 0)
        {
            count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(countOffset, 2));
            entriesOffset = countOffset + 2;
        }
        else if (version == 1)
        {
            if (payloadEnd - countOffset < 4) throw new ArchiveException("sony_root_meta_unsupported", "文件级 iinf 条目数缺失。");
            count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(countOffset, 4));
            entriesOffset = countOffset + 4;
        }
        else throw new ArchiveException("sony_root_meta_unsupported", "文件级 iinf 版本不受支持。");

        var entries = ParseMemoryBoxes(bytes, entriesOffset, payloadEnd).Where(b => b.Type == "infe").ToList();
        if (entries.Count != count) throw new ArchiveException("sony_root_meta_unsupported", "文件级 iinf 条目数与 infe 不一致。");
        var ids = new HashSet<int>();
        foreach (var item in entries)
        {
            var p = checked((int)item.PayloadOffset);
            var end = checked((int)item.End);
            if (end - p < 8) throw new ArchiveException("sony_root_meta_unsupported", "文件级 infe 条目被截断。");
            var itemVersion = bytes[p];
            var idOffset = itemVersion is 0 or 1 or 2 ? p + 4 : itemVersion == 3 ? p + 4 : -1;
            var idWidth = itemVersion is 0 or 1 or 2 ? 2 : itemVersion == 3 ? 4 : 0;
            if (idOffset < 0 || end - idOffset < idWidth) throw new ArchiveException("sony_root_meta_unsupported", "文件级 infe 版本不受支持。");
            var id = idWidth == 2
                ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(idOffset, 2))
                : checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(idOffset, 4)));
            if (id <= 0 || !ids.Add(id)) throw new ArchiveException("sony_root_meta_unsupported", "文件级 infe item_ID 重复或无效。");
        }
        return ids;
    }

    private static Dictionary<int, List<(ulong Offset, ulong Length)>> ParseItemLocations(byte[] bytes, Box iloc, Box idat)
    {
        var p = checked((int)iloc.PayloadOffset);
        var end = checked((int)iloc.End);
        if (end - p < 8) throw new ArchiveException("sony_root_meta_unsupported", "文件级 iloc box 被截断。");
        var version = bytes[p];
        if (version is not (1 or 2)) throw new ArchiveException("sony_root_meta_external_item", "仅支持可验证的 iloc v1/v2 自包含资源。");
        var offsetSize = bytes[p + 4] >> 4;
        var lengthSize = bytes[p + 4] & 0x0f;
        var baseOffsetSize = bytes[p + 5] >> 4;
        var indexSize = bytes[p + 5] & 0x0f;
        if (offsetSize > 8 || lengthSize is 0 or > 8 || baseOffsetSize > 8 || indexSize > 8)
            throw new ArchiveException("sony_root_meta_unsupported", "文件级 iloc 字段宽度不受支持。");
        var cursor = p + 6;
        var countWidth = version == 2 ? 4 : 2;
        var itemCount = ReadVariable(bytes, ref cursor, countWidth, end);
        if (itemCount > 4096) throw new ArchiveException("sony_root_meta_unsupported", "文件级 meta item 数超出安全范围。");
        var locations = new Dictionary<int, List<(ulong Offset, ulong Length)>>();
        var idWidth = version == 2 ? 4 : 2;
        for (var i = 0UL; i < itemCount; i++)
        {
            var id = checked((int)ReadVariable(bytes, ref cursor, idWidth, end));
            var construction = ReadVariable(bytes, ref cursor, 2, end);
            if ((construction & 0xfff0) != 0 || (construction & 0x000f) != 1)
                throw new ArchiveException("sony_root_meta_external_item", "文件级 meta 包含非 idat 自包含 item。");
            var dataReference = ReadVariable(bytes, ref cursor, 2, end);
            if (dataReference != 0) throw new ArchiveException("sony_root_meta_external_item", "文件级 meta item 引用了外部数据。");
            var baseOffset = ReadVariable(bytes, ref cursor, baseOffsetSize, end);
            var extentCount = ReadVariable(bytes, ref cursor, 2, end);
            if (id <= 0 || extentCount is 0 or > 4096 || locations.ContainsKey(id))
                throw new ArchiveException("sony_root_meta_unsupported", "文件级 iloc item_ID 或 extent 数无效。");
            var extents = new List<(ulong Offset, ulong Length)>();
            for (var e = 0UL; e < extentCount; e++)
            {
                if (indexSize > 0) _ = ReadVariable(bytes, ref cursor, indexSize, end);
                var extentOffset = ReadVariable(bytes, ref cursor, offsetSize, end);
                var extentLength = ReadVariable(bytes, ref cursor, lengthSize, end);
                if (extentLength == 0 || baseOffset > ulong.MaxValue - extentOffset ||
                    baseOffset + extentOffset > (ulong)(idat.Size - idat.HeaderSize) ||
                    extentLength > (ulong)(idat.Size - idat.HeaderSize) - (baseOffset + extentOffset))
                    throw new ArchiveException("sony_root_meta_external_item", "文件级 meta item extent 不完全位于自带 idat 中。");
                extents.Add((baseOffset + extentOffset, extentLength));
            }
            locations.Add(id, extents);
        }
        if (cursor != end) throw new ArchiveException("sony_root_meta_unsupported", "文件级 iloc 有未识别尾部字段。");
        return locations;
    }

    private static ulong ReadVariable(byte[] bytes, ref int offset, int width, int end)
    {
        if (width < 0 || width > 8 || offset < 0 || end - offset < width)
            throw new ArchiveException("sony_root_meta_unsupported", "文件级 iloc 字段被截断。");
        ulong value = 0;
        for (var i = 0; i < width; i++) value = (value << 8) | bytes[offset++];
        return value;
    }

    private static Box RequireSingle(IEnumerable<Box> boxes, string type, string code)
    {
        var matches = boxes.Where(b => b.Type == type).ToList();
        if (matches.Count != 1) throw new ArchiveException(code, $"MP4 必须且只允许一个 {type} box。");
        return matches[0];
    }

    private static Track RequireOne(IEnumerable<Track> tracks, Func<Track, bool> predicate, string code)
    {
        var matches = tracks.Where(predicate).ToList();
        if (matches.Count != 1) throw new ArchiveException(code, "MP4Box 未能唯一识别所需轨道。");
        return matches[0];
    }

    private static void EnsureOutput(string path, string code)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new ArchiveException(code, "MP4Box/媒体工具未生成非空输出文件。");
    }
}
