using PoiConvert.Core;

namespace PoiConvert.Core.Tests;

public class FfmpegLocatorTests
{
    private static FfmpegLocator Win(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return new FfmpegLocator(set.Contains, isWindows: true);
    }

    private static string P(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void exeと同じフォルダが最優先()
    {
        var loc = Win(P("app", "ffmpeg.exe"), P("app", "ffprobe.exe"), P("conf", "ffmpeg.exe"), P("conf", "ffprobe.exe"));
        var r = loc.Locate("app", "conf", "path1");
        Assert.NotNull(r);
        Assert.Equal(FfmpegSource.AppFolder, r!.Source);
        Assert.Equal(P("app", "ffmpeg.exe"), r.Ffmpeg);
        Assert.Equal(P("app", "ffprobe.exe"), r.Ffprobe);
    }

    [Fact]
    public void 次に設定のパス_フォルダ指定()
    {
        var loc = Win(P("conf", "ffmpeg.exe"), P("conf", "ffprobe.exe"), P("p1", "ffmpeg.exe"), P("p1", "ffprobe.exe"));
        var r = loc.Locate("app", "conf", "p1");
        Assert.Equal(FfmpegSource.Settings, r!.Source);
    }

    [Fact]
    public void 設定のパス_ffmpeg_exe自体を指定()
    {
        var loc = Win(P("C:", "tools", "ff mpeg", "ffmpeg.exe"), P("C:", "tools", "ff mpeg", "ffprobe.exe"));
        var r = loc.Locate("app", "\"" + P("C:", "tools", "ff mpeg", "ffmpeg.exe") + "\"", null);
        Assert.Equal(FfmpegSource.Settings, r!.Source);
        Assert.Equal(P("C:", "tools", "ff mpeg", "ffprobe.exe"), r.Ffprobe);
    }

    [Fact]
    public void 最後にPATH()
    {
        var loc = Win(P("p2", "ffmpeg.exe"), P("p2", "ffprobe.exe"));
        var r = loc.Locate("app", null, "p1;;\"p2\";p3");
        Assert.Equal(FfmpegSource.PathEnvironment, r!.Source);
        Assert.Equal(P("p2", "ffmpeg.exe"), r.Ffmpeg);
    }

    [Fact]
    public void ffprobeが欠けている場所は採用しない()
    {
        var loc = Win(P("app", "ffmpeg.exe"), P("p1", "ffmpeg.exe"), P("p1", "ffprobe.exe"));
        var r = loc.Locate("app", null, "p1");
        Assert.Equal(FfmpegSource.PathEnvironment, r!.Source);
    }

    [Fact]
    public void 見つからなければnull()
    {
        Assert.Null(Win().Locate("app", "conf", "p1;p2"));
        Assert.Null(Win().Locate(null, null, null));
    }

    [Fact]
    public void Linuxでは拡張子なしとコロン区切り()
    {
        var set = new HashSet<string> { P("/usr", "bin", "ffmpeg"), P("/usr", "bin", "ffprobe") };
        var loc = new FfmpegLocator(set.Contains, isWindows: false);
        var r = loc.Locate(null, null, "/opt:" + P("/usr", "bin"));
        Assert.Equal(P("/usr", "bin", "ffmpeg"), r!.Ffmpeg);
    }
}

public class SettingsStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "PoiConvertTest_" + Guid.NewGuid().ToString("N"), "設定", "settings.json");

    [Fact]
    public void 保存して読み込むと同じ値()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var s = new AppSettings
            {
                OutputToSourceFolder = false,
                OutputFolder = @"D:\出力 先",
                FfmpegPath = @"C:\ffmpeg\bin",
                Concurrency = 2,
            };
            s.Options.Mode = ConversionMode.Gif;
            s.Options.Gif.Fps = 20;
            s.Options.Gif.Width = 0;
            s.Options.Audio.Format = AudioFormat.Copy;
            s.Options.Audio.BitrateKbps = 320;
            s.Options.Compress.UseTargetSize = true;
            s.Options.Compress.TargetSizeMB = 8.5;
            s.Options.Compress.Resolution = ResolutionLimit.P720;
            s.Options.Convert.Container = ContainerFormat.Webm;
            s.Options.X264Preset = "medium";
            store.Save(s);

            var json = File.ReadAllText(path);
            Assert.Contains("\"Gif\"", json); // enum は文字列で保存（人が読める）

            var l = store.Load();
            Assert.False(l.OutputToSourceFolder);
            Assert.Equal(@"D:\出力 先", l.OutputFolder);
            Assert.Equal(@"C:\ffmpeg\bin", l.FfmpegPath);
            Assert.Equal(2, l.Concurrency);
            Assert.Equal(ConversionMode.Gif, l.Options.Mode);
            Assert.Equal(20, l.Options.Gif.Fps);
            Assert.Equal(0, l.Options.Gif.Width);
            Assert.Equal(AudioFormat.Copy, l.Options.Audio.Format);
            Assert.Equal(320, l.Options.Audio.BitrateKbps);
            Assert.True(l.Options.Compress.UseTargetSize);
            Assert.Equal(8.5, l.Options.Compress.TargetSizeMB);
            Assert.Equal(ResolutionLimit.P720, l.Options.Compress.Resolution);
            Assert.Equal(ContainerFormat.Webm, l.Options.Convert.Container);
            Assert.Equal("medium", l.Options.X264Preset);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, true);
        }
    }

    [Fact]
    public void ファイルがなければ既定値()
    {
        var s = new SettingsStore(TempFile()).Load();
        Assert.True(s.OutputToSourceFolder);
        Assert.Equal(1, s.Concurrency);
        Assert.Equal("veryfast", s.Options.X264Preset);
    }

    [Fact]
    public void 壊れたファイルや範囲外の値でも起動できる()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Equal(1, new SettingsStore(path).Load().Concurrency);

            File.WriteAllText(path, """{ "Concurrency": 9, "Options": { "X264Preset": "bogus", "Gif": { "Fps": 99 } } }""");
            var s = new SettingsStore(path).Load();
            Assert.Equal(2, s.Concurrency);
            Assert.Equal("veryfast", s.Options.X264Preset);
            Assert.Equal(15, s.Options.Gif.Fps);
            Assert.NotNull(s.Options.Audio);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, true);
        }
    }
}
