using PoiConvert.Core;

namespace PoiConvert.Core.Tests;

public class FfmpegCommandBuilderTests
{
    // 空白・日本語・記号入りのパス（1要素のまま渡ることを確認する）
    private const string Input = @"C:\動画 フォルダ\テスト (1) & 'x' #2.mp4";
    private const string Output = @"C:\出力 先\テスト (1) & 'x' #2_small.mp4";
    private const string Temp = @"C:\Temp\PoiConvert\abc";

    private static MediaInfo Video(int w = 1920, int h = 1080, double dur = 60, string v = "h264", string? a = "aac", int rot = 0)
        => new() { Width = w, Height = h, DurationSeconds = dur, VideoCodec = v, AudioCodec = a, Rotation = rot, SizeBytes = 10_000_000 };

    private static MediaInfo AudioOnly(string a = "mp3", double dur = 180)
        => new() { AudioCodec = a, DurationSeconds = dur };

    private static ConversionOptions Opt(ConversionMode mode) => new() { Mode = mode };

    // ─────────────── 共通

    [Theory]
    [InlineData(ConversionMode.AudioExtract)]
    [InlineData(ConversionMode.Compress)]
    [InlineData(ConversionMode.Gif)]
    [InlineData(ConversionMode.Convert)]
    public void 全モード_共通の先頭引数と上書き禁止と入力パスがそのまま入る(ConversionMode mode)
    {
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(), Opt(mode), Temp);
        foreach (var step in plan.Steps)
        {
            ArgAssert.StartsWithCommonPrefix(step.Arguments);
            ArgAssert.HasPair(step.Arguments, "-i", Input);
            ArgAssert.HasPair(step.Arguments, "-progress", "pipe:1");
            ArgAssert.HasFlag(step.Arguments, "-nostdin");
        }
        ArgAssert.EndsWithNoOverwrite(plan.Steps[^1].Arguments, Output);
        Assert.Equal(1.0, plan.Steps.Sum(s => s.Weight), 6);
        Assert.Equal(Output, plan.OutputPath);
    }

    // ─────────────── A. 音声抽出

    [Theory]
    [InlineData(AudioFormat.Mp3, 128, "libmp3lame", ".mp3")]
    [InlineData(AudioFormat.Mp3, 320, "libmp3lame", ".mp3")]
    [InlineData(AudioFormat.M4a, 192, "aac", ".m4a")]
    [InlineData(AudioFormat.M4a, 256, "aac", ".m4a")]
    public void 音声抽出_形式とビットレート(AudioFormat format, int kbps, string codec, string ext)
    {
        var o = Opt(ConversionMode.AudioExtract);
        o.Audio.Format = format;
        o.Audio.BitrateKbps = kbps;
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp);
        var args = plan.Steps.Single().Arguments;

        ArgAssert.HasFlag(args, "-vn");
        ArgAssert.HasPair(args, "-c:a", codec);
        ArgAssert.HasPair(args, "-b:a", $"{kbps}k");
        Assert.Equal(ext, FfmpegCommandBuilder.GetOutputExtension(o, Video()));
    }

    [Fact]
    public void 音声抽出_WAVはPCMでビットレート指定なし()
    {
        var o = Opt(ConversionMode.AudioExtract);
        o.Audio.Format = AudioFormat.Wav;
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp).Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-c:a", "pcm_s16le");
        ArgAssert.NoFlag(args, "-b:a");
        Assert.Equal(".wav", FfmpegCommandBuilder.GetOutputExtension(o, Video()));
    }

    [Theory]
    [InlineData("aac", ".m4a")]
    [InlineData("mp3", ".mp3")]
    [InlineData("opus", ".opus")]
    [InlineData("vorbis", ".ogg")]
    [InlineData("flac", ".flac")]
    [InlineData("pcm_s16le", ".wav")]
    [InlineData("pcm_s24le", ".wav")]
    [InlineData("pcm_s16be", ".aiff")]
    [InlineData("ac3", ".ac3")]
    [InlineData("alac", ".m4a")]
    [InlineData("dts", ".mka")]
    [InlineData("truehd", ".mka")]
    public void 音声抽出_元のままはコーデックから拡張子を決める(string codec, string ext)
    {
        var o = Opt(ConversionMode.AudioExtract);
        o.Audio.Format = AudioFormat.Copy;
        var media = Video(a: codec);
        var plan = FfmpegCommandBuilder.Build(Input, Output, media, o, Temp);
        var args = plan.Steps.Single().Arguments;

        ArgAssert.HasPair(args, "-c:a", "copy");
        ArgAssert.NoFlag(args, "-b:a");
        Assert.Equal(ext, FfmpegCommandBuilder.GetOutputExtension(o, media));
        // 定番の拡張子がない場合は .mka にして、その旨をログに出す（エラーで止めない）
        Assert.Equal(ext == ".mka", plan.Notes.Any(n => n.Contains(".mka")));
    }

    [Fact]
    public void 音声抽出_音声トラックなしは分かりやすいエラー()
    {
        var o = Opt(ConversionMode.AudioExtract);
        var ex = Assert.Throws<ConversionException>(() =>
            FfmpegCommandBuilder.Build(Input, Output, Video(a: null), o, Temp));
        Assert.Contains("音声トラックがありません", ex.Message);

        o.Audio.Format = AudioFormat.Copy;
        Assert.Throws<ConversionException>(() => FfmpegCommandBuilder.GetOutputExtension(o, Video(a: null)));
    }

    // ─────────────── B. 軽量化

    [Theory]
    [InlineData(QualityPreset.High, "20")]
    [InlineData(QualityPreset.Standard, "23")]
    [InlineData(QualityPreset.Light, "28")]
    public void 軽量化_画質プリセットのCRF(QualityPreset q, string crf)
    {
        var o = Opt(ConversionMode.Compress);
        o.Compress.Quality = q;
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp).Steps.Single().Arguments;

        ArgAssert.HasPair(args, "-c:v", "libx264");
        ArgAssert.HasPair(args, "-crf", crf);
        ArgAssert.HasPair(args, "-c:a", "aac");
        ArgAssert.HasPair(args, "-preset", "veryfast");
        ArgAssert.HasPair(args, "-pix_fmt", "yuv420p");
        ArgAssert.HasPair(args, "-movflags", "+faststart");
        ArgAssert.NoFlag(args, "-vf");
        Assert.Equal(".mp4", FfmpegCommandBuilder.GetOutputExtension(o, Video()));
    }

    [Fact]
    public void 軽量化_エンコード速度は設定で変えられる()
    {
        var o = Opt(ConversionMode.Compress);
        o.X264Preset = "slow";
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp).Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-preset", "slow");
    }

    [Fact]
    public void 軽量化_不正なプリセット名はveryfastに戻す()
    {
        var o = Opt(ConversionMode.Compress);
        o.X264Preset = "; rm -rf /";
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp).Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-preset", "veryfast");
    }

    [Theory]
    // 横長
    [InlineData(1920, 1080, 0, ResolutionLimit.P720, "scale=-2:720")]
    [InlineData(1920, 1080, 0, ResolutionLimit.P480, "scale=-2:480")]
    [InlineData(3840, 2160, 0, ResolutionLimit.P1080, "scale=-2:1080")]
    // 縮小のみ（拡大しない）
    [InlineData(1920, 1080, 0, ResolutionLimit.P1080, null)]
    [InlineData(1280, 720, 0, ResolutionLimit.P1080, null)]
    [InlineData(640, 360, 0, ResolutionLimit.P480, null)]
    [InlineData(1920, 1080, 0, ResolutionLimit.Original, null)]
    // 縦長（短い辺＝横幅を基準）
    [InlineData(1080, 1920, 0, ResolutionLimit.P720, "scale=720:-2")]
    // スマホ縦動画：保存は 1920x1080 + 回転90度 → 表示は 1080x1920
    [InlineData(1920, 1080, 90, ResolutionLimit.P720, "scale=720:-2")]
    [InlineData(1920, 1080, -90, ResolutionLimit.P720, "scale=720:-2")]
    [InlineData(1920, 1080, 180, ResolutionLimit.P720, "scale=-2:720")]
    public void 軽量化_解像度は縦横比維持で縮小のみ(int w, int h, int rot, ResolutionLimit r, string? expected)
    {
        Assert.Equal(expected, FfmpegCommandBuilder.ComputeScaleFilter(Video(w, h, rot: rot), r));

        var o = Opt(ConversionMode.Compress);
        o.Compress.Resolution = r;
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(w, h, rot: rot), o, Temp).Steps.Single().Arguments;
        Assert.Equal(expected, ArgAssert.ValueOf(args, "-vf"));
    }

    [Fact]
    public void 軽量化_解像度不明ならffmpegの式で縮小のみ()
    {
        var media = new MediaInfo { VideoCodec = "h264", AudioCodec = "aac", DurationSeconds = 10 };
        var f = FfmpegCommandBuilder.ComputeScaleFilter(media, ResolutionLimit.P720);
        Assert.NotNull(f);
        Assert.Contains("min(720,iw)", f);
        Assert.Contains("min(720,ih)", f);
    }

    [Fact]
    public void 軽量化_映像なしはエラー()
    {
        var ex = Assert.Throws<ConversionException>(() =>
            FfmpegCommandBuilder.Build(Input, Output, AudioOnly(), Opt(ConversionMode.Compress), Temp));
        Assert.Contains("映像トラックがありません", ex.Message);
    }

    [Fact]
    public void 軽量化_目標サイズは2パスでビットレートを逆算()
    {
        var o = Opt(ConversionMode.Compress);
        o.Compress.UseTargetSize = true;
        o.Compress.TargetSizeMB = 10;
        o.Compress.Resolution = ResolutionLimit.P720;
        var media = Video(dur: 100);
        var plan = FfmpegCommandBuilder.Build(Input, Output, media, o, Temp);

        Assert.Equal(2, plan.Steps.Count);
        var p1 = plan.Steps[0].Arguments;
        var p2 = plan.Steps[1].Arguments;

        // 10MB * 1024*1024*8 / 100s / 1000 * 0.97 = 813.69 kbps → 映像 685k + 音声 128k
        ArgAssert.HasPair(p1, "-b:v", "685k");
        ArgAssert.HasPair(p2, "-b:v", "685k");
        ArgAssert.HasPair(p2, "-b:a", "128k");
        ArgAssert.HasPair(p1, "-pass", "1");
        ArgAssert.HasPair(p2, "-pass", "2");
        ArgAssert.HasPair(p1, "-passlogfile", Temp + "_pass");
        ArgAssert.HasPair(p2, "-passlogfile", Temp + "_pass");
        ArgAssert.HasFlag(p1, "-an");
        ArgAssert.HasPair(p1, "-f", "null");
        Assert.Equal("-", p1[^1]);
        ArgAssert.NoFlag(p1, "-crf");
        ArgAssert.NoFlag(p2, "-crf");
        ArgAssert.HasPair(p1, "-vf", "scale=-2:720");
        ArgAssert.HasPair(p2, "-vf", "scale=-2:720");
        ArgAssert.EndsWithNoOverwrite(p2, Output);
        Assert.Equal(Temp + "_pass", plan.PassLogPrefix);
    }

    [Theory]
    [InlineData(10, 100, true, 685, 128)]
    [InlineData(100, 60, true, 13433, 128)]
    [InlineData(10, 100, false, 813, 0)]
    // 合計 400kbps 未満なら音声を 64k に下げる
    [InlineData(5, 120, true, 275, 64)]
    public void 目標サイズ_ビットレート計算(double mb, double dur, bool hasAudio, int video, int audio)
    {
        var (v, a) = FfmpegCommandBuilder.ComputeTargetBitrates(mb, dur, hasAudio);
        Assert.Equal(video, v);
        Assert.Equal(audio, a);
    }

    [Fact]
    public void 目標サイズ_小さすぎると最低サイズを案内()
    {
        var ex = Assert.Throws<ConversionException>(() => FfmpegCommandBuilder.ComputeTargetBitrates(1, 3600, true));
        Assert.Contains("目標サイズが小さすぎます", ex.Message);
        Assert.Contains("MB", ex.Message);
    }

    [Fact]
    public void 目標サイズ_長さ不明ならエラー()
    {
        var o = Opt(ConversionMode.Compress);
        o.Compress.UseTargetSize = true;
        var media = new MediaInfo { VideoCodec = "h264", Width = 1920, Height = 1080 };
        var ex = Assert.Throws<ConversionException>(() => FfmpegCommandBuilder.Build(Input, Output, media, o, Temp));
        Assert.Contains("長さ", ex.Message);
    }

    // ─────────────── C. GIF

    [Fact]
    public void GIF_パレット2段階()
    {
        var o = Opt(ConversionMode.Gif);
        o.Gif.StartSeconds = 12.5;
        o.Gif.DurationSeconds = 4;
        o.Gif.Fps = 10;
        o.Gif.Width = 320;
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp);

        Assert.Equal(2, plan.Steps.Count);
        var s1 = plan.Steps[0].Arguments;
        var s2 = plan.Steps[1].Arguments;
        var palette = Temp + "_palette.png";

        ArgAssert.HasPair(s1, "-ss", "12.5");
        ArgAssert.HasPair(s1, "-t", "4");
        ArgAssert.HasPair(s1, "-vf", "fps=10,scale=320:-1:flags=lanczos,palettegen=stats_mode=diff");
        Assert.Equal(palette, s1[^1]);

        ArgAssert.HasPair(s2, "-ss", "12.5");
        ArgAssert.HasPair(s2, "-t", "4");
        ArgAssert.HasPair(s2, "-i", palette);
        var lavfi = ArgAssert.ValueOf(s2, "-lavfi");
        Assert.StartsWith("fps=10,scale=320:-1:flags=lanczos[x];[x][1:v]paletteuse", lavfi);
        ArgAssert.EndsWithNoOverwrite(s2, Output);

        Assert.Contains(palette, plan.TempFiles);
        Assert.Equal(".gif", FfmpegCommandBuilder.GetOutputExtension(o, Video()));
    }

    [Theory]
    [InlineData(15, 480, "fps=15,scale=480:-1:flags=lanczos")]
    [InlineData(20, 640, "fps=20,scale=640:-1:flags=lanczos")]
    [InlineData(15, 0, "fps=15")]
    public void GIF_FPSと横幅(int fps, int width, string expectedFilterHead)
    {
        var o = Opt(ConversionMode.Gif);
        o.Gif.Fps = fps;
        o.Gif.Width = width;
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp);
        Assert.Equal(expectedFilterHead + ",palettegen=stats_mode=diff", ArgAssert.ValueOf(plan.Steps[0].Arguments, "-vf"));
    }

    [Fact]
    public void GIF_元が指定より小さければ拡大しない()
    {
        var o = Opt(ConversionMode.Gif);
        o.Gif.Width = 640;
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(480, 270), o, Temp);
        Assert.Equal("fps=15,palettegen=stats_mode=diff", ArgAssert.ValueOf(plan.Steps[0].Arguments, "-vf"));
    }

    [Fact]
    public void GIF_30秒超は警告メモ()
    {
        var o = Opt(ConversionMode.Gif);
        o.Gif.DurationSeconds = 45;
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(dur: 120), o, Temp);
        Assert.Contains(plan.Notes, n => n.Contains("長め"));

        o.Gif.DurationSeconds = 30;
        plan = FfmpegCommandBuilder.Build(Input, Output, Video(dur: 120), o, Temp);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("長め"));
    }

    [Fact]
    public void GIF_動画の残りより長い指定は進捗計算を残り時間に合わせる()
    {
        var o = Opt(ConversionMode.Gif);
        o.Gif.StartSeconds = 55;
        o.Gif.DurationSeconds = 10;
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(dur: 60), o, Temp);
        Assert.Equal(5, plan.Steps[1].ExpectedDurationSeconds);
    }

    [Theory]
    [InlineData(70, 5)]   // 開始が長さを超える
    [InlineData(-1, 5)]   // 負の開始
    [InlineData(0, 0)]    // 長さ0
    public void GIF_不正な時間指定はエラー(double start, double dur)
    {
        var o = Opt(ConversionMode.Gif);
        o.Gif.StartSeconds = start;
        o.Gif.DurationSeconds = dur;
        Assert.Throws<ConversionException>(() => FfmpegCommandBuilder.Build(Input, Output, Video(dur: 60), o, Temp));
    }

    // ─────────────── D. 形式変換

    [Theory]
    [InlineData(ContainerFormat.Mp4, ".mp4")]
    [InlineData(ContainerFormat.Mov, ".mov")]
    [InlineData(ContainerFormat.Mkv, ".mkv")]
    [InlineData(ContainerFormat.Webm, ".webm")]
    public void 形式変換_拡張子(ContainerFormat c, string ext)
    {
        var o = Opt(ConversionMode.Convert);
        o.Convert.Container = c;
        Assert.Equal(ext, FfmpegCommandBuilder.GetOutputExtension(o, Video()));
    }

    [Fact]
    public void 形式変換_H264をMOVへは再エンコードなし()
    {
        var o = Opt(ConversionMode.Convert);
        o.Convert.Container = ContainerFormat.Mov;
        o.Convert.PreferStreamCopy = true;
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp).Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-c", "copy");
        ArgAssert.NoFlag(args, "libx264");
        ArgAssert.HasPair(args, "-movflags", "+faststart");
    }

    [Fact]
    public void 形式変換_MKVへの再エンコードなしは全ストリーム()
    {
        var o = Opt(ConversionMode.Convert);
        o.Convert.Container = ContainerFormat.Mkv;
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(v: "prores", a: "pcm_s24le"), o, Temp).Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-map", "0");
        ArgAssert.HasPair(args, "-c", "copy");
    }

    [Fact]
    public void 形式変換_HEVCをMP4へコピーするときはhvc1タグ()
    {
        var o = Opt(ConversionMode.Convert);
        o.Convert.Container = ContainerFormat.Mp4;
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(v: "hevc"), o, Temp).Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-tag:v", "hvc1");
    }

    [Fact]
    public void 形式変換_WEBMにH264は入らないので自動で再エンコード()
    {
        var o = Opt(ConversionMode.Convert);
        o.Convert.Container = ContainerFormat.Webm;
        o.Convert.PreferStreamCopy = true;
        var plan = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp);
        var args = plan.Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-c:v", "libvpx-vp9");
        ArgAssert.HasPair(args, "-c:a", "libopus");
        ArgAssert.NoFlag(args, "copy");
        Assert.Contains(plan.Notes, n => n.Contains("h264") && n.Contains("再エンコード"));
    }

    [Fact]
    public void 形式変換_再エンコードなしをオフにするとH264再エンコード()
    {
        var o = Opt(ConversionMode.Convert);
        o.Convert.Container = ContainerFormat.Mp4;
        o.Convert.PreferStreamCopy = false;
        var args = FfmpegCommandBuilder.Build(Input, Output, Video(), o, Temp).Steps.Single().Arguments;
        ArgAssert.HasPair(args, "-c:v", "libx264");
        ArgAssert.HasPair(args, "-c:a", "aac");
        ArgAssert.HasPair(args, "-preset", "veryfast");
        ArgAssert.NoFlag(args, "copy");
    }

    [Theory]
    [InlineData(ContainerFormat.Webm, "vp9", "opus", true)]
    [InlineData(ContainerFormat.Webm, "h264", "aac", false)]
    [InlineData(ContainerFormat.Webm, "vp9", "aac", false)]
    [InlineData(ContainerFormat.Mp4, "h264", "aac", true)]
    [InlineData(ContainerFormat.Mp4, "prores", "aac", false)]
    [InlineData(ContainerFormat.Mp4, "h264", "pcm_s16le", false)]
    [InlineData(ContainerFormat.Mov, "prores", "pcm_s16le", true)]
    [InlineData(ContainerFormat.Mkv, "anything", "whatever", true)]
    public void コンテナ相性表(ContainerFormat c, string v, string a, bool expected)
    {
        Assert.Equal(expected, ContainerSupport.CanStreamCopy(c, Video(v: v, a: a), out var reason));
        Assert.Equal(expected, reason is null);
    }

    // ─────────────── 出力名

    [Theory]
    [InlineData(ConversionMode.AudioExtract, "旅行 動画_audio")]
    [InlineData(ConversionMode.Compress, "旅行 動画_small")]
    [InlineData(ConversionMode.Gif, "旅行 動画")]
    [InlineData(ConversionMode.Convert, "旅行 動画")]
    public void 出力ベース名(ConversionMode mode, string expected)
    {
        Assert.Equal(expected, FfmpegCommandBuilder.GetOutputBaseName(Path.Combine("dir", "旅行 動画.mov"), Opt(mode)));
    }
}
