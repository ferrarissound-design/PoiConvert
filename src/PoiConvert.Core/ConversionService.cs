namespace PoiConvert.Core;

public enum ConversionOutcome
{
    Succeeded,
    Failed,
    Canceled,
}

public sealed record ConversionResult(ConversionOutcome Outcome, string? OutputPath, string Message, string? ErrorTail);

public sealed class OutputSettings
{
    public bool ToSourceFolder { get; init; } = true;
    public string? Folder { get; init; }
}

/// <summary>
/// 1ファイルを変換する：出力名の予約 → コマンド組み立て → ffmpeg 実行（進捗通知）→ 後片付け。
/// 失敗・キャンセル時は作りかけの出力ファイルと一時ファイルを消す。
/// </summary>
public sealed class ConversionService
{
    private readonly FfmpegPaths _ffmpeg;
    private readonly OutputPathResolver _resolver;
    private readonly string _tempDirectory;

    public ConversionService(FfmpegPaths ffmpeg, OutputPathResolver? resolver = null, string? tempDirectory = null)
    {
        _ffmpeg = ffmpeg;
        _resolver = resolver ?? new OutputPathResolver();
        _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "PoiConvert");
    }

    public async Task<MediaInfo> ProbeAsync(string inputPath, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(_ffmpeg.Ffprobe, FfprobeParser.BuildArguments(inputPath),
            null, captureStdout: true, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var tail = string.IsNullOrWhiteSpace(result.StderrTail) ? "" : $"（{LastLine(result.StderrTail)}）";
            throw new ConversionException($"動画・音声ファイルとして読み込めませんでした{tail}");
        }
        try
        {
            return FfprobeParser.Parse(result.StandardOutput);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ConversionException("ffprobe の出力を解析できませんでした。");
        }
    }

    public async Task<ConversionResult> ConvertAsync(
        string inputPath,
        MediaInfo media,
        ConversionOptions options,
        OutputSettings output,
        IProgress<double>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        inputPath = Path.GetFullPath(inputPath);
        string? outputPath = null;
        ConversionPlan? plan = null;
        bool outputCreatedByUs = false;
        bool startedWriting = false;
        string? lastTail = null;

        try
        {
            var dir = output.ToSourceFolder || string.IsNullOrWhiteSpace(output.Folder)
                ? Path.GetDirectoryName(inputPath)!
                : Path.GetFullPath(output.Folder);
            Directory.CreateDirectory(dir);

            var ext = FfmpegCommandBuilder.GetOutputExtension(options, media);
            var baseName = FfmpegCommandBuilder.GetOutputBaseName(inputPath, options);
            outputPath = _resolver.Reserve(dir, baseName, ext);

            Directory.CreateDirectory(_tempDirectory);
            var tempPrefix = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
            plan = FfmpegCommandBuilder.Build(inputPath, outputPath, media, options, tempPrefix);

            foreach (var note in plan.Notes)
                log?.Invoke(note);

            double done = 0;
            foreach (var step in plan.Steps)
            {
                ct.ThrowIfCancellationRequested();
                // 実行直前にもう一度確認：誰かが同名ファイルを作っていたら触らずに中止（上書き・削除は絶対しない）
                if (!outputCreatedByUs && File.Exists(outputPath))
                {
                    CleanupTemp(plan);
                    return new ConversionResult(ConversionOutcome.Failed, null,
                        "同名のファイルが直前に作られたため、上書きせずに中止しました。もう一度実行してください。", null);
                }
                log?.Invoke($"▶ {step.Description}");
                startedWriting = true;
                double stepBase = done;
                var result = await ProcessRunner.RunAsync(_ffmpeg.Ffmpeg, step.Arguments, line =>
                {
                    var t = ProgressParser.TryParseOutTime(line);
                    if (t is not null)
                        progress?.Report(stepBase + step.Weight * ProgressParser.ToFraction(t.Value, step.ExpectedDurationSeconds));
                }, captureStdout: false, ct).ConfigureAwait(false);

                // ffmpeg が出力ファイルを作り始めたら「自分が作ったもの」とみなす（-n なので既存は触らない）
                // ただし「already exists」で拒否された場合は他人のファイルなので絶対に消さない
                if (File.Exists(outputPath) && !IsAlreadyExistsError(result.StderrTail))
                    outputCreatedByUs = true;

                lastTail = result.StderrTail;
                if (result.ExitCode != 0)
                {
                    Cleanup(plan, outputPath, outputCreatedByUs);
                    return new ConversionResult(ConversionOutcome.Failed, null,
                        ExplainFailure(options, media, result.StderrTail), result.StderrTail);
                }
                done += step.Weight;
                progress?.Report(done);
            }

            if (!File.Exists(outputPath))
            {
                Cleanup(plan, outputPath, false);
                return new ConversionResult(ConversionOutcome.Failed, null, "出力ファイルが作成されませんでした。", lastTail);
            }

            CleanupTemp(plan);
            progress?.Report(1.0);
            return new ConversionResult(ConversionOutcome.Succeeded, outputPath, "完了", lastTail);
        }
        catch (OperationCanceledException)
        {
            // 実行中にキャンセルした場合、事前確認で存在しなかった出力は ffmpeg が作ったもの
            if (outputPath is not null && startedWriting && File.Exists(outputPath))
                outputCreatedByUs = true;
            if (plan is not null)
                Cleanup(plan, outputPath, outputCreatedByUs);
            return new ConversionResult(ConversionOutcome.Canceled, null, "キャンセルしました", lastTail);
        }
        catch (ConversionException ex)
        {
            if (plan is not null)
                Cleanup(plan, outputPath, outputCreatedByUs);
            return new ConversionResult(ConversionOutcome.Failed, null, ex.Message, lastTail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (plan is not null)
                Cleanup(plan, outputPath, outputCreatedByUs);
            return new ConversionResult(ConversionOutcome.Failed, null, $"ファイルの読み書きに失敗しました：{ex.Message}", lastTail);
        }
        finally
        {
            if (outputPath is not null)
                _resolver.Release(outputPath);
        }
    }

    /// <summary>よくある失敗を分かりやすい日本語にする。</summary>
    internal static string ExplainFailure(ConversionOptions options, MediaInfo media, string stderrTail)
    {
        var t = stderrTail ?? "";
        if (options.Mode == ConversionMode.AudioExtract && options.Audio.Format == AudioFormat.Copy)
            return $"「元のまま」で取り出せませんでした（音声形式：{media.AudioCodec ?? "不明"}）。MP3 / M4A / WAV を選ぶと変換できます。";
        if (options.Mode == ConversionMode.Convert && options.Convert.PreferStreamCopy &&
            (t.Contains("codec not currently supported in container", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("Could not find tag for codec", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
            return "再エンコードなしではこの形式に入りません。「再エンコードしない」をオフにして試してください。";
        if (t.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "同名のファイルが直前に作られたため、上書きせずに中止しました。もう一度実行してください。";
        if (t.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            return "ディスクの空き容量が足りません。";
        if (t.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "出力先に書き込む権限がありません。";
        return "変換に失敗しました（詳細はログ欄を見てください）。";
    }

    private static bool IsAlreadyExistsError(string? tail)
        => tail is not null && tail.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    private void Cleanup(ConversionPlan plan, string? outputPath, bool deleteOutput)
    {
        if (deleteOutput && outputPath is not null)
            TryDelete(outputPath);
        CleanupTemp(plan);
    }

    private static void CleanupTemp(ConversionPlan plan)
    {
        foreach (var f in plan.TempFiles)
            TryDelete(f);
        if (plan.PassLogPrefix is { } prefix)
        {
            try
            {
                var dir = Path.GetDirectoryName(prefix)!;
                var name = Path.GetFileName(prefix);
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir, name + "*"))
                        TryDelete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        // ffmpeg 終了直後はファイルが掴まれていることがあるので少しだけ再試行
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    private static string LastLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
