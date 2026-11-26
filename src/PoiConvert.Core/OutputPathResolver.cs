namespace PoiConvert.Core;

/// <summary>
/// 出力ファイル名を決める。既存ファイルは絶対に上書きしないよう、
/// 「名前.ext」「名前_1.ext」「名前_2.ext」…と空いている名前を探す。
/// 同時変換中の別ジョブとぶつからないよう、予約済みの名前も避ける。
/// </summary>
public sealed class OutputPathResolver
{
    private readonly Func<string, bool> _exists;
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public OutputPathResolver() : this(p => File.Exists(p) || Directory.Exists(p)) { }

    public OutputPathResolver(Func<string, bool> exists)
    {
        _exists = exists;
    }

    /// <summary>
    /// "C:\dir", "movie_small", ".mp4" → "C:\dir\movie_small.mp4"（使われていれば _1, _2 …）。
    /// 返したパスは <see cref="Release"/> されるまで予約される。
    /// </summary>
    public string Reserve(string directory, string baseName, string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;

        lock (_lock)
        {
            for (int i = 0; i < 100_000; i++)
            {
                var name = i == 0 ? baseName + extension : $"{baseName}_{i}{extension}";
                var path = Path.Combine(directory, name);
                if (_reserved.Contains(path) || _exists(path))
                    continue;
                _reserved.Add(path);
                return path;
            }
        }
        throw new ConversionException("出力ファイル名の連番が上限に達しました。");
    }

    public void Release(string path)
    {
        lock (_lock)
        {
            _reserved.Remove(path);
        }
    }

    /// <summary>モードごとの「元の名前」の後ろに付ける文字列。</summary>
    public static string GetSuffix(ConversionMode mode) => mode switch
    {
        ConversionMode.AudioExtract => "_audio",
        ConversionMode.Compress => "_small",
        ConversionMode.Gif => "",
        ConversionMode.Convert => "",
        _ => "",
    };
}
