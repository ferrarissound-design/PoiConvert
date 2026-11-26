using PoiConvert.Core;

namespace PoiConvert.Core.Tests;

public class ProgressParserTests
{
    [Theory]
    [InlineData("out_time_us=1500000", 1.5)]
    [InlineData("out_time_ms=2500000", 2.5)] // ffmpeg の out_time_ms は実際はマイクロ秒
    [InlineData("out_time=00:01:02.500000", 62.5)]
    [InlineData("out_time=01:00:00.000000", 3600)]
    [InlineData("out_time=-00:00:00.023220", 0)]
    public void 時間を読む(string line, double seconds)
    {
        var t = ProgressParser.TryParseOutTime(line);
        Assert.NotNull(t);
        Assert.Equal(seconds, t!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time=N/A")]
    [InlineData("frame=120")]
    [InlineData("progress=continue")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void 対象外の行はnull(string? line)
    {
        Assert.Null(ProgressParser.TryParseOutTime(line));
    }

    [Fact]
    public void 終了行()
    {
        Assert.True(ProgressParser.IsEnd("progress=end"));
        Assert.False(ProgressParser.IsEnd("progress=continue"));
    }

    [Theory]
    [InlineData(5.0, 10.0, 0.5)]
    [InlineData(15.0, 10.0, 1.0)]
    [InlineData(5.0, null, 0.0)]
    [InlineData(5.0, 0.0, 0.0)]
    public void 割合(double t, double? duration, double expected)
    {
        Assert.Equal(expected, ProgressParser.ToFraction(TimeSpan.FromSeconds(t), duration), 6);
    }
}

public class FfprobeParserTests
{
    private const string VideoJson = """
    {
      "streams": [
        { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1920, "height": 1080,
          "disposition": { "default": 1, "attached_pic": 0 } },
        { "index": 1, "codec_name": "aac", "codec_type": "audio", "sample_rate": "48000" }
      ],
      "format": { "filename": "C:\\動画\\a b.mp4", "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                  "duration": "63.480000", "size": "12345678" }
    }
    """;

    [Fact]
    public void 動画の情報()
    {
        var m = FfprobeParser.Parse(VideoJson);
        Assert.Equal(63.48, m.DurationSeconds!.Value, 3);
        Assert.Equal(12345678, m.SizeBytes);
        Assert.Equal(1920, m.Width);
        Assert.Equal(1080, m.Height);
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal("aac", m.AudioCodec);
        Assert.True(m.HasVideo);
        Assert.True(m.HasAudio);
        Assert.Equal(0, m.Rotation);
    }

    [Fact]
    public void スマホ縦動画の回転_新形式()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "hevc", "width": 1920, "height": 1080,
            "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] } ],
          "format": { "duration": "10" } }
        """;
        var m = FfprobeParser.Parse(json);
        Assert.Equal(-90, m.Rotation);
        Assert.Equal(1080, m.DisplayWidth);
        Assert.Equal(1920, m.DisplayHeight);
    }

    [Fact]
    public void スマホ縦動画の回転_旧形式タグ()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720,
            "tags": { "rotate": "90" } } ], "format": {} }
        """;
        var m = FfprobeParser.Parse(json);
        Assert.Equal(90, m.Rotation);
        Assert.Equal(720, m.DisplayWidth);
        Assert.Null(m.DurationSeconds);
    }

    [Fact]
    public void MP3のジャケット画像は映像扱いしない()
    {
        const string json = """
        { "streams": [
            { "codec_type": "audio", "codec_name": "mp3" },
            { "codec_type": "video", "codec_name": "mjpeg", "width": 500, "height": 500,
              "disposition": { "attached_pic": 1 } } ],
          "format": { "duration": "200.5", "size": "5000000" } }
        """;
        var m = FfprobeParser.Parse(json);
        Assert.False(m.HasVideo);
        Assert.Equal("mp3", m.AudioCodec);
        Assert.Null(m.Width);
    }

    [Fact]
    public void durationがN_Aでも落ちない()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480, "duration": "12.0" } ],
          "format": { "duration": "N/A" } }
        """;
        var m = FfprobeParser.Parse(json);
        Assert.Equal(12.0, m.DurationSeconds);
    }

    [Fact]
    public void 引数はJSON出力で入力パスは1要素()
    {
        var args = FfprobeParser.BuildArguments(@"C:\a b\日本語 & (1).mkv");
        ArgAssert.HasPair(args, "-print_format", "json");
        ArgAssert.HasFlag(args, "-show_format");
        ArgAssert.HasFlag(args, "-show_streams");
        Assert.Equal(@"C:\a b\日本語 & (1).mkv", args[^1]);
    }
}

public class TimeInputTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("12.5", 12.5)]
    [InlineData("1:23", 83)]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData(" 90 ", 90)]
    [InlineData("１：３０", 90)] // 全角
    public void 正しい入力(string text, double expected)
    {
        Assert.True(TimeInput.TryParseSeconds(text, out var s));
        Assert.Equal(expected, s, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:75")]
    [InlineData("1.5:20")]
    [InlineData("1:2:3:4")]
    [InlineData("1::2")]
    public void 不正な入力(string text)
    {
        Assert.False(TimeInput.TryParseSeconds(text, out _));
    }
}

public class TextTailTests
{
    [Fact]
    public void 最後のN行だけ残す()
    {
        var t = new TextTail(3);
        for (int i = 1; i <= 5; i++)
            t.Add($"line{i}");
        Assert.Equal(string.Join(Environment.NewLine, "line3", "line4", "line5"), t.ToString());
    }
}
