using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoiConvert.Core;

/// <summary>%AppData%\PoiConvert\settings.json に保存する設定。</summary>
public sealed class AppSettings
{
    public ConversionOptions Options { get; set; } = new();
    /// <summary>true = 元ファイルと同じフォルダに出力。</summary>
    public bool OutputToSourceFolder { get; set; } = true;
    public string? OutputFolder { get; set; }
    /// <summary>ffmpeg.exe のパス、またはそれが入ったフォルダ。</summary>
    public string? FfmpegPath { get; set; }
    /// <summary>同時実行数（1〜2）。</summary>
    public int Concurrency { get; set; } = 1;

    public void Normalize()
    {
        Options ??= new ConversionOptions();
        Options.Audio ??= new AudioOptions();
        Options.Compress ??= new CompressOptions();
        Options.Gif ??= new GifOptions();
        Options.Convert ??= new ConvertOptions();
        Concurrency = Math.Clamp(Concurrency, 1, 2);
        if (Array.IndexOf(ConversionOptions.X264Presets, Options.X264Preset) < 0)
            Options.X264Preset = "veryfast";
        if (Array.IndexOf(ConversionOptions.AudioBitrates, Options.Audio.BitrateKbps) < 0)
            Options.Audio.BitrateKbps = 192;
        if (Array.IndexOf(ConversionOptions.GifFpsChoices, Options.Gif.Fps) < 0)
            Options.Gif.Fps = 15;
        if (Array.IndexOf(ConversionOptions.GifWidthChoices, Options.Gif.Width) < 0)
            Options.Gif.Width = 480;
        if (!(Options.Compress.TargetSizeMB > 0))
            Options.Compress.TargetSizeMB = 25;
        if (!(Options.Gif.DurationSeconds > 0))
            Options.Gif.DurationSeconds = 5;
        if (!(Options.Gif.StartSeconds >= 0))
            Options.Gif.StartSeconds = 0;
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string FilePath { get; }

    public SettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoiConvert", "settings.json");

    /// <summary>読み込む。ファイルがない・壊れている場合は既定値を返す（例外は投げない）。</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s is not null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception)
        {
            // 壊れた設定ファイルは無視して既定値で起動する
        }
        var d = new AppSettings();
        d.Normalize();
        return d;
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        // 書きかけで壊れないよう一時ファイル経由で置き換える
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
