namespace PoiConvert.Core;

/// <summary>
/// コーデックとコンテナの相性表（再エンコードなしで入るかどうか）。
/// 完全な表ではなく「一般的なプレイヤーで再生できる組み合わせ」に絞っている。
/// </summary>
public static class ContainerSupport
{
    private static readonly Dictionary<ContainerFormat, (HashSet<string> Video, HashSet<string> Audio)?> Table = new()
    {
        [ContainerFormat.Mp4] = (
            Set("h264", "hevc", "mpeg4", "av1", "vp9", "mpeg2video", "mpeg1video"),
            Set("aac", "mp3", "ac3", "eac3", "alac", "opus", "flac", "mp2")),
        [ContainerFormat.Mov] = (
            Set("h264", "hevc", "mpeg4", "prores", "mjpeg", "mpeg2video", "dnxhd"),
            Set("aac", "mp3", "alac", "ac3", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_f32le")),
        // Matroska はほぼ何でも入る
        [ContainerFormat.Mkv] = null,
        [ContainerFormat.Webm] = (
            Set("vp8", "vp9", "av1"),
            Set("opus", "vorbis")),
    };

    private static HashSet<string> Set(params string[] s) => new(s, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 再エンコードなしで入れられるか。入らない場合は理由（日本語）を返す。
    /// </summary>
    public static bool CanStreamCopy(ContainerFormat container, MediaInfo media, out string? reason)
    {
        reason = null;
        var entry = Table[container];
        if (entry is null)
            return true;

        var name = GetExtension(container).TrimStart('.').ToUpperInvariant();
        if (media.VideoCodec is { } v && !entry.Value.Video.Contains(v))
        {
            reason = $"映像コーデック「{v}」は {name} に再エンコードなしでは入りません";
            return false;
        }
        if (media.AudioCodec is { } a && !entry.Value.Audio.Contains(a))
        {
            reason = $"音声コーデック「{a}」は {name} に再エンコードなしでは入りません";
            return false;
        }
        return true;
    }

    public static string GetExtension(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => ".mp4",
        ContainerFormat.Mov => ".mov",
        ContainerFormat.Mkv => ".mkv",
        ContainerFormat.Webm => ".webm",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    /// <summary>
    /// 音声を -c:a copy で取り出すときの拡張子。
    /// 定番の拡張子がないコーデックは何でも入る .mka（Matroska 音声）にする。
    /// </summary>
    public static string GetAudioCopyExtension(string audioCodec, out bool isFallback)
    {
        isFallback = false;
        var codec = audioCodec.ToLowerInvariant();
        if (codec.StartsWith("pcm_"))
            return codec.EndsWith("be") ? ".aiff" : ".wav"; // WAV はリトルエンディアンのみ
        switch (codec)
        {
            case "aac":
            case "alac":
                return ".m4a";
            case "mp3":
                return ".mp3";
            case "opus":
                return ".opus";
            case "vorbis":
                return ".ogg";
            case "flac":
                return ".flac";
            case "ac3":
                return ".ac3";
            case "eac3":
                return ".eac3";
            default:
                isFallback = true;
                return ".mka";
        }
    }
}
