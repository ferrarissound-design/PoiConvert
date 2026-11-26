using PoiConvert.Core;

namespace PoiConvert.Core.Tests;

/// <summary>
/// 本物の ffmpeg を使った結合テスト。PATH に ffmpeg / ffprobe が無い環境ではスキップする。
/// テスト用の動画は ffmpeg の lavfi（テストパターン＋サイン波）で生成する。
/// </summary>
public sealed class IntegrationFixture : IDisposable
{
    public FfmpegPaths? Ffmpeg { get; }
    public string Root { get; }
    /// <summary>空白・日本語・記号入りのフォルダ名・ファイル名。</summary>
    public string SourceVideo { get; } = "";
    public string SourceAudioOnly { get; } = "";

    public IntegrationFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "PoiConvert 結合テスト_" + Guid.NewGuid().ToString("N")[..8], "動画 & 音声 (テスト)");
        Directory.CreateDirectory(Root);
        Ffmpeg = new FfmpegLocator().Locate(null, Environment.GetEnvironmentVariable("POICONVERT_FFMPEG"),
            Environment.GetEnvironmentVariable("PATH"));
        if (Ffmpeg is null)
            return;

        SourceVideo = Path.Combine(Root, "テスト 動画 #1 'a'.mov");
        Generate(new[]
        {
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
        }, SourceVideo);

        SourceAudioOnly = Path.Combine(Root, "音声 だけ.wav");
        Generate(new[] { "-f", "lavfi", "-i", "sine=frequency=880:duration=3", "-c:a", "pcm_s16le" }, SourceAudioOnly);
    }

    private void Generate(string[] args, string output)
    {
        var all = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        all.AddRange(args);
        all.Add("-y");
        all.Add(output);
        var r = ProcessRunner.RunAsync(Ffmpeg!.Ffmpeg, all, null, false, CancellationToken.None).GetAwaiter().GetResult();
        if (r.ExitCode != 0)
            throw new InvalidOperationException("テスト素材の生成に失敗: " + r.StderrTail);
    }

    public string NewOutputDir()
    {
        var d = Path.Combine(Root, "出力 " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Root)!, true); } catch { }
    }
}

