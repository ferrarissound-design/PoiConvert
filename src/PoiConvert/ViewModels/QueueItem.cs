using PoiConvert.Core;

namespace PoiConvert.ViewModels;

public enum ItemStatus
{
    Probing,
    Ready,
    ProbeFailed,
    Queued,
    Running,
    Done,
    Failed,
    Canceled,
}

/// <summary>キュー（一覧）の1行。</summary>
public sealed class QueueItem : ObservableObject
{
    public QueueItem(string path)
    {
        FilePath = path;
        FileName = System.IO.Path.GetFileName(path);
        try
        {
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists)
                _sizeText = FormatSize(fi.Length);
        }
        catch
        {
            // サイズは ffprobe からも取れるので無視
        }
    }

    public string FilePath { get; }
    public string FileName { get; }

    /// <summary>ffprobe の実行タスク（変換開始時に完了を待つ）。</summary>
    public Task? ProbeTask { get; set; }

    private MediaInfo? _media;
    public MediaInfo? Media
    {
        get => _media;
        set
        {
            if (!Set(ref _media, value) || value is null)
                return;
            DurationText = value.DurationSeconds is { } d ? FfmpegCommandBuilder.FormatDuration(d) : "—";
            ResolutionText = value.DisplayWidth is { } w && value.DisplayHeight is { } h
                ? $"{w}×{h}"
                : value.HasAudio ? "音声のみ" : "—";
            if (value.SizeBytes is { } s)
                SizeText = FormatSize(s);
        }
    }

    private string _durationText = "…";
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }

    private string _resolutionText = "…";
    public string ResolutionText { get => _resolutionText; private set => Set(ref _resolutionText, value); }

    private string _sizeText = "—";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    private ItemStatus _status = ItemStatus.Probing;
    public ItemStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => Status switch
    {
        ItemStatus.Probing => "解析中…",
        ItemStatus.Ready => "待機",
        ItemStatus.ProbeFailed => "読込失敗",
        ItemStatus.Queued => "順番待ち",
        ItemStatus.Running => "変換中",
        ItemStatus.Done => "完了",
        ItemStatus.Failed => "失敗",
        ItemStatus.Canceled => "キャンセル",
        _ => "",
    };

    /// <summary>0〜100。</summary>
    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            if (Set(ref _progress, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{Progress:0}%";

    /// <summary>失敗理由など（一覧のツールチップに出す）。</summary>
    private string? _message;
    public string? Message { get => _message; set => Set(ref _message, value); }

    public string? OutputPath { get; set; }

    public bool IsFinished => Status is ItemStatus.Done or ItemStatus.Failed or ItemStatus.Canceled;

    public static string FormatSize(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:0.00} GB";
        if (bytes >= MB) return $"{bytes / MB:0.0} MB";
        if (bytes >= KB) return $"{bytes / KB:0} KB";
        return $"{bytes} B";
    }
}
