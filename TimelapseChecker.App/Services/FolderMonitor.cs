using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TimelapseChecker.Core;

namespace TimelapseChecker.App.Services;

/// <summary>
/// フォルダを監視し、新しく出力された画像を順に解析して判定結果を通知する。
/// FileSystemWatcherの取りこぼしに備えて、定期的なフォルダ走査も併用する。
/// </summary>
public sealed class FolderMonitor : IDisposable
{
    // 基準画像の自動選択で、先頭から何枚まで試すか
    private const int MaxBaselineCandidates = 20;

    private readonly SequenceChecker checker;
    private readonly HashSet<string> extensions;
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object seenLock = new();
    private readonly CancellationTokenSource cts = new();

    private FileSystemWatcher? watcher;
    private Task? worker;
    private Task? poller;
    private string folder = "";

    public event Action<CheckResult>? FrameChecked;
    public event Action<string>? Message;

    /// <summary>基準画像が設定（または再設定）されたとき、その解析結果（プレビュー含む）を通知する。</summary>
    public event Action<FrameMetrics>? BaselineChanged;

    public FolderMonitor(CheckerThresholds thresholds, string extensionList)
    {
        checker = new SequenceChecker(thresholds);
        extensions = extensionList
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Start(string watchFolder)
    {
        folder = watchFolder;

        // 起動時点で存在するファイルは判定対象外（これから出力される画像だけを判定する）。
        // ただし、その中の最初の1枚（ファイル名順）は基準画像として使う。
        var existing = Directory.EnumerateFiles(folder).Where(IsTarget)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in existing)
        {
            seen.Add(f);
        }

        watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) => Enqueue(e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        watcher.Changed += (_, e) => Enqueue(e.FullPath);

        var token = cts.Token;
        worker = Task.Run(async () =>
        {
            // 基準画像の読み込みを済ませてから、新規画像の判定を始める。
            // 先頭の画像に端のノイズ（太陽が画角から外れた状態）がある場合は基準にせず、次の画像を試す。
            foreach (var candidate in existing.Take(MaxBaselineCandidates))
            {
                if (token.IsCancellationRequested) break;
                if (await SetBaselineAsync(candidate, quiet: true)) break;
            }
            await WorkerLoop(token);
        });
        poller = Task.Run(() => PollLoop(token));
    }

    /// <summary>基準画像の設定結果。失敗時は、利用者に見せるメッセージを持つ。</summary>
    public sealed record BaselineResult(bool Ok, string? Error);