public class IntegrationTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _f;

    public IntegrationTests(IntegrationFixture f)
    {
        _f = f;
    }

    private ConversionService Service(string? temp = null)
    {
        Skip.If(_f.Ffmpeg is null, "ffmpeg / ffprobe が見つからないためスキップ");
        return new ConversionService(_f.Ffmpeg!, tempDirectory: temp);
    }

    private async Task<(ConversionResult Result, MediaInfo Out, List<double> Progress, List<string> Log)> Run(
        ConversionOptions o, string? input = null, string? outDir = null, string? temp = null)
    {
        var svc = Service(temp);
        input ??= _f.SourceVideo;
        var media = await svc.ProbeAsync(input, CancellationToken.None);
        var progress = new List<double>();
        var log = new List<string>();
        var result = await svc.ConvertAsync(input, media, o,
            new OutputSettings { ToSourceFolder = false, Folder = outDir ?? _f.NewOutputDir() },
            new SyncProgress(progress), log.Add, CancellationToken.None);
        Assert.True(result.Outcome == ConversionOutcome.Succeeded, $"{result.Message}\n{result.ErrorTail}");
        var outMedia = await svc.ProbeAsync(result.OutputPath!, CancellationToken.None);
        return (result, outMedia, progress, log);
    }

    [SkippableFact]
    public async Task ffprobeで長さと解像度が取れる()
    {
        var svc = Service();
        var m = await svc.ProbeAsync(_f.SourceVideo, CancellationToken.None);
        Assert.Equal(1280, m.Width);
        Assert.Equal(720, m.Height);
        Assert.InRange(m.DurationSeconds!.Value, 5.5, 6.5);
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal("aac", m.AudioCodec);
    }

    [SkippableTheory]
    [InlineData(AudioFormat.Mp3, ".mp3", "mp3")]
    [InlineData(AudioFormat.M4a, ".m4a", "aac")]
    [InlineData(AudioFormat.Wav, ".wav", "pcm_s16le")]
    [InlineData(AudioFormat.Copy, ".m4a", "aac")]
    public async Task 音声抽出(AudioFormat format, string ext, string codec)
    {
        var o = new ConversionOptions { Mode = ConversionMode.AudioExtract };
        o.Audio.Format = format;
        var (r, m, progress, _) = await Run(o);
        Assert.EndsWith("_audio" + ext, r.OutputPath);
        Assert.False(m.HasVideo);
        Assert.Equal(codec, m.AudioCodec);
        Assert.Equal(1.0, progress[^1], 6);
    }

    [SkippableFact]
    public async Task 軽量化_720pを480pに縮小()
    {
        var o = new ConversionOptions { Mode = ConversionMode.Compress };
        o.Compress.Resolution = ResolutionLimit.P480;
        o.X264Preset = "ultrafast";
        var (r, m, progress, _) = await Run(o);
        Assert.EndsWith("テスト 動画 #1 'a'_small.mp4", r.OutputPath);
        Assert.Equal(480, m.Height);
        Assert.Equal(854, m.Width);
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal("aac", m.AudioCodec);
        Assert.Equal(1.0, progress[^1], 6);
    }

    [SkippableFact]
    public async Task 軽量化_1080p指定でも拡大しない()
    {
        var o = new ConversionOptions { Mode = ConversionMode.Compress };
        o.Compress.Resolution = ResolutionLimit.P1080;
        o.X264Preset = "ultrafast";
        var (_, m, _, _) = await Run(o);
        Assert.Equal(720, m.Height);
    }

    [SkippableFact]
    public async Task 軽量化_目標サイズ2パスで目標以下に収まり一時ファイルが残らない()
    {
        var temp = Path.Combine(_f.Root, "一時 " + Guid.NewGuid().ToString("N")[..6]);
        var o = new ConversionOptions { Mode = ConversionMode.Compress };
        o.Compress.UseTargetSize = true;
        o.Compress.TargetSizeMB = 0.5;
        o.X264Preset = "ultrafast";
        var (r, _, _, log) = await Run(o, temp: temp);
        var size = new FileInfo(r.OutputPath!).Length;
        Assert.True(size <= 0.5 * 1024 * 1024 * 1.05, $"size={size}");
        Assert.Contains(log, l => l.Contains("2パス"));
        Assert.Empty(Directory.EnumerateFiles(temp));
    }

    [SkippableTheory]
    [InlineData(10, 320, 320)]
    [InlineData(15, 0, 1280)]
    public async Task GIF(int fps, int width, int expectedWidth)
    {
        var temp = Path.Combine(_f.Root, "一時 " + Guid.NewGuid().ToString("N")[..6]);
        var o = new ConversionOptions { Mode = ConversionMode.Gif };
        o.Gif.StartSeconds = 1;
        o.Gif.DurationSeconds = 2;
        o.Gif.Fps = fps;
        o.Gif.Width = width;
        var (r, m, _, _) = await Run(o, temp: temp);
        Assert.EndsWith("テスト 動画 #1 'a'.gif", r.OutputPath);
        Assert.Equal("gif", m.VideoCodec);
        Assert.Equal(expectedWidth, m.Width);
        Assert.Empty(Directory.EnumerateFiles(temp)); // パレットPNGが消えている
    }

    [SkippableTheory]
    [InlineData(ContainerFormat.Mp4, true, "h264")]
    [InlineData(ContainerFormat.Mkv, true, "h264")]
    [InlineData(ContainerFormat.Mov, false, "h264")]
    [InlineData(ContainerFormat.Webm, true, "vp9")] // コピー不可 → 自動で再エンコード
    public async Task 形式変換(ContainerFormat c, bool copy, string expectedCodec)
    {
        var o = new ConversionOptions { Mode = ConversionMode.Convert, X264Preset = "ultrafast" };
        o.Convert.Container = c;
        o.Convert.PreferStreamCopy = copy;
        var (r, m, _, _) = await Run(o);
        Assert.EndsWith(ContainerSupport.GetExtension(c), r.OutputPath);
        Assert.Equal(expectedCodec, m.VideoCodec);
    }

    [SkippableFact]
    public async Task 同名ファイルは上書きせず連番()
    {
        var dir = _f.NewOutputDir();
        var existing = Path.Combine(dir, "テスト 動画 #1 'a'_audio.mp3");
        File.WriteAllText(existing, "大事なファイル");
        var o = new ConversionOptions { Mode = ConversionMode.AudioExtract };

        var (r1, _, _, _) = await Run(o, outDir: dir);
        var (r2, _, _, _) = await Run(o, outDir: dir);

        Assert.Equal(Path.Combine(dir, "テスト 動画 #1 'a'_audio_1.mp3"), r1.OutputPath);
        Assert.Equal(Path.Combine(dir, "テスト 動画 #1 'a'_audio_2.mp3"), r2.OutputPath);
        Assert.Equal("大事なファイル", File.ReadAllText(existing));
    }

    [SkippableFact]
    public async Task 元と同じフォルダに出力()
    {
        var svc = Service();
        var media = await svc.ProbeAsync(_f.SourceAudioOnly, CancellationToken.None);
        var o = new ConversionOptions { Mode = ConversionMode.AudioExtract };
        var r = await svc.ConvertAsync(_f.SourceAudioOnly, media, o, new OutputSettings { ToSourceFolder = true },
            null, null, CancellationToken.None);
        try
        {
            Assert.Equal(ConversionOutcome.Succeeded, r.Outcome);
            Assert.Equal(Path.GetDirectoryName(_f.SourceAudioOnly), Path.GetDirectoryName(r.OutputPath));
        }
        finally
        {
            if (r.OutputPath is not null) File.Delete(r.OutputPath);
        }
    }

    [SkippableFact]
    public async Task 音声だけのファイルを軽量化すると分かりやすく失敗()
    {
        var svc = Service();
        var media = await svc.ProbeAsync(_f.SourceAudioOnly, CancellationToken.None);
        var r = await svc.ConvertAsync(_f.SourceAudioOnly, media, new ConversionOptions { Mode = ConversionMode.Compress },
            new OutputSettings { ToSourceFolder = false, Folder = _f.NewOutputDir() }, null, null, CancellationToken.None);
        Assert.Equal(ConversionOutcome.Failed, r.Outcome);
        Assert.Contains("映像トラックがありません", r.Message);
    }

    [SkippableFact]
    public async Task 動画でないファイルは読み込みエラー()
    {
        var svc = Service();
        var bogus = Path.Combine(_f.Root, "ただの テキスト.mp4");
        File.WriteAllText(bogus, "not a video");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => svc.ProbeAsync(bogus, CancellationToken.None));
        Assert.Contains("読み込めません", ex.Message);
    }

    [SkippableFact]
    public async Task キャンセルするとffmpegが止まり作りかけの出力が消える()
    {
        var svc = Service();
        // 長めの素材（重いプリセットで時間がかかるようにする）
        var longSrc = Path.Combine(_f.Root, "長い 動画.mkv");
        if (!File.Exists(longSrc))
        {
            var gen = new List<string>(new[] { "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=120",
                "-c:v", "libx264", "-preset", "ultrafast", "-y", longSrc });
            var g = await ProcessRunner.RunAsync(_f.Ffmpeg!.Ffmpeg, gen, null, false, CancellationToken.None);
            Assert.Equal(0, g.ExitCode);
        }

        var dir = _f.NewOutputDir();
        var media = await svc.ProbeAsync(longSrc, CancellationToken.None);
        var o = new ConversionOptions { Mode = ConversionMode.Compress, X264Preset = "veryslow" };
        using var cts = new CancellationTokenSource();
        var sawProgress = new TaskCompletionSource();
        var reported = new List<double>();
        var progress = new SyncProgress(reported, p => { if (p > 0) sawProgress.TrySetResult(); });

        var task = svc.ConvertAsync(longSrc, media, o, new OutputSettings { ToSourceFolder = false, Folder = dir },
            progress, null, cts.Token);
        await Task.WhenAny(sawProgress.Task, Task.Delay(30_000));
        Assert.True(Directory.EnumerateFiles(dir).Any(), "変換中の出力ファイルができているはず");
        // 変換の途中で -progress の進捗（0〜1 の間）が届いている
        lock (reported)
            Assert.Contains(reported, p => p > 0 && p < 1);
        cts.Cancel();
        var r = await task;

        Assert.Equal(ConversionOutcome.Canceled, r.Outcome);
        Assert.Empty(Directory.EnumerateFiles(dir));
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly List<double> _list;
        private readonly Action<double>? _on;
        public SyncProgress(List<double> list, Action<double>? on = null) { _list = list; _on = on; }
        public void Report(double value)
        {
            lock (_list) _list.Add(value);
            _on?.Invoke(value);
        }
    }
}
