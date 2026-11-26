using System.Globalization;
using System.Text.Json;

namespace PoiConvert.Core;

/// <summary>
/// ffprobe -print_format json -show_format -show_streams の出力を解析する。
/// </summary>
public static class FfprobeParser
{
    public static IReadOnlyList<string> BuildArguments(string inputPath) => new[]
    {
        "-v", "error",
        "-print_format", "json",
        "-show_format",
        "-show_streams",
        inputPath,
    };

    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double? duration = null;
        long? size = null;
        string? formatName = null;

        if (root.TryGetProperty("format", out var format))
        {
            duration = ReadDouble(format, "duration");
            size = ReadLong(format, "size");
            formatName = ReadString(format, "format_name");
        }

        string? videoCodec = null, audioCodec = null;
        int? width = null, height = null;
        int rotation = 0;
        double? videoStreamDuration = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = ReadString(s, "codec_type");
                if (type == "video" && videoCodec is null)
                {
                    // MP3 のジャケット画像などは「映像」扱いしない
                    if (IsAttachedPicture(s))
                        continue;
                    videoCodec = ReadString(s, "codec_name") ?? "unknown";
                    width = (int?)ReadLong(s, "width");
                    height = (int?)ReadLong(s, "height");
                    rotation = ReadRotation(s);
                    videoStreamDuration = ReadDouble(s, "duration");
                }
                else if (type == "audio" && audioCodec is null)
                {
                    audioCodec = ReadString(s, "codec_name") ?? "unknown";
                }
            }
        }

        return new MediaInfo
        {
            DurationSeconds = duration ?? videoStreamDuration,
            SizeBytes = size,
            FormatName = formatName,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            Width = width,
            Height = height,
            Rotation = rotation,
        };
    }

    private static bool IsAttachedPicture(JsonElement stream)
    {
        if (stream.TryGetProperty("disposition", out var disp) &&
            disp.TryGetProperty("attached_pic", out var ap) &&
            ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1)
            return true;
        return false;
    }

    private static int ReadRotation(JsonElement stream)
    {
        // 新しい ffprobe: side_data_list[].rotation（例: -90）
        if (stream.TryGetProperty("side_data_list", out var sdl) && sdl.ValueKind == JsonValueKind.Array)
        {
            foreach (var sd in sdl.EnumerateArray())
            {
                var r = ReadDouble(sd, "rotation");
                if (r is not null)
                    return (int)Math.Round(r.Value);
            }
        }
        // 古い ffprobe: tags.rotate（例: "90"）
        if (stream.TryGetProperty("tags", out var tags))
        {
            var r = ReadDouble(tags, "rotate");
            if (r is not null)
                return (int)Math.Round(r.Value);
        }
        return 0;
    }

    private static string? ReadString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double? ReadDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) &&
            !double.IsNaN(d) && !double.IsInfinity(d))
            return d;
        return null;
    }

    private static long? ReadLong(JsonElement e, string name)
    {
        var d = ReadDouble(e, name);
        return d is null ? null : (long)d.Value;
    }
}
