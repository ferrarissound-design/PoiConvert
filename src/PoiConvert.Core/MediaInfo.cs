namespace PoiConvert.Core;

/// <summary>ffprobe から得たファイル情報。</summary>
public sealed class MediaInfo
{
    public double? DurationSeconds { get; init; }
    public long? SizeBytes { get; init; }
    public string? FormatName { get; init; }

    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;

    /// <summary>格納されている（回転前の）幅。</summary>
    public int? Width { get; init; }
    /// <summary>格納されている（回転前の）高さ。</summary>
    public int? Height { get; init; }
    /// <summary>回転角（度）。スマホ縦動画などで 90 / -90 / 270 になる。</summary>
    public int Rotation { get; init; }

    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }

    private bool IsSideways => Math.Abs(Rotation % 180) == 90;

    /// <summary>ffmpeg の自動回転後に見える幅。</summary>
    public int? DisplayWidth => IsSideways ? Height : Width;
    /// <summary>ffmpeg の自動回転後に見える高さ。</summary>
    public int? DisplayHeight => IsSideways ? Width : Height;
}
