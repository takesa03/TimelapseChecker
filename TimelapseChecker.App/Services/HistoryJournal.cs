using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TimelapseChecker.App.Services;

/// <summary>判定履歴1件分（履歴ファイルの "frame" レコード）。画像の場所は、監視フォルダからの相対パスで持つ。</summary>
public class HistoryEntry
{
    public DateTime Time { get; set; }

    /// <summary>監視フォルダからの相対パス（フォルダを移動しても、読み込み時に今のフォルダへ解決できる）。</summary>
    public string File { get; set; } = "";

    /// <summary>0=正常、1=注意、2=警告。</summary>
    public int Level { get; set; }
    public bool Calibrating { get; set; }
    public List<string> Reasons { get; set; } = new();

    public double Area { get; set; } = 1;
    public double AreaSession { get; set; } = 1;
    public double Shift { get; set; }
    public double Brightness { get; set; } = 1;
    public double Unevenness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Range { get; set; } = 1;
    public double BackgroundLift { get; set; }
    public bool TouchesEdge { get; set; }
}

/// <summary>
/// 監視フォルダ直下の履歴ファイル（JSON Lines・追記のみ）への記録と読み込み。
/// 画像のパスは相対で保存するので、フォルダごと別の場所へ移動しても履歴を使える。
/// </summary>
public static class HistoryJournal
{
    public const string FileName = "TimelapseChecker.history.jsonl";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    public static string PathFor(string folder) => Path.Combine(folder, FileName);

    /// <summary>履歴の相対パスを、今の監視フォルダの絶対パスに直す。</summary>
    public static string Resolve(string folder, string file)
        => Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(folder, file));

    /// <summary>画像の絶対パスを、監視フォルダからの相対パスにする（別ドライブなど相対にできない場合は絶対パスのまま）。</summary>
    public static string MakeRelative(string folder, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(folder, fullPath);
        }
        catch
        {
            return fullPath;
        }
    }

    public static void AppendFrame(string folder, HistoryEntry e)
        => Append(folder, new Dictionary<string, object?>
        {
            ["type"] = "frame",
            ["time"] = e.Time.ToString("o"),
            ["file"] = e.File,
            ["level"] = e.Level,
            ["calibrating"] = e.Calibrating,
            ["reasons"] = e.Reasons,
            ["area"] = Math.Round(e.Area, 5),
            ["areaSession"] = Math.Round(e.AreaSession, 5),
            ["shift"] = Math.Round(e.Shift, 5),
            ["brightness"] = Math.Round(e.Brightness, 5),
            ["unevenness"] = Math.Round(e.Unevenness, 5),
            ["contrast"] = Math.Round(e.Contrast, 5),
            ["range"] = Math.Round(e.Range, 5),
            ["backgroundLift"] = Math.Round(e.BackgroundLift, 5),
            ["touchesEdge"] = e.TouchesEdge,
        });

    /// <param name="kind">"start"（監視開始）／"stop"（監視終了）</param>
    public static void AppendSession(string folder, string kind, string appVersion)
        => Append(folder, new Dictionary<string, object?>
        {
            ["type"] = "session",
            ["time"] = DateTime.Now.ToString("o"),
            ["kind"] = kind,
            ["app"] = appVersion,
        });

    /// <param name="relativeFile">基準にした画像（フォルダからの相対パス）</param>
    /// <param name="source">"自動"／"最新画像"／"指定"</param>
    public static void AppendBaseline(string folder, string relativeFile, string source)
        => Append(folder, new Dictionary<string, object?>
        {
            ["type"] = "baseline",
            ["time"] = DateTime.Now.ToString("o"),
            ["file"] = relativeFile,
            ["source"] = source,
        });

    private static void Append(string folder, Dictionary<string, object?> record)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            string line = JsonSerializer.Serialize(record, Options) + "\n";
            lock (Gate)
            {
                using var fs = new FileStream(PathFor(folder), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = new UTF8Encoding(false).GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            // 記録に失敗しても、監視は止めない
            System.Diagnostics.Debug.WriteLine($"履歴の記録エラー: {ex.Message}");
        }
    }

    /// <summary>フォルダの履歴ファイルから、新しい順に最大 max 件の判定結果を読む。壊れた行は読み飛ばす。</summary>
    public static List<HistoryEntry> LoadRecent(string folder, int max)
    {
        var all = new List<HistoryEntry>();
        string path = PathFor(folder);
        if (string.IsNullOrWhiteSpace(folder) || !File.Exists(path)) return all;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var t) || t.GetString() != "frame") continue;

                    var e = new HistoryEntry
                    {
                        Time = DateTime.Parse(root.GetProperty("time").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
                        File = root.GetProperty("file").GetString() ?? "",
                        Level = root.GetProperty("level").GetInt32(),
                        Calibrating = Get(root, "calibrating", false),
                        Area = Get(root, "area", 1.0),
                        AreaSession = Get(root, "areaSession", 1.0),
                        Shift = Get(root, "shift", 0.0),
                        Brightness = Get(root, "brightness", 1.0),
                        Unevenness = Get(root, "unevenness", 0.0),
                        Contrast = Get(root, "contrast", 1.0),
                        Range = Get(root, "range", 1.0),
                        BackgroundLift = Get(root, "backgroundLift", 0.0),
                        TouchesEdge = Get(root, "touchesEdge", false),
                    };
                    if (root.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rs.EnumerateArray())
                        {
                            if (r.GetString() is { } s) e.Reasons.Add(s);
                        }
                    }
                    all.Add(e);
                }
                catch
                {
                    // 書き込み途中・壊れた行は読み飛ばす
                }
            }
        }
        catch
        {
            return new List<HistoryEntry>();
        }

        int skip = Math.Max(0, all.Count - max);
        var recent = all.GetRange(skip, all.Count - skip);
        recent.Reverse();
        return recent;
    }

    private static double Get(JsonElement o, string name, double fallback)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static bool Get(JsonElement o, string name, bool fallback)
        => o.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;
}
