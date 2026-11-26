using PoiConvert.Core;

namespace PoiConvert.Core.Tests;

internal static class ArgAssert
{
    /// <summary>引数リストに「key value」が連続して含まれていること。</summary>
    public static void HasPair(IReadOnlyList<string> args, string key, string value)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == key && args[i + 1] == value)
                return;
        }
        Assert.Fail($"引数に「{key} {value}」がありません: {Join(args)}");
    }

    public static void HasFlag(IReadOnlyList<string> args, string flag)
    {
        Assert.True(args.Contains(flag), $"引数に「{flag}」がありません: {Join(args)}");
    }

    public static void NoFlag(IReadOnlyList<string> args, string flag)
    {
        Assert.False(args.Contains(flag), $"引数に「{flag}」が含まれています: {Join(args)}");
    }

    public static string? ValueOf(IReadOnlyList<string> args, string key)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == key)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>最後の2引数が「-n 出力パス」であること（上書きしない）。</summary>
    public static void EndsWithNoOverwrite(IReadOnlyList<string> args, string output)
    {
        Assert.Equal("-n", args[^2]);
        Assert.Equal(output, args[^1]);
        Assert.DoesNotContain("-y", args);
    }

    public static void StartsWithCommonPrefix(IReadOnlyList<string> args)
    {
        for (int i = 0; i < FfmpegCommandBuilder.CommonPrefix.Count; i++)
            Assert.Equal(FfmpegCommandBuilder.CommonPrefix[i], args[i]);
    }

    public static string Join(IReadOnlyList<string> args) => string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
