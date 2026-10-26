// 诊断：应急重校在残局/中局为何失败。对比 Analyze(FEN) 与 BuildOccupancy(占位) 两条路径。
using SkiaSharp;
using XiangqiCore;
using XiangqiCore.Recognition;

const string Base = @"D:\Trae CN\project\xiangqi-assistant";
const string Models = Base + @"\xiangqi-pwa-offline";

var cal = new Calibration(new[] { 80f, 1360f, 1360f, 80f }, new[] { 900f, 900f, 2290f, 2290f }, 15);

using var recognizer = new BoardRecognizer(Models);

foreach (var file in new[] { "jj_board.png", "jj_mid.png", "jj_now.png", @"live\black_start.png", @"live\black_after_horse.png" })
{
    var p = Path.Combine(Base, "screenshots", file);
    if (!File.Exists(p)) { Console.WriteLine($"--- {file}: 缺文件"); continue; }
    using var bmp = SKBitmap.Decode(p);
    var black = file.Contains("black");
    Console.WriteLine($"=== {file} (我方{(black ? "黑" : "红")}) ===");

    // 路径A：跟踪用的占位（minConf 0.5）
    var occ = recognizer.BuildOccupancy(bmp, cal, black, null, null);
    Console.WriteLine($"[A 占位] colOff={occ.ColOffset} rowOff={occ.RowOffset} offGrid={occ.OffGridFraction:F2} count={Count(occ)}");
    PrintGhost(occ.Filled);

    // 路径B：应急用的全量识别（ConfThreshold 0.3）
    var rec = recognizer.Analyze(bmp, cal, black ? "b" : "w", reverseRows: black);
    Console.WriteLine($"[B 识别FEN] {rec.Fen}");
    var (b, _) = Notation.ParseFen(rec.Fen);
    var occB = new bool[10, 9];
    for (var r = 0; r < 10; r++) for (var c = 0; c < 9; c++) occB[r, c] = b[r, c] is not null;
    Console.WriteLine($"[B 占位] count={Count2(occB)}");
    PrintGhost(occB);

    // 差异
    var diffs = 0;
    for (var r = 0; r < 10; r++) for (var c = 0; c < 9; c++)
        if (occ.Filled[r, c] != occB[r, c]) { diffs++; Console.WriteLine($"  diff at [{r},{c}]: A={occ.Filled[r, c]} B={occB[r, c]} piece={b[r, c]}"); }
    Console.WriteLine($"diffs={diffs} -> PlacementMatches={diffs == 0}");
    Console.WriteLine();
}

static int Count(Occupancy o) { var n = 0; for (var r = 0; r < 10; r++) for (var c = 0; c < 9; c++) if (o.Filled[r, c]) n++; return n; }
static int Count2(bool[,] f) { var n = 0; for (var r = 0; r < 10; r++) for (var c = 0; c < 9; c++) if (f[r, c]) n++; return n; }
static void PrintGhost(bool[,] f)
{
    for (var r = 0; r < 10; r++)
    {
        var s = "";
        for (var c = 0; c < 9; c++) s += f[r, c] ? "?" : ".";
        Console.WriteLine($"  {s}");
    }
}
