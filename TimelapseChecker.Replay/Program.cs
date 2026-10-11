using TimelapseChecker.Core;

// 既存フォルダ(case*/ 直下の連番画像)を順に判定して結果を表示する検証用ツール。
// 使い方: TimelapseChecker.Replay <フォルダ> [<フォルダ>...]
//   フォルダ直下に画像があればそのフォルダを、無ければ子フォルダを個別のシーケンスとして扱う。
if (args.Length == 0)
{
    Console.WriteLine("usage: TimelapseChecker.Replay <folder> [<folder>...]");
    return 1;
}

var exts = new[] { ".tif", ".tiff", ".png", ".jpg", ".jpeg" };
bool IsImage(string f) => exts.Contains(Path.GetExtension(f).ToLowerInvariant());

IEnumerable<string> Sequences(string dir)
{
    if (Directory.EnumerateFiles(dir).Any(IsImage)) { yield return dir; yield break; }
    foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d)) yield return sub;
}

int bad = 0, total = 0;
foreach (var root in args)
foreach (var seq in Sequences(root))
{
    Console.WriteLine($"== {seq}");
    var checker = new SequenceChecker(new CheckerThresholds());
    foreach (var f in Directory.GetFiles(seq).Where(IsImage).OrderBy(f => f))
    {
        var m = FrameAnalyzer.Analyze(f, withPreview: false);
        var name = Path.GetFileNameWithoutExtension(f);
        name = name.Length > 8 ? name[^8..] : name;
        if (m == null) { Console.WriteLine($"{name}  (読み込み失敗)"); continue; }
        var r = checker.Evaluate(m);
        total++;
        if (r.Level != CheckLevel.Normal) bad++;
        Console.WriteLine($"{name} {r.Level,-8} area={r.AreaRatio:F2}/{r.AreaRatioSession:F2} shift={r.Shift:F2} contrast={r.ContrastRatio:F2} range={r.RangeRatio:F2} bg={r.BackgroundLift:+0.00;-0.00;0.00} edge={(m.TouchesEdge ? 1 : 0)} art={m.EdgeArtifactStrength:P1}{m.EdgeArtifactSide}  {string.Join(" / ", r.Reasons)}");
    }
}
Console.WriteLine($"\n判定対象 {total} 枚中、注意/警告 {bad} 枚");
return 0;
