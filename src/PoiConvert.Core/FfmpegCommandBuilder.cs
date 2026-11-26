using System.Globalization;

namespace PoiConvert.Core;

/// <summary>ffmpeg を1回起動する単位。</summary>
public sealed class FfmpegStep
{
    public required string Description { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    /// <summary>全体の進捗に占める割合（全ステップ合計で 1）。</summary>
    public required double Weight { get; init; }
    /// <summary>このステップで出力される長さ（秒）。進捗%の計算に使う。</summary>
    public double? ExpectedDurationSeconds { get; init; }
}

/// <summary>1ファイル分の変換手順。</summary>
public sealed class ConversionPlan
{
    public required string OutputPath { get; init; }
    public required IReadOnlyList<FfmpegStep> Steps { get; init; }
    /// <summary>終了後に必ず消す一時ファイル。</summary>
    public IReadOnlyList<string> TempFiles { get; init; } = Array.Empty<string>();
    /// <summary>2パス用ログのプレフィックス（prefix* を後で消す）。</summary>
    public string? PassLogPrefix { get; init; }
    /// <summary>ログ欄に出す補足メッセージ。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 変換モードとオプションから ffmpeg の引数を組み立てる。UI にもプロセスにも依存しない純粋なロジック。
/// 引数は 1要素 = 1引数 のリストで返すので、ProcessStartInfo.ArgumentList にそのまま渡せる
/// （パスに空白・日本語・記号があってもクォートの心配がない）。
/// </summary>
public static class FfmpegCommandBuilder
{
    /// <summary>全コマンド共通の先頭引数。進捗は標準出力に key=value で出させる。</summary>
    public static readonly IReadOnlyList<string> CommonPrefix = new[]
    {
        "-hide_banner",
        "-nostdin",
        "-loglevel", "warning",
        "-nostats",
        "-progress", "pipe:1",
    };

    public const int CompressAudioKbps = 128;
    public const int ConvertAudioKbps = 192;
    public const int MinTargetVideoKbps = 50;
    /// <summary>コンテナのオーバーヘッド分として目標サイズから差し引く割合。</summary>
    public const double TargetSizeOverheadFactor = 0.97;

    public static int GetCrf(QualityPreset q) => q switch
    {
        QualityPreset.High => 20,
        QualityPreset.Standard => 23,
        QualityPreset.Light => 28,
        _ => 23,
    };

    public static int? GetResolutionTarget(ResolutionLimit r) => r switch
    {
        ResolutionLimit.P1080 => 1080,
        ResolutionLimit.P720 => 720,
        ResolutionLimit.P480 => 480,
        _ => null,
    };

    /// <summary>出力ファイル名（拡張子なし）。例: "movie" → "movie_small"</summary>
    public static string GetOutputBaseName(string inputPath, ConversionOptions options)
        => Path.GetFileNameWithoutExtension(inputPath) + OutputPathResolver.GetSuffix(options.Mode);

