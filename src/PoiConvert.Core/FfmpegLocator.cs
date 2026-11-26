namespace PoiConvert.Core;

public enum FfmpegSource
{
    AppFolder,
    Settings,
    PathEnvironment,
}

public sealed record FfmpegPaths(string Ffmpeg, string Ffprobe, FfmpegSource Source);

/// <summary>
/// ffmpeg / ffprobe を探す。順番：① exe と同じフォルダ ② 設定で指定したパス ③ 環境変数 PATH。
/// ffmpeg と ffprobe は「同じ場所に両方そろっている」ものだけを採用する。
/// </summary>
public sealed class FfmpegLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly bool _isWindows;

    public FfmpegLocator() : this(File.Exists, OperatingSystem.IsWindows()) { }

    public FfmpegLocator(Func<string, bool> fileExists, bool isWindows)
    {
        _fileExists = fileExists;
        _isWindows = isWindows;
    }

    public string FfmpegFileName => _isWindows ? "ffmpeg.exe" : "ffmpeg";
    public string FfprobeFileName => _isWindows ? "ffprobe.exe" : "ffprobe";

    /// <param name="appDirectory">PoiConvert.exe のあるフォルダ</param>
    /// <param name="configuredPath">設定のパス（ffmpeg.exe そのもの、またはそれが入ったフォルダ）</param>
    /// <param name="pathEnvironment">環境変数 PATH の値</param>
    public FfmpegPaths? Locate(string? appDirectory, string? configuredPath, string? pathEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(appDirectory) && TryDirectory(appDirectory, FfmpegSource.AppFolder) is { } a)
            return a;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = configuredPath.Trim().Trim('"');
            var dir = LooksLikeFfmpegFile(p) ? Path.GetDirectoryName(p) : p;
            if (!string.IsNullOrEmpty(dir) && TryDirectory(dir, FfmpegSource.Settings) is { } s)
                return s;
        }

        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            var sep = _isWindows ? ';' : ':';
            foreach (var raw in pathEnvironment.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0)
                    continue;
                if (TryDirectory(dir, FfmpegSource.PathEnvironment) is { } e)
                    return e;
            }
        }
        return null;
    }

    private bool LooksLikeFfmpegFile(string p)
    {
        var name = Path.GetFileName(p);
        return string.Equals(name, FfmpegFileName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, FfprobeFileName, StringComparison.OrdinalIgnoreCase)
               || _fileExists(p);
    }

    private FfmpegPaths? TryDirectory(string dir, FfmpegSource source)
    {
        try
        {
            var ffmpeg = Path.Combine(dir, FfmpegFileName);
            var ffprobe = Path.Combine(dir, FfprobeFileName);
            if (_fileExists(ffmpeg) && _fileExists(ffprobe))
                return new FfmpegPaths(ffmpeg, ffprobe, source);
        }
        catch (ArgumentException)
        {
            // PATH に不正な文字が入っていても無視して次へ
        }
        return null;
    }
}
