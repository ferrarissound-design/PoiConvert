using System.Diagnostics;
using System.Text;

namespace PoiConvert.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StderrTail);

/// <summary>
/// 外部プロセス（ffmpeg / ffprobe）を非同期で実行する。
/// 引数は必ず ArgumentList で渡す（空白・日本語・記号入りのパスでも壊れない）。
/// キャンセル時はプロセスツリーごと確実に終了させる。
/// </summary>
public static class ProcessRunner
{
    /// <param name="onStdoutLine">標準出力1行ごとのコールバック（別スレッドから呼ばれる）</param>
    /// <param name="captureStdout">true なら標準出力全体を結果に入れる（ffprobe 用）</param>
    public static async Task<ProcessResult> RunAsync(
        string exePath,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        bool captureStdout,
        CancellationToken cancellationToken,
        int stderrTailLines = 30)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        var stdout = captureStdout ? new StringBuilder() : null;
        var stderr = new TextTail(stderrTailLines);
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult();
                return;
            }
            if (stdout is not null)
            {
                lock (stdout)
                    stdout.AppendLine(e.Data);
            }
            try { onStdoutLine?.Invoke(e.Data); } catch { /* 進捗通知の失敗で変換を止めない */ }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.TrySetResult();
                return;
            }
            stderr.Add(e.Data);
        };

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start())
                throw new ConversionException($"プロセスを起動できませんでした: {exePath}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ConversionException($"プロセスを起動できませんでした: {exePath}（{ex.Message}）");
        }

        // アプリが落ちても ffmpeg が残らないよう、Windows ではジョブオブジェクトに入れる
        if (OperatingSystem.IsWindows())
            WindowsJobObject.TryAssign(process);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // コールバックは UI スレッドから呼ばれることがあるので待たずに kill だけする
        using (cancellationToken.Register(() => Kill(process, wait: false)))
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    Kill(process, wait: true);
            }
        }

        // 出力を最後まで読み切る（行が欠けないように）
        await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(5000)).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        string outText = "";
        if (stdout is not null)
        {
            lock (stdout)
                outText = stdout.ToString();
        }
        return new ProcessResult(process.ExitCode, outText, stderr.ToString());
    }

    private static void Kill(Process process, bool wait)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* もう終了している */ }
        catch (System.ComponentModel.Win32Exception) { /* 終了処理中 */ }
        catch (NotSupportedException) { }

        if (!wait)
            return;
        try
        {
            // 終了を待ってからファイル削除に進む（ファイルがロックされたままにならないように）
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException) { }
    }
}