    /// <summary>出力の拡張子（"." 付き）。音声の「元のまま」は元のコーデックで決まる。</summary>
    public static string GetOutputExtension(ConversionOptions options, MediaInfo media)
    {
        switch (options.Mode)
        {
            case ConversionMode.AudioExtract:
                return options.Audio.Format switch
                {
                    AudioFormat.Mp3 => ".mp3",
                    AudioFormat.M4a => ".m4a",
                    AudioFormat.Wav => ".wav",
                    AudioFormat.Copy => ContainerSupport.GetAudioCopyExtension(
                        media.AudioCodec ?? throw NoAudio(), out _),
                    _ => throw new ArgumentOutOfRangeException(nameof(options)),
                };
            case ConversionMode.Compress:
                return ".mp4";
            case ConversionMode.Gif:
                return ".gif";
            case ConversionMode.Convert:
                return ContainerSupport.GetExtension(options.Convert.Container);
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>
    /// 変換手順を作る。変換できない条件（音声なし・目標サイズが小さすぎる等）は
    /// <see cref="ConversionException"/>（日本語メッセージ）を投げる。
    /// </summary>
    /// <param name="tempPrefix">一時ファイル用のパス（拡張子なし）。例: %TEMP%\PoiConvert\abc123</param>
    public static ConversionPlan Build(string inputPath, string outputPath, MediaInfo media,
        ConversionOptions options, string tempPrefix)
    {
        return options.Mode switch
        {
            ConversionMode.AudioExtract => BuildAudio(inputPath, outputPath, media, options.Audio),
            ConversionMode.Compress => BuildCompress(inputPath, outputPath, media, options.Compress, options.X264Preset, tempPrefix),
            ConversionMode.Gif => BuildGif(inputPath, outputPath, media, options.Gif, tempPrefix),
            ConversionMode.Convert => BuildConvert(inputPath, outputPath, media, options.Convert, options.X264Preset),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    // ───────────────────────────── A. 音声抽出

    private static ConversionPlan BuildAudio(string input, string output, MediaInfo media, AudioOptions o)
    {
        if (!media.HasAudio)
            throw NoAudio();

        var notes = new List<string>();
        var args = Start(input);
        args.AddRange(new[] { "-vn", "-sn", "-dn" });

        switch (o.Format)
        {
            case AudioFormat.Mp3:
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", Kbps(o.BitrateKbps) });
                break;
            case AudioFormat.M4a:
                args.AddRange(new[] { "-c:a", "aac", "-b:a", Kbps(o.BitrateKbps), "-movflags", "+faststart" });
                break;
            case AudioFormat.Wav:
                args.AddRange(new[] { "-c:a", "pcm_s16le" });
                break;
            case AudioFormat.Copy:
                ContainerSupport.GetAudioCopyExtension(media.AudioCodec!, out var fallback);
                if (fallback)
                    notes.Add($"音声形式「{media.AudioCodec}」には定番の拡張子がないため、何でも入る .mka（Matroska音声）で保存します。");
                args.AddRange(new[] { "-c:a", "copy" });
                break;
        }

        Finish(args, output);
        return new ConversionPlan
        {
            OutputPath = output,
            Steps = new[] { Step("音声抽出", args, 1.0, media.DurationSeconds) },
            Notes = notes,
        };
    }

    // ───────────────────────────── B. 軽量化

    private static ConversionPlan BuildCompress(string input, string output, MediaInfo media,
        CompressOptions o, string preset, string tempPrefix)
    {
        if (!media.HasVideo)
            throw new ConversionException("映像トラックがありません（音声だけのファイルは「音声抽出」を使ってください）。");

        var scale = ComputeScaleFilter(media, o.Resolution);
        var notes = new List<string>();
        if (scale is null && o.Resolution != ResolutionLimit.Original)
            notes.Add("元の解像度が指定より小さいので、解像度はそのままにします（拡大はしません）。");

        if (!o.UseTargetSize)
        {
            var args = Start(input);
            AddScale(args, scale);
            args.AddRange(X264(preset));
            args.AddRange(new[] { "-crf", GetCrf(o.Quality).ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", Kbps(CompressAudioKbps) });
            args.AddRange(new[] { "-sn", "-dn", "-movflags", "+faststart" });
            Finish(args, output);
            return new ConversionPlan
            {
                OutputPath = output,
                Steps = new[] { Step("軽量化", args, 1.0, media.DurationSeconds) },
                Notes = notes,
            };
        }

        // 目標サイズ（2パス）
        if (media.DurationSeconds is not { } duration || duration <= 0)
            throw new ConversionException("動画の長さが取得できないため、目標サイズモードは使えません（画質プリセットで変換してください）。");

        var (videoKbps, audioKbps) = ComputeTargetBitrates(o.TargetSizeMB, duration, media.HasAudio);
        notes.Add($"目標 {Fmt(o.TargetSizeMB)} MB → 映像 {videoKbps} kbps / 音声 {audioKbps} kbps の2パスで変換します。");

        var passLog = tempPrefix + "_pass";

        var pass1 = Start(input);
        AddScale(pass1, scale);
        pass1.AddRange(X264(preset));
        pass1.AddRange(new[] { "-b:v", Kbps(videoKbps), "-pix_fmt", "yuv420p" });
        pass1.AddRange(new[] { "-pass", "1", "-passlogfile", passLog });
        pass1.AddRange(new[] { "-an", "-sn", "-dn", "-f", "null", "-" });

        var pass2 = Start(input);
        AddScale(pass2, scale);
        pass2.AddRange(X264(preset));
        pass2.AddRange(new[] { "-b:v", Kbps(videoKbps), "-pix_fmt", "yuv420p" });
        pass2.AddRange(new[] { "-pass", "2", "-passlogfile", passLog });
        if (audioKbps > 0)
            pass2.AddRange(new[] { "-c:a", "aac", "-b:a", Kbps(audioKbps) });
        else
            pass2.Add("-an");
        pass2.AddRange(new[] { "-sn", "-dn", "-movflags", "+faststart" });
        Finish(pass2, output);

        return new ConversionPlan
        {
            OutputPath = output,
            Steps = new[]
            {
                Step("2パス変換（1回目：解析）", pass1, 0.4, duration),
                Step("2パス変換（2回目：本番）", pass2, 0.6, duration),
            },
            PassLogPrefix = passLog,
            Notes = notes,
        };
    }

    /// <summary>
    /// 縦横比を維持して「短い辺」を目標値に縮小するフィルタ。拡大はしない（不要なら null）。
    /// 縦動画（スマホ）でも 720p = 短辺 720px になる。
    /// </summary>
    public static string? ComputeScaleFilter(MediaInfo media, ResolutionLimit limit)
    {
        if (GetResolutionTarget(limit) is not { } t)
            return null;

        if (media.DisplayWidth is not { } w || media.DisplayHeight is not { } h || w <= 0 || h <= 0)
        {
            // 解像度不明：ffmpeg 側の式で「短辺を t 以下に」する（拡大しない）
            return $"scale='if(gte(iw,ih),-2,min({t},iw))':'if(gte(iw,ih),min({t},ih),-2)'";
        }

        if (Math.Min(w, h) <= t)
            return null;
        // -2 = 縦横比を保ったまま偶数に丸める（H.264/yuv420p は偶数が必要）
        return w >= h ? $"scale=-2:{t}" : $"scale={t}:-2";
    }

    /// <summary>目標サイズ（MB = 1024×1024 バイト）と長さから映像・音声ビットレート(kbps)を逆算する。</summary>
    public static (int VideoKbps, int AudioKbps) ComputeTargetBitrates(double targetMB, double durationSeconds, bool hasAudio)
    {
        if (targetMB <= 0)
            throw new ConversionException("目標サイズは 0 より大きい値を入れてください。");
        if (durationSeconds <= 0)
            throw new ConversionException("動画の長さが不正です。");

        double totalKbps = targetMB * 1024 * 1024 * 8 / durationSeconds / 1000 * TargetSizeOverheadFactor;
        int audio = !hasAudio ? 0 : totalKbps < 400 ? 64 : CompressAudioKbps;
        int video = (int)Math.Floor(totalKbps - audio);

        if (video < MinTargetVideoKbps)
        {
            double minMB = (MinTargetVideoKbps + audio) * 1000.0 * durationSeconds / 8 / 1024 / 1024 / TargetSizeOverheadFactor;
            minMB = Math.Ceiling(minMB * 10) / 10;
            throw new ConversionException(
                $"目標サイズが小さすぎます。この長さ（{FormatDuration(durationSeconds)}）なら最低でも約 {Fmt(minMB)} MB は必要です。");
        }
        return (video, audio);
    }

    // ───────────────────────────── C. GIF

    private static ConversionPlan BuildGif(string input, string output, MediaInfo media, GifOptions o, string tempPrefix)
    {
        if (!media.HasVideo)
            throw new ConversionException("映像トラックがないため GIF にできません。");
        if (o.StartSeconds < 0)
            throw new ConversionException("開始時間は 0 以上にしてください。");
        if (o.DurationSeconds <= 0)
            throw new ConversionException("GIF の長さ（秒）は 0 より大きい値を入れてください。");
        if (o.Fps <= 0)
            throw new ConversionException("FPS が不正です。");

        double effective = o.DurationSeconds;
        if (media.DurationSeconds is { } total)
        {
            if (o.StartSeconds >= total)
                throw new ConversionException(
                    $"開始時間（{FormatDuration(o.StartSeconds)}）が動画の長さ（{FormatDuration(total)}）を超えています。");
            effective = Math.Min(o.DurationSeconds, total - o.StartSeconds);
        }

        var notes = new List<string>();
        if (effective > ConversionOptions.GifWarnDurationSeconds)
            notes.Add($"GIF が {Fmt(effective)} 秒と長めです。ファイルがかなり大きくなる可能性があります。");

        var filter = $"fps={o.Fps.ToString(CultureInfo.InvariantCulture)}";
        if (o.Width > 0)
        {
            if (media.DisplayWidth is { } w && w <= o.Width)
                notes.Add($"元の横幅（{w}px）が指定（{o.Width}px）以下なので、横幅はそのままにします。");
            else
                filter += $",scale={o.Width.ToString(CultureInfo.InvariantCulture)}:-1:flags=lanczos";
        }

        var palette = tempPrefix + "_palette.png";
        var ss = Fmt(o.StartSeconds);
        var t = Fmt(o.DurationSeconds);

        // 1段階目: 色パレットを作る
        var step1 = new List<string>(CommonPrefix) { "-ss", ss, "-t", t, "-i", input };
        step1.AddRange(new[] { "-vf", filter + ",palettegen=stats_mode=diff" });
        step1.AddRange(new[] { "-frames:v", "1", "-update", "1", "-y", palette }); // 一時ファイルなので -y で可

        // 2段階目: パレットを使って GIF 化
        var step2 = new List<string>(CommonPrefix) { "-ss", ss, "-t", t, "-i", input, "-i", palette };
        step2.AddRange(new[] { "-lavfi", filter + "[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" });
        step2.AddRange(new[] { "-an", "-loop", "0" });
        Finish(step2, output);

        return new ConversionPlan
        {
            OutputPath = output,
            Steps = new[]
            {
                Step("GIF（1段階目：パレット作成）", step1, 0.3, effective),
                Step("GIF（2段階目：書き出し）", step2, 0.7, effective),
            },
            TempFiles = new[] { palette },
            Notes = notes,
        };
    }

    // ───────────────────────────── D. 形式変換

    private static ConversionPlan BuildConvert(string input, string output, MediaInfo media, ConvertOptions o, string preset)
    {
        var notes = new List<string>();
        var c = o.Container;
        bool copy = false;
        if (o.PreferStreamCopy)
        {
            if (ContainerSupport.CanStreamCopy(c, media, out var reason))
                copy = true;
            else
                notes.Add($"{reason}。自動的に再エンコードします。");
        }

        var args = Start(input);
        bool isMp4Like = c is ContainerFormat.Mp4 or ContainerFormat.Mov;

        if (copy)
        {
            if (c == ContainerFormat.Mkv)
                args.AddRange(new[] { "-map", "0", "-dn" }); // MKV は字幕・複数音声も丸ごと
            else
                args.AddRange(new[] { "-sn", "-dn" });
            args.AddRange(new[] { "-c", "copy" });
            if (isMp4Like && string.Equals(media.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
                args.AddRange(new[] { "-tag:v", "hvc1" }); // Apple 系で再生できるように
        }
        else if (c == ContainerFormat.Webm)
        {
            args.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-pix_fmt", "yuv420p" });
            args.AddRange(new[] { "-c:a", "libopus", "-b:a", Kbps(128) });
            args.AddRange(new[] { "-sn", "-dn" });
        }
        else
        {
            args.AddRange(X264(preset));
            args.AddRange(new[] { "-crf", "23", "-pix_fmt", "yuv420p" });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", Kbps(ConvertAudioKbps) });
            if (c == ContainerFormat.Mkv)
                args.AddRange(new[] { "-c:s", "copy", "-dn" });
            else
                args.AddRange(new[] { "-sn", "-dn" });
        }

        if (isMp4Like)
            args.AddRange(new[] { "-movflags", "+faststart" });
        Finish(args, output);

        return new ConversionPlan
        {
            OutputPath = output,
            Steps = new[] { Step(copy ? "形式変換（再エンコードなし）" : "形式変換（再エンコード）", args, 1.0, media.DurationSeconds) },
            Notes = notes,
        };
    }

    // ───────────────────────────── helpers

    private static List<string> Start(string input)
    {
        var list = new List<string>(CommonPrefix) { "-i", input };
        return list;
    }

    /// <summary>出力を追加する。-n = 既存ファイルがあれば絶対に上書きせず失敗させる（二重の安全策）。</summary>
    private static void Finish(List<string> args, string output)
    {
        args.Add("-n");
        args.Add(output);
    }

    private static void AddScale(List<string> args, string? scale)
    {
        if (scale is not null)
            args.AddRange(new[] { "-vf", scale });
    }

    private static IEnumerable<string> X264(string preset)
    {
        if (Array.IndexOf(ConversionOptions.X264Presets, preset) < 0)
            preset = "veryfast";
        return new[] { "-c:v", "libx264", "-preset", preset };
    }

    private static FfmpegStep Step(string desc, List<string> args, double weight, double? duration) => new()
    {
        Description = desc,
        Arguments = args,
        Weight = weight,
        ExpectedDurationSeconds = duration,
    };

    private static ConversionException NoAudio() => new("音声トラックがありません。");

    private static string Kbps(int kbps) => kbps.ToString(CultureInfo.InvariantCulture) + "k";

    public static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string FormatDuration(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds))
            return "--:--";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
