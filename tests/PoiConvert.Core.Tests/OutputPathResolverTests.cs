using PoiConvert.Core;

namespace PoiConvert.Core.Tests;

public class OutputPathResolverTests
{
    private static readonly string Dir = Path.Combine("x", "出力 先");

    [Fact]
    public void 同名がなければそのままの名前()
    {
        var r = new OutputPathResolver(_ => false);
        Assert.Equal(Path.Combine(Dir, "動画_small.mp4"), r.Reserve(Dir, "動画_small", ".mp4"));
    }

    [Fact]
    public void 同名があれば連番_1_2()
    {
        var existing = new HashSet<string>
        {
            Path.Combine(Dir, "a b.gif"),
            Path.Combine(Dir, "a b_1.gif"),
        };
        var r = new OutputPathResolver(existing.Contains);
        Assert.Equal(Path.Combine(Dir, "a b_2.gif"), r.Reserve(Dir, "a b", ".gif"));
    }

    [Fact]
    public void 途中が空いていればそこを使う()
    {
        var existing = new HashSet<string>
        {
            Path.Combine(Dir, "m.mp3"),
            Path.Combine(Dir, "m_2.mp3"),
        };
        var r = new OutputPathResolver(existing.Contains);
        Assert.Equal(Path.Combine(Dir, "m_1.mp3"), r.Reserve(Dir, "m", "mp3"));
    }

    [Fact]
    public void 予約中の名前は同時実行の別ジョブに渡さない()
    {
        var r = new OutputPathResolver(_ => false);
        var a = r.Reserve(Dir, "v", ".mp4");
        var b = r.Reserve(Dir, "v", ".mp4");
        var c = r.Reserve(Dir, "v", ".mp4");
        Assert.Equal(Path.Combine(Dir, "v.mp4"), a);
        Assert.Equal(Path.Combine(Dir, "v_1.mp4"), b);
        Assert.Equal(Path.Combine(Dir, "v_2.mp4"), c);

        r.Release(a);
        Assert.Equal(Path.Combine(Dir, "v.mp4"), r.Reserve(Dir, "v", ".mp4"));
    }

    [Fact]
    public void 大文字小文字違いも同名とみなす_Windows向け()
    {
        var r = new OutputPathResolver(_ => false);
        r.Reserve(Dir, "Clip", ".MP4");
        Assert.Equal(Path.Combine(Dir, "clip_1.mp4"), r.Reserve(Dir, "clip", ".mp4"));
    }

    [Fact]
    public void 実ファイルシステムで既存ファイルを避ける()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PoiConvertTest_" + Guid.NewGuid().ToString("N"), "日本語 フォルダ");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "曲 (1)_audio.mp3"), "keep");
            var r = new OutputPathResolver();
            var p = r.Reserve(dir, "曲 (1)_audio", ".mp3");
            Assert.Equal(Path.Combine(dir, "曲 (1)_audio_1.mp3"), p);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(dir, "曲 (1)_audio.mp3")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, true);
        }
    }

    [Theory]
    [InlineData(ConversionMode.AudioExtract, "_audio")]
    [InlineData(ConversionMode.Compress, "_small")]
    [InlineData(ConversionMode.Gif, "")]
    [InlineData(ConversionMode.Convert, "")]
    public void モードごとの接尾辞(ConversionMode mode, string suffix)
    {
        Assert.Equal(suffix, OutputPathResolver.GetSuffix(mode));
    }
}
