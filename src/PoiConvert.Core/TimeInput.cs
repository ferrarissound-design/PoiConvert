using System.Globalization;

namespace PoiConvert.Core;

/// <summary>「12.5」「1:23」「1:02:03.5」のような時間入力を秒に変換する。</summary>
public static class TimeInput
{
    public static bool TryParseSeconds(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 全角数字・全角コロン・全角ピリオドも受け付ける
        var s = text.Trim().Normalize(System.Text.NormalizationForm.FormKC);
        var parts = s.Split(':');
        if (parts.Length > 3)
            return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            bool last = i == parts.Length - 1;
            if (p.Length == 0)
                return false;
            if (!double.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v < 0)
                return false;
            if (!last && p.Contains('.'))
                return false;
            // 先頭以外の分・秒は 60 未満
            if (i > 0 && v >= 60)
                return false;
            total = total * 60 + v;
        }
        if (double.IsInfinity(total) || double.IsNaN(total))
            return false;
        seconds = total;
        return true;
    }
}
