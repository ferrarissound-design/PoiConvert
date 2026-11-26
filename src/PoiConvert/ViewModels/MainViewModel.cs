using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using PoiConvert.Core;

namespace PoiConvert.ViewModels;

/// <summary>画面側（MainWindow）が実装するダイアログ類。</summary>
public interface IUiServices
{
    void ShowError(string message);
    void ShowInfo(string message);
    bool Confirm(string message);
    /// <summary>ffmpeg が見つからないときの案内。ユーザーが指定したパス（なければ null）を返す。</summary>
    string? ShowFfmpegMissing(string? currentPath);
    string? PickFolder(string? initial);
    string[]? PickFiles();
    void RevealInExplorer(string path);
}

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] MediaExtensions =
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp",
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".gif",
    };

    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly OutputPathResolver _resolver = new();
    private readonly SemaphoreSlim _probeLimit = new(3);
    private readonly StringBuilder _log = new();
    private IUiServices? _uiServices;
    private CancellationTokenSource? _cts;
    private List<QueueItem> _currentTargets = new();

    public MainViewModel(SettingsStore store, Dispatcher dispatcher)
    {
        _store = store;
        _settings = store.Load();
        _ui = dispatcher;

        var o = _settings.Options;
        _mode = o.Mode;
        _audioFormat = o.Audio.Format;
        _audioBitrate = o.Audio.BitrateKbps;
        _quality = o.Compress.Quality;
        _resolution = o.Compress.Resolution;
        _useTargetSize = o.Compress.UseTargetSize;
        _targetSizeText = o.Compress.TargetSizeMB.ToString("0.##", CultureInfo.InvariantCulture);
        _gifStartText = FfmpegCommandBuilder.Fmt(o.Gif.StartSeconds);
        _gifDurationText = FfmpegCommandBuilder.Fmt(o.Gif.DurationSeconds);
        _gifFps = o.Gif.Fps;
        _gifWidth = o.Gif.Width;
        _container = o.Convert.Container;
        _preferCopy = o.Convert.PreferStreamCopy;
        _x264Preset = o.X264Preset;
        _outputToSourceFolder = _settings.OutputToSourceFolder || string.IsNullOrWhiteSpace(_settings.OutputFolder);
        _outputFolder = _settings.OutputFolder ?? "";
        _concurrency = _settings.Concurrency;

        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoItems));

        AddFilesCommand = new RelayCommand(AddFilesFromDialog);
        ClearAllCommand = new RelayCommand(ClearAll, () => IsIdle && Items.Count > 0);
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => IsIdle && Items.Count > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        BrowseOutputFolderCommand = new RelayCommand(BrowseOutputFolder);
        ChangeFfmpegCommand = new RelayCommand(() => ShowFfmpegDialog());
        ClearLogCommand = new RelayCommand(() => { _log.Clear(); OnPropertyChanged(nameof(LogText)); });

        LocateFfmpeg();
    }

    public void AttachUi(IUiServices ui) => _uiServices = ui;
    private IUiServices Ui => _uiServices ?? throw new InvalidOperationException("UI が未設定です");

    // ───────────────────────────── 一覧

    public ObservableCollection<QueueItem> Items { get; } = new();
    public bool HasNoItems => Items.Count == 0;

    public ICommand AddFilesCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand BrowseOutputFolderCommand { get; }
    public ICommand ChangeFfmpegCommand { get; }
    public ICommand ClearLogCommand { get; }

    private void AddFilesFromDialog()
    {
        var files = Ui.PickFiles();
        if (files is not null)
            AddPaths(files);
    }

    /// <summary>D&amp;D やダイアログから受け取ったパスを追加する（フォルダなら中のメディアファイル）。</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(Items.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var raw in paths)
        {
            foreach (var path in Expand(raw))
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch { continue; }
                if (!existing.Add(full))
                    continue;
                var item = new QueueItem(full);
                Items.Add(item);
                StartProbe(item);
                added++;
            }
        }
        if (added == 0 && paths.Any())
            AppendLog("追加できるファイルがありませんでした（同じファイルは二重に追加しません）。");
    }

    private static IEnumerable<string> Expand(string path)
    {
        if (File.Exists(path))
            return new[] { path };
        if (Directory.Exists(path))
        {
            try
            {
                return Directory.EnumerateFiles(path)
                    .Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
        return Array.Empty<string>();
    }

    private void StartProbe(QueueItem item)
    {
        if (_ffmpeg is null)
        {
            item.Status = ItemStatus.Ready;
            return;
        }
        item.Status = ItemStatus.Probing;
        item.ProbeTask = ProbeAsync(item, new ConversionService(_ffmpeg, _resolver));
    }

    private async Task ProbeAsync(QueueItem item, ConversionService svc)
    {
        await _probeLimit.WaitAsync();
        try
        {
            item.Media = await svc.ProbeAsync(item.FilePath, CancellationToken.None);
            if (item.Status == ItemStatus.Probing)
                item.Status = ItemStatus.Ready;
        }
        catch (ConversionException ex)
        {
            item.Message = ex.Message;
            item.Status = ItemStatus.ProbeFailed;
            AppendLog($"[{item.FileName}] {ex.Message}");
        }
        catch (Exception ex)
        {
            item.Message = ex.Message;
            item.Status = ItemStatus.ProbeFailed;
            AppendLog($"[{item.FileName}] 解析に失敗しました：{ex.Message}");
        }
        finally
        {
            _probeLimit.Release();
        }
    }

    public void RemoveItems(IEnumerable<QueueItem> items)
    {
        if (IsRunning)
            return;
        foreach (var i in items.ToList())
            Items.Remove(i);
    }

    private void ClearAll()
    {
        if (IsRunning)
            return;
        Items.Clear();
        OverallProgress = 0;
        OverallText = "";
    }

    // ───────────────────────────── モード・オプション

    private ConversionMode _mode;
    public int SelectedModeIndex
    {
        get => (int)_mode;
        set
        {
            var m = (ConversionMode)value;
            if (_mode == m) return;
            _mode = m;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice<AudioFormat>> AudioFormatChoices { get; } = new[]
    {
        new Choice<AudioFormat>("MP3", AudioFormat.Mp3),
        new Choice<AudioFormat>("M4A (AAC)", AudioFormat.M4a),
        new Choice<AudioFormat>("WAV（無圧縮）", AudioFormat.Wav),
        new Choice<AudioFormat>("元のまま（再エンコードなし）", AudioFormat.Copy),
    };

    private AudioFormat _audioFormat;
    public AudioFormat AudioFormat
    {
        get => _audioFormat;
        set
        {
            if (Set(ref _audioFormat, value))
                OnPropertyChanged(nameof(IsAudioBitrateEnabled));
        }
    }

    public bool IsAudioBitrateEnabled => AudioFormat is AudioFormat.Mp3 or AudioFormat.M4a;

    public IReadOnlyList<Choice<int>> AudioBitrateChoices { get; } =
        ConversionOptions.AudioBitrates.Select(b => new Choice<int>($"{b} kbps", b)).ToArray();

    private int _audioBitrate;
    public int AudioBitrate { get => _audioBitrate; set => Set(ref _audioBitrate, value); }

    public IReadOnlyList<Choice<QualityPreset>> QualityChoices { get; } = new[]
    {
        new Choice<QualityPreset>("高画質（CRF 20）", QualityPreset.High),
        new Choice<QualityPreset>("標準（CRF 23）", QualityPreset.Standard),
        new Choice<QualityPreset>("軽量（CRF 28）", QualityPreset.Light),
    };

    private QualityPreset _quality;
    public QualityPreset Quality { get => _quality; set => Set(ref _quality, value); }

    public IReadOnlyList<Choice<ResolutionLimit>> ResolutionChoices { get; } = new[]
    {
        new Choice<ResolutionLimit>("元のまま", ResolutionLimit.Original),
        new Choice<ResolutionLimit>("1080p", ResolutionLimit.P1080),
        new Choice<ResolutionLimit>("720p", ResolutionLimit.P720),
        new Choice<ResolutionLimit>("480p", ResolutionLimit.P480),
    };

    private ResolutionLimit _resolution;
    public ResolutionLimit Resolution { get => _resolution; set => Set(ref _resolution, value); }

    private bool _useTargetSize;
    public bool UseTargetSize
    {
        get => _useTargetSize;
        set
        {
            if (Set(ref _useTargetSize, value))
                OnPropertyChanged(nameof(UseQualityPreset));
        }
    }

    public bool UseQualityPreset
    {
        get => !UseTargetSize;
        set => UseTargetSize = !value;
    }

    private string _targetSizeText;
    public string TargetSizeText { get => _targetSizeText; set => Set(ref _targetSizeText, value); }

    private string _gifStartText;
    public string GifStartText { get => _gifStartText; set => Set(ref _gifStartText, value); }

    private string _gifDurationText;
    public string GifDurationText
    {
        get => _gifDurationText;
        set
        {
            if (Set(ref _gifDurationText, value))
                OnPropertyChanged(nameof(GifWarning));
        }
    }

    public string? GifWarning =>
        TimeInput.TryParseSeconds(GifDurationText, out var d) && d > ConversionOptions.GifWarnDurationSeconds
            ? $"⚠ {ConversionOptions.GifWarnDurationSeconds:0}秒を超える GIF はファイルがとても大きくなります（数十〜数百MB）。"
            : null;

    public IReadOnlyList<Choice<int>> GifFpsChoices { get; } =
        ConversionOptions.GifFpsChoices.Select(f => new Choice<int>($"{f} fps", f)).ToArray();

    private int _gifFps;
    public int GifFps { get => _gifFps; set => Set(ref _gifFps, value); }

    public IReadOnlyList<Choice<int>> GifWidthChoices { get; } =
        ConversionOptions.GifWidthChoices.Select(w => new Choice<int>(w == 0 ? "元のまま" : $"{w} px", w)).ToArray();

    private int _gifWidth;
    public int GifWidth { get => _gifWidth; set => Set(ref _gifWidth, value); }

    public IReadOnlyList<Choice<ContainerFormat>> ContainerChoices { get; } = new[]
    {
        new Choice<ContainerFormat>("MP4", ContainerFormat.Mp4),
        new Choice<ContainerFormat>("MOV", ContainerFormat.Mov),
        new Choice<ContainerFormat>("MKV", ContainerFormat.Mkv),
        new Choice<ContainerFormat>("WEBM", ContainerFormat.Webm),
    };

    private ContainerFormat _container;
    public ContainerFormat Container { get => _container; set => Set(ref _container, value); }

    private bool _preferCopy;
    public bool PreferCopy { get => _preferCopy; set => Set(ref _preferCopy, value); }

    // ───────────────────────────── 出力・設定

    private bool _outputToSourceFolder;
    public bool OutputToSourceFolder
    {
        get => _outputToSourceFolder;
        set
        {
            if (Set(ref _outputToSourceFolder, value))
                OnPropertyChanged(nameof(OutputToCustomFolder));
        }
    }

    public bool OutputToCustomFolder
    {
        get => !OutputToSourceFolder;
        set => OutputToSourceFolder = !value;
    }

    private string _outputFolder;
    public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, value); }

    private void BrowseOutputFolder()
    {
        var f = Ui.PickFolder(string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder);
        if (f is not null)
        {
            OutputFolder = f;
            OutputToCustomFolder = true;
        }
    }

    public IReadOnlyList<Choice<int>> ConcurrencyChoices { get; } = new[]
    {
        new Choice<int>("1（順番に）", 1),
        new Choice<int>("2（同時に2つ）", 2),
    };

    private int _concurrency;
    public int Concurrency { get => _concurrency; set => Set(ref _concurrency, value); }

    public IReadOnlyList<Choice<string>> X264PresetChoices { get; } = ConversionOptions.X264Presets
        .Select(p => new Choice<string>(p switch
        {
            "ultrafast" => "ultrafast（最速・大きめ）",
            "veryfast" => "veryfast（速い・おすすめ）",
            "medium" => "medium（標準）",
            "veryslow" => "veryslow（最遅・小さめ）",
            _ => p,
        }, p)).ToArray();

    private string _x264Preset;
    public string X264Preset { get => _x264Preset; set => Set(ref _x264Preset, value); }

    // ───────────────────────────── FFmpeg

    private FfmpegPaths? _ffmpeg;

    private string _ffmpegStatus = "";
    public string FfmpegStatus { get => _ffmpegStatus; private set => Set(ref _ffmpegStatus, value); }

    public bool IsFfmpegMissing => _ffmpeg is null;

    private void LocateFfmpeg()
    {
        _ffmpeg = new FfmpegLocator().Locate(AppContext.BaseDirectory, _settings.FfmpegPath,
            Environment.GetEnvironmentVariable("PATH"));
        FfmpegStatus = _ffmpeg is null
            ? "FFmpeg が見つかりません"
            : $"{_ffmpeg.Ffmpeg}（{_ffmpeg.Source switch
            {
                FfmpegSource.AppFolder => "exe と同じフォルダ",
                FfmpegSource.Settings => "設定で指定",
                _ => "PATH",
            }}）";
        OnPropertyChanged(nameof(IsFfmpegMissing));
    }

    /// <summary>起動時に呼ぶ。見つからなければ案内ダイアログ。</summary>
    public void CheckFfmpegOnStartup()
    {
        if (_ffmpeg is null)
            ShowFfmpegDialog();
    }

    private bool ShowFfmpegDialog()
    {
        var picked = Ui.ShowFfmpegMissing(_settings.FfmpegPath);
        if (picked is null)
            return _ffmpeg is not null;

        // 指定された場所だけを確認する
        if (new FfmpegLocator().Locate(null, picked, null) is null)
        {
            Ui.ShowError("指定した場所に ffmpeg.exe と ffprobe.exe の両方が見つかりませんでした。\n2つを同じフォルダに置いてから指定してください。");
            return _ffmpeg is not null;
        }

        _settings.FfmpegPath = picked;
        LocateFfmpeg();
        SaveSettings();
        AppendLog($"FFmpeg: {FfmpegStatus}");
        // まだ解析できていないファイルを解析し直す
        foreach (var i in Items.Where(i => i.Media is null && i.Status is ItemStatus.Ready or ItemStatus.ProbeFailed).ToList())
            StartProbe(i);
        return _ffmpeg is not null;
    }

    // ───────────────────────────── 実行

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsIdle => !IsRunning;

    private double _overallProgress;
    public double OverallProgress { get => _overallProgress; private set => Set(ref _overallProgress, value); }

    private string _overallText = "";
    public string OverallText { get => _overallText; private set => Set(ref _overallText, value); }

    private string? _lastOutputPath;

    /// <summary>実行中のタスク（終了時に待つため）。</summary>
    public Task? RunningTask { get; private set; }

    private bool TryBuildOptions(out ConversionOptions options, out string? error)
    {
        error = null;
        options = new ConversionOptions
        {
            Mode = _mode,
            X264Preset = X264Preset,
            Audio = new AudioOptions { Format = AudioFormat, BitrateKbps = AudioBitrate },
            Compress = new CompressOptions { Quality = Quality, Resolution = Resolution, UseTargetSize = UseTargetSize },
            Gif = new GifOptions { Fps = GifFps, Width = GifWidth },
            Convert = new ConvertOptions { Container = Container, PreferStreamCopy = PreferCopy },
        };

        if (UseTargetSize || _mode == ConversionMode.Compress)
        {
            var t = (TargetSizeText ?? "").Trim().Normalize(NormalizationForm.FormKC);
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) && mb > 0)
                options.Compress.TargetSizeMB = mb;
            else if (_mode == ConversionMode.Compress && UseTargetSize)
            {
                error = "目標サイズ（MB）には 0 より大きい数字を入れてください。";
                return false;
            }
        }

        if (TimeInput.TryParseSeconds(GifStartText, out var start))
            options.Gif.StartSeconds = start;
        else if (_mode == ConversionMode.Gif)
        {
            error = "GIF の開始時間は「12.5」（秒）や「1:23」（分:秒）の形で入れてください。";
            return false;
        }

        if (TimeInput.TryParseSeconds(GifDurationText, out var dur) && dur > 0)
            options.Gif.DurationSeconds = dur;
        else if (_mode == ConversionMode.Gif)
        {
            error = "GIF の長さ（秒）には 0 より大きい数字を入れてください。";
            return false;
        }
        return true;
    }

    public void SaveSettings()
    {
        TryBuildOptions(out var o, out _);
        _settings.Options = o;
        _settings.OutputToSourceFolder = OutputToSourceFolder;
        _settings.OutputFolder = string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder;
        _settings.Concurrency = Concurrency;
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendLog($"設定を保存できませんでした：{ex.Message}");
        }
    }

    private async Task StartAsync()
    {
        if (IsRunning)
            return;

        if (_ffmpeg is null && !ShowFfmpegDialog())
            return;
        var ffmpeg = _ffmpeg!;

        if (!TryBuildOptions(out var options, out var error))
        {
            Ui.ShowError(error!);
            return;
        }

        if (OutputToCustomFolder && string.IsNullOrWhiteSpace(OutputFolder))
        {
            Ui.ShowError("出力先フォルダを指定してください（または「元ファイルと同じフォルダ」を選んでください）。");
            return;
        }

        var targets = Items.Where(i => i.Status is not (ItemStatus.Done or ItemStatus.Running)).ToList();
        if (targets.Count == 0)
        {
            Ui.ShowInfo("変換するファイルがありません。\n（「完了」のファイルは再変換しません。もう一度変換したい場合は一覧から削除して追加し直してください）");
            return;
        }

        if (options.Mode == ConversionMode.Gif && options.Gif.DurationSeconds > ConversionOptions.GifWarnDurationSeconds &&
            !Ui.Confirm($"GIF の長さが {FfmpegCommandBuilder.Fmt(options.Gif.DurationSeconds)} 秒あります。\n" +
                        $"{ConversionOptions.GifWarnDurationSeconds:0}秒を超える GIF はファイルがとても大きくなり、時間もかかります。\n\nこのまま変換しますか？"))
            return;

        SaveSettings();

        var output = new OutputSettings { ToSourceFolder = OutputToSourceFolder, Folder = OutputFolder };
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        _currentTargets = targets;
        foreach (var t in targets)
        {
            t.Progress = 0;
            t.Message = null;
            t.OutputPath = null;
            t.Status = ItemStatus.Queued;
        }
        UpdateOverall();
        AppendLog($"──── 変換開始：{targets.Count} 件（{ModeName(options.Mode)}）────");

        var queue = new ConcurrentQueue<QueueItem>(targets);
        var service = new ConversionService(ffmpeg, _resolver);
        var workers = Enumerable.Range(0, Math.Clamp(Concurrency, 1, 2))
            .Select(_ => WorkerAsync(queue, service, options, output, ct))
            .ToList();

        RunningTask = Task.WhenAll(workers);
        try
        {
            await RunningTask;
        }
        finally
        {
            int ok = targets.Count(t => t.Status == ItemStatus.Done);
            int ng = targets.Count(t => t.Status == ItemStatus.Failed);
            int cancel = targets.Count(t => t.Status == ItemStatus.Canceled);
            AppendLog($"──── 終了：完了 {ok} / 失敗 {ng} / キャンセル {cancel} ────");
            OverallText = $"完了 {ok} 件・失敗 {ng} 件" + (cancel > 0 ? $"・キャンセル {cancel} 件" : "");
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            RunningTask = null;
        }
    }

    private async Task WorkerAsync(ConcurrentQueue<QueueItem> queue, ConversionService service,
        ConversionOptions options, OutputSettings output, CancellationToken ct)
    {
        while (queue.TryDequeue(out var item))
        {
            if (ct.IsCancellationRequested)
            {
                item.Status = ItemStatus.Canceled;
                UpdateOverall();
                continue;
            }
            await ProcessItemAsync(item, service, options, output, ct);
            UpdateOverall();
        }
    }

    private async Task ProcessItemAsync(QueueItem item, ConversionService service,
        ConversionOptions options, OutputSettings output, CancellationToken ct)
    {
        item.Status = ItemStatus.Running;
        var name = item.FileName;
        try
        {
            if (item.ProbeTask is { IsCompleted: false } pt)
                await pt;
            if (item.Media is null)
            {
                try
                {
                    item.Media = await service.ProbeAsync(item.FilePath, ct);
                }
                catch (ConversionException ex)
                {
                    Fail(item, ex.Message, null);
                    return;
                }
            }

            AppendLog($"[{name}] 開始");
            // Progress<T> は UI スレッドに戻して通知してくれる
            var progress = new Progress<double>(p =>
            {
                if (item.Status != ItemStatus.Running) return;
                item.Progress = Math.Round(p * 100, 1);
                UpdateOverall();
            });
            var result = await service.ConvertAsync(item.FilePath, item.Media, options, output, progress,
                msg => AppendLog($"[{name}] {msg}"), ct);

            switch (result.Outcome)
            {
                case ConversionOutcome.Succeeded:
                    item.Status = ItemStatus.Done;
                    item.Progress = 100;
                    item.OutputPath = result.OutputPath;
                    item.Message = result.OutputPath;
                    _lastOutputPath = result.OutputPath;
                    AppendLog($"[{name}] 完了 → {result.OutputPath}");
                    break;
                case ConversionOutcome.Canceled:
                    item.Status = ItemStatus.Canceled;
                    item.Message = "キャンセルしました（作りかけのファイルは削除済み）";
                    AppendLog($"[{name}] キャンセルしました（作りかけの出力は削除しました）");
                    break;
                default:
                    Fail(item, result.Message, result.ErrorTail);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            item.Status = ItemStatus.Canceled;
        }
        catch (Exception ex)
        {
            Fail(item, "予期しないエラー：" + ex.Message, null);
        }
    }

    private void Fail(QueueItem item, string message, string? tail)
    {
        item.Status = ItemStatus.Failed;
        item.Message = message;
        AppendLog($"[{item.FileName}] 失敗：{message}");
        if (!string.IsNullOrWhiteSpace(tail))
            AppendLog($"---- ffmpeg のエラー出力（末尾）----{Environment.NewLine}{tail}{Environment.NewLine}------------------------------------");
    }

    private void UpdateOverall()
    {
        var t = _currentTargets;
        if (t.Count == 0)
            return;
        double sum = t.Sum(i => i.IsFinished ? 100 : i.Progress);
        OverallProgress = sum / t.Count;
        int finished = t.Count(i => i.IsFinished);
        if (IsRunning)
            OverallText = $"{finished} / {t.Count} 件　{OverallProgress:0}%";
    }

    private void Cancel()
    {
        if (_cts is null || _cts.IsCancellationRequested)
            return;
        AppendLog("キャンセルしています…");
        // kill の後始末で UI を止めないよう別スレッドで
        var cts = _cts;
        Task.Run(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
    }

    /// <summary>ウィンドウを閉じる前に呼ぶ：実行中ならキャンセルして終わるまで待つ。</summary>
    public async Task ShutdownAsync()
    {
        Cancel();
        if (RunningTask is { } t)
        {
            try { await t; } catch { }
        }
        SaveSettings();
    }

    private void OpenOutputFolder()
    {
        if (_lastOutputPath is not null && File.Exists(_lastOutputPath))
        {
            Ui.RevealInExplorer(_lastOutputPath);
            return;
        }
        if (OutputToCustomFolder && Directory.Exists(OutputFolder))
        {
            Ui.RevealInExplorer(OutputFolder);
            return;
        }
        var first = Items.FirstOrDefault();
        if (first is not null && Path.GetDirectoryName(first.FilePath) is { } d && Directory.Exists(d))
        {
            Ui.RevealInExplorer(d);
            return;
        }
        Ui.ShowInfo("開ける出力フォルダがまだありません。");
    }

    // ───────────────────────────── ログ

    public string LogText => _log.ToString();

    public void AppendLog(string message)
    {
        void Do()
        {
            _log.Append(DateTime.Now.ToString("HH:mm:ss ")).AppendLine(message);
            const int Max = 200_000;
            if (_log.Length > Max)
                _log.Remove(0, _log.Length - Max);
            OnPropertyChanged(nameof(LogText));
        }

        if (_ui.CheckAccess())
            Do();
        else
            _ui.BeginInvoke(Do);
    }

    private static string ModeName(ConversionMode m) => m switch
    {
        ConversionMode.AudioExtract => "音声抽出",
        ConversionMode.Compress => "軽量化",
        ConversionMode.Gif => "GIF化",
        ConversionMode.Convert => "形式変換",
        _ => "",
    };
}
