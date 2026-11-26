using System.Globalization;

namespace PoiConvert.Core;

/// <summary>ffmpeg -progress pipe:1 の出力（key=value 行）を解析する。</summary>
public static class ProgressParser
{
    /// <summary>
    /// 進捗行から出力済みの時間を取り出す。対象外の行や N/A のときは null。
    /// out_time_us / out_time_ms（実際はどちらもマイクロ秒）/ out_time=HH:MM:SS.ffffff に対応。
    /// </summary>
    public static TimeSpan? TryParseOutTime(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return null;
        int eq = line.IndexOf('=');
        if (eq <= 0)
            return null;
        var key = line.AsSpan(0, eq).Trim();
        var value = line.AsSpan(eq + 1).Trim();

        if (key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                return TimeSpan.FromTicks(us * 10);
            return null;
        }

        if (key.SequenceEqual("out_time"))
        {
            // 例: 00:01:23.456789 / -00:00:00.000000 / N/A
            var s = value.ToString();
            if (s.StartsWith('-'))
                return TimeSpan.Zero;
            var parts = s.Split(':');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                return TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
            return null;
        }
        return null;
    }

    /// <summary>"progress=end" 行か。</summary>
    public static bool IsEnd(string? line) => line?.Trim() == "progress=end";

    /// <summary>出力済み時間と全体の長さから 0〜1 の割合を出す。</summary>
    public static double ToFraction(TimeSpan outTime, double? durationSeconds)
    {
        if (durationSeconds is not { } d || d <= 0)
            return 0;
        var f = outTime.TotalSeconds / d;
        return Math.Clamp(f, 0, 1);
    }
}
