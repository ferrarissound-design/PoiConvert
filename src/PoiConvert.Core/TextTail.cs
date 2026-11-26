namespace PoiConvert.Core;

/// <summary>最後の N 行だけ覚えておくリングバッファ（ffmpeg のエラー出力末尾用）。スレッドセーフ。</summary>
public sealed class TextTail
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();
    private readonly object _lock = new();

    public TextTail(int capacity = 30)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (_lock)
        {
            _lines.Enqueue(line.TrimEnd());
            while (_lines.Count > _capacity)
                _lines.Dequeue();
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }
}
