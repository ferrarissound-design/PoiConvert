namespace PoiConvert.Core;

public enum ConversionMode
{
    AudioExtract,
    Compress,
    Gif,
    Convert,
}

public enum AudioFormat
{
    Mp3,
    M4a,
    Wav,
    /// <summary>再エンコードなし（-c:a copy）。拡張子は元の音声コーデックから決める。</summary>
    Copy,
}

public enum QualityPreset
{
    /// <summary>CRF 20</summary>
    High,
    /// <summary>CRF 23</summary>
    Standard,
    /// <summary>CRF 28</summary>
    Light,
}

public enum ResolutionLimit
{
    Original,
    P1080,
    P720,
    P480,
}

public enum ContainerFormat
{
    Mp4,
    Mov,
    Mkv,
    Webm,
}

public sealed class AudioOptions
{
    public AudioFormat Format { get; set; } = AudioFormat.Mp3;
    public int BitrateKbps { get; set; } = 192;
}

public sealed class CompressOptions
{
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public ResolutionLimit Resolution { get; set; } = ResolutionLimit.Original;
    /// <summary>true のとき「目標サイズ」モード（2パス）。</summary>
    public bool UseTargetSize { get; set; }
    public double TargetSizeMB { get; set; } = 25;
}

public sealed class GifOptions
{
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; } = 5;
    public int Fps { get; set; } = 15;
    /// <summary>0 = 元のまま。</summary>
    public int Width { get; set; } = 480;
}

public sealed class ConvertOptions
{
    public ContainerFormat Container { get; set; } = ContainerFormat.Mp4;
    /// <summary>可能なら再エンコードせず -c copy する。</summary>
    public bool PreferStreamCopy { get; set; } = true;
}

public sealed class ConversionOptions
{
    public ConversionMode Mode { get; set; } = ConversionMode.Compress;
    public AudioOptions Audio { get; set; } = new();
    public CompressOptions Compress { get; set; } = new();
    public GifOptions Gif { get; set; } = new();
    public ConvertOptions Convert { get; set; } = new();
    /// <summary>libx264 の -preset。</summary>
    public string X264Preset { get; set; } = "veryfast";

    public static readonly string[] X264Presets =
    {
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow",
    };

    public static readonly int[] AudioBitrates = { 128, 192, 256, 320 };
    public static readonly int[] GifFpsChoices = { 10, 15, 20 };
    public static readonly int[] GifWidthChoices = { 320, 480, 640, 0 };

    /// <summary>GIF の長さがこれを超えたら警告する（秒）。</summary>
    public const double GifWarnDurationSeconds = 30;

    public ConversionOptions Clone()
    {
        return new ConversionOptions
        {
            Mode = Mode,
            X264Preset = X264Preset,
            Audio = new AudioOptions { Format = Audio.Format, BitrateKbps = Audio.BitrateKbps },
            Compress = new CompressOptions
            {
                Quality = Compress.Quality,
                Resolution = Compress.Resolution,
                UseTargetSize = Compress.UseTargetSize,
                TargetSizeMB = Compress.TargetSizeMB,
            },
            Gif = new GifOptions
            {
                StartSeconds = Gif.StartSeconds,
                DurationSeconds = Gif.DurationSeconds,
                Fps = Gif.Fps,
                Width = Gif.Width,
            },
            Convert = new ConvertOptions
            {
                Container = Convert.Container,
                PreferStreamCopy = Convert.PreferStreamCopy,
            },
        };
    }
}