    /// <summary>
    /// 指定した画像を基準に設定し直す（利用者の操作用）。失敗したときは、理由を結果で返す
    /// （画面に出すかどうかは呼び出し側が決める。ダイアログで知らせる想定）。
    /// </summary>
    public async Task<BaselineResult> TrySetBaselineAsync(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            var metrics = await Task.Run(() => FrameAnalyzer.Analyze(path));
            if (metrics == null)
            {
                return new BaselineResult(false, $"画像を読み込めませんでした。\n{name}");
            }

            bool ok;
            lock (checker) ok = checker.SetBaseline(metrics);
            if (!ok)
            {
                // 端に引き伸ばしノイズがある画像を基準にすると、以後の判定がずれるため採用しない
                return new BaselineResult(false,
                    $"{name} は、画像の{metrics.EdgeArtifactSide}側に引き伸ばしノイズ（太陽が画角から外れている状態）があるため、基準画像にできません。\n別の画像を選んでください。");
            }

            BaselineChanged?.Invoke(metrics);
            return new BaselineResult(true, null);
        }
        catch (Exception ex)
        {
            return new BaselineResult(false, $"基準画像の設定に失敗しました。\n{ex.Message}");
        }
    }

    /// <summary>基準画像にしようとしている画像の、事前の確認結果。</summary>
    /// <param name="HardError">基準にできない理由（読めない・端のノイズ）。nullなら基準にできる。</param>
    /// <param name="Inspection">現在の基準に対する判定。注意・警告なら、エラーのある画像である。</param>
    public sealed record BaselineCandidate(FrameMetrics? Metrics, string? HardError, CheckResult? Inspection);

    /// <summary>
    /// 画像を解析して、基準にできるかを確認する（基準はまだ変えない）。
    /// エラーのある画像かどうか（Inspection）は、呼び出し側が利用者に確認するために使う。
    /// </summary>
    public async Task<BaselineCandidate> PrepareBaselineAsync(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            var metrics = await Task.Run(() => FrameAnalyzer.Analyze(path));
            if (metrics == null)
                return new BaselineCandidate(null, $"画像を読み込めませんでした。\n{name}", null);

            CheckResult inspection;
            bool artifact;
            lock (checker)
            {
                artifact = checker.HasEdgeArtifact(metrics);
                inspection = checker.Inspect(metrics);
            }
            if (artifact)
            {
                return new BaselineCandidate(metrics,
                    $"{name} は、画像の{metrics.EdgeArtifactSide}側に引き伸ばしノイズ（太陽が画角から外れている状態）があるため、基準画像にできません。\n別の画像を選んでください。",
                    inspection);
            }
            return new BaselineCandidate(metrics, null, inspection);
        }
        catch (Exception ex)
        {
            return new BaselineCandidate(null, $"基準画像の設定に失敗しました。\n{ex.Message}", null);
        }
    }

    /// <summary>確認済みの画像を、基準画像として確定する。</summary>
    public bool CommitBaseline(FrameMetrics metrics)
    {
        bool ok;
        lock (checker) ok = checker.SetBaseline(metrics);
        if (ok) BaselineChanged?.Invoke(metrics);
        return ok;
    }

    /// <summary>
    /// 基準画像の自動選択用。基準にできなかった理由は、画面下部のメッセージとして通知する
    /// （自動処理なので、ダイアログは出さない）。
    /// </summary>
    /// <param name="quiet">読み込めなかった場合のメッセージを出さない。</param>
    public async Task<bool> SetBaselineAsync(string path, bool quiet = false)
    {
        var r = await TrySetBaselineAsync(path);
        if (!r.Ok && !quiet && r.Error != null) Message?.Invoke(r.Error.Replace("\n", " "));
        else if (!r.Ok && quiet && r.Error != null && r.Error.Contains("ノイズ")) Message?.Invoke(r.Error.Replace("\n", " "));
        return r.Ok;
    }

    private bool IsTarget(string path) => extensions.Contains(Path.GetExtension(path));

    private void Enqueue(string path)
    {
        if (!IsTarget(path)) return;
        lock (seenLock)
        {
            if (!seen.Add(path)) return;
        }
        queue.Writer.TryWrite(path);
    }

    private async Task PollLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder).Where(IsTarget).OrderBy(f => f).ToList();
                }
                catch (Exception ex)
                {
                    Message?.Invoke($"フォルダを読み取れません: {ex.Message}");
                    continue;
                }
                foreach (var f in files) Enqueue(f);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        try
        {
            await foreach (var path in queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    if (!await WaitForFileReady(path, token)) continue;

                    var metrics = FrameAnalyzer.Analyze(path);
                    if (metrics == null)
                    {
                        Message?.Invoke($"画像を読み込めませんでした: {Path.GetFileName(path)}");
                        continue;
                    }

                    CheckResult result;
                    lock (checker) result = checker.Evaluate(metrics);
                    FrameChecked?.Invoke(result);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Message?.Invoke($"解析エラー {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>撮影ソフトが書き込み中のファイルを読まないよう、サイズが安定して開けるまで待つ。</summary>
    private static async Task<bool> WaitForFileReady(string path, CancellationToken token)
    {
        long lastSize = -1;
        for (int i = 0; i < 60; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                if (info.Length > 0 && info.Length == lastSize)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return true;
                }
                lastSize = info.Length;
            }
            catch (IOException) { }
            await Task.Delay(300, token);
        }
        return false;
    }

    public void Dispose()
    {
        cts.Cancel();
        watcher?.Dispose();
        queue.Writer.TryComplete();
        cts.Dispose();
    }
}


