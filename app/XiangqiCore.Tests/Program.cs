using System.Text.Json;
using SkiaSharp;
using XiangqiCore;
using XiangqiCore.Recognition;

const string Base = @"D:\Trae CN\project\xiangqi-assistant";
const string Models = Base + @"\xiangqi-pwa-offline";
const string Layout = Base + @"\phase0\jj_layout.json";
const string Engine = Base + @"\engines\Windows\pikafish-sse41-popcnt.exe";
const string Nnue = Base + @"\engines\pikafish.nnue";

var failures = 0;

void Check(string name, string got, string expected)
{
    var ok = got == expected;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}: got={got} expected={expected}");
}

// ---------- notation ----------
var start = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w";
var startB = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR b";
var current = "2baka1nr/9/4b2c1/p1C1n1p1p/9/9/r1P1P1P1P/6N2/4N4/2BAKAB2 w";

var notationCases = new (string Fen, string Move, string Expected)[]
{
    (start, "h2e2", "炮二平五"),
    (start, "b2e2", "炮八平五"),
    (startB, "h9g7", "马8进7"),
    (startB, "b9c7", "马2进3"),
    (current, "e3e4", "兵五进一"),
    (start, "f0e1", "仕四进五"),
    (startB, "c9e7", "象3进5"),
    (start, "a0a5", "车九进五"),
    (startB, "b7b3", "炮2进4"),
    ("9/9/9/9/9/9/9/9/R8/R8 w", "a1a2", "前车进一"),
    ("9/9/9/9/9/9/9/9/R8/R8 w", "a0a1", "后车进一"),
    ("9/9/9/9/9/9/9/9/r8/r8 b", "a0a1", "前车退1"),
    ("9/9/9/9/9/9/9/9/r8/r8 b", "a1a0", "后车进1"),
};
foreach (var (fen, mv, exp) in notationCases)
{
    try
    {
        Check($"notation {mv}", Notation.MoveToChinese(fen, mv), exp);
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL notation {mv}: exception {ex.Message}");
    }
}

// ---------- move generator (XiangqiRules) vs engine perft oracle ----------
using (var oracle = XiangqiEngine.Start(Engine, Nnue))
{
    var (startBoard, _) = Notation.ParseFen(start);
    var startMoves = XiangqiRules.LegalMoves(startBoard, true).Select(m => m.ToUci()).ToHashSet();
    Check("start legal moves == 44", startMoves.Count.ToString(), "44");
    Check("start has b2b9 (cannon screen capture)", startMoves.Contains("b2b9").ToString(), "True");
    Check("start has h2h9 (cannon screen capture)", startMoves.Contains("h2h9").ToString(), "True");

    void CheckMoves(string name, string fen)
    {
        var (b, sd) = Notation.ParseFen(fen);
        var red = sd == "w";
        var gen = XiangqiRules.LegalMoves(b, red).Select(m => m.ToUci()).ToHashSet();
        var orc = oracle.Perft(fen, 1).ToHashSet();
        var missing = orc.Except(gen).OrderBy(x => x).ToList(); // 引擎有、生成器漏 -> BUG
        var extra = gen.Except(orc).OrderBy(x => x).ToList();   // 生成器多 -> BUG
        var ok = missing.Count == 0 && extra.Count == 0;
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} moves {name}: gen={gen.Count} oracle={orc.Count}" +
            (ok ? "" : $" missing=[{string.Join(",", missing)}] extra=[{string.Join(",", extra)}]"));
    }

    CheckMoves("start", start);
    CheckMoves("start black", startB);
    CheckMoves("jj_now", current);
    CheckMoves("jj_mid", "2baka1nr/9/1cn1b2R1/p1C1p1p1p/9/9/r1P1P1P1P/2N1C1N2/9/2BAKAB2 w");
    CheckMoves("jj_board", "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w");
    CheckMoves("crossed pawn (sideways)", "4k4/9/9/9/4P4/9/9/9/9/4K4 w");
    CheckMoves("in check (few moves)", "3k5/9/9/9/9/9/9/9/3r5/3K5 w");

    // 飞将检测
    var kf = new char?[10, 9];
    kf[9, 4] = 'K'; kf[0, 4] = 'k';
    Check("KingsFace open file", XiangqiRules.KingsFace(kf).ToString(), "True");
    kf[5, 4] = 'R';
    Check("KingsFace blocked", XiangqiRules.KingsFace(kf).ToString(), "False");

    // 无升变：所有 UCI move 长度 == 4
    var lenOk = XiangqiRules.LegalMoves(startBoard, true).All(m => m.ToUci().Length == 4);
    Check("no promotion (uci len==4)", lenOk.ToString(), "True");
}

// ---------- move tracker (synthetic, no images) ----------
{
    using var dummy = new SKBitmap(new SKImageInfo(1440, 3200, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    var tracker = new MoveTracker(null!);
    var tcal = new Calibration(new[] { 80f, 1360f, 1360f, 80f }, new[] { 900f, 900f, 2290f, 2290f }, 15);
    var (b0, _) = Notation.ParseFen(start);
    Occupancy Occ(char?[,] b)
    {
        var o = new Occupancy();
        for (var r = 0; r < 10; r++) for (var c = 0; c < 9; c++) o.Filled[r, c] = b[r, c] is not null;
        return o;
    }

    // 1-ply 非吃子：红兵 e3e4
    {
        var b1 = XiangqiRules.CloneAndApply(b0, Move.FromUci("e3e4"));
        var t = tracker.Track(start, Occ(b0), Occ(b1), dummy, dummy, tcal, false);
        var ok = t.Result == TrackerMatch.Kind.Matched && t.Top!.Moves.Count == 1 && t.Top.Moves[0].ToUci() == "e3e4";
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} track 1-ply e3e4: result={t.Result} top={t.Top?.Moves.FirstOrDefault().ToUci()}");
    }
    // 2-ply：红 e3e4 + 黑 c6c5（两个不同子，1-ply 无法解释）
    {
        var b1 = XiangqiRules.CloneAndApply(b0, Move.FromUci("e3e4"));
        var b2 = XiangqiRules.CloneAndApply(b1, Move.FromUci("c6c5"));
        var expFen = Notation.ApplyMove(Notation.ApplyMove(start, "e3e4"), "c6c5");
        var t = tracker.Track(start, Occ(b0), Occ(b2), dummy, dummy, tcal, false);
        var cand = t.Candidates.FirstOrDefault(x => x.Moves.Count == 2);
        var ok = t.Result == TrackerMatch.Kind.Matched && cand is not null && cand.ResultingFen == expFen;
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} track 2-ply: result={t.Result} has2ply={cand is not null} fenOk={cand?.ResultingFen == expFen}");
    }
    // NoChange
    {
        var t = tracker.Track(start, Occ(b0), Occ(b0), dummy, dummy, tcal, false);
        var ok = t.Result == TrackerMatch.Kind.NoChange;
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} track nochange: result={t.Result}");
    }
    // LostTrack：3 个 source
    {
        var o = Occ(b0); o.Filled[9, 0] = false; o.Filled[9, 1] = false; o.Filled[9, 2] = false;
        var t = tracker.Track(start, Occ(b0), o, dummy, dummy, tcal, false);
        var ok = t.Result == TrackerMatch.Kind.LostTrack;
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} track losttrack(3 sources): result={t.Result}");
    }
}

// ---------- tracking service (real images: jj_board == 开局) ----------
using (var tsvc = new TrackingAnalysisService(new BoardRecognizer(Models), Engine, Nnue))
{
    var jjcal = new Calibration(new[] { 80f, 1360f, 1360f, 80f }, new[] { 900f, 900f, 2290f, 2290f }, 15);
    using var boardImg = SKBitmap.Decode(Base + @"\screenshots\jj_board.png");
    using var midImg = SKBitmap.Decode(Base + @"\screenshots\jj_mid.png");
    tsvc.Reset(boardImg, false, jjcal);
    var nc = tsvc.Analyze(boardImg, 1500); // 同帧再分析 -> NoChange 且给开局建议
    var ok1 = nc.Result == TrackerMatch.Kind.NoChange && !string.IsNullOrEmpty(nc.Advice);
    if (!ok1) failures++;
    Console.WriteLine($"{(ok1 ? "OK  " : "FAIL")} track-svc nochange: result={nc.Result} advice={(nc.Advice ?? "").Replace("\n", " | ")}");
    var lt = tsvc.Analyze(midImg, 1500); // 起始 -> 中局（差很多步）-> LostTrack
    var ok2 = lt.Result == TrackerMatch.Kind.LostTrack;
    if (!ok2) failures++;
    Console.WriteLine($"{(ok2 ? "OK  " : "FAIL")} track-svc losttrack start->mid: result={lt.Result}");
}

// ---------- 我方黑 引导 bootstrap（真机截图，对方已秒走首招；reverseRows 180°，不旋转）----------
{
    var bcal = new Calibration(new[] { 80f, 1360f, 1360f, 80f }, new[] { 900f, 900f, 2290f, 2290f }, 15);
    // black_after_horse.png：对方已走 马二进三，bootstrap 应识别出这一步
    var p = Base + @"\screenshots\live\black_after_horse.png";
    if (File.Exists(p))
    {
        using var tsvc2 = new TrackingAnalysisService(new BoardRecognizer(Models), Engine, Nnue);
        using var bs = SKBitmap.Decode(p);
        var msg = tsvc2.Reset(bs, true, bcal);
        var ok = msg.Contains("马二进三");
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} black bootstrap after 马二进三: {msg.Replace("\n", " | ")}");
    }
    // black_start.png：另一张我方黑截图，打印识别到的首招
    var p2 = Base + @"\screenshots\live\black_start.png";
    if (File.Exists(p2))
    {
        using var tsvc3 = new TrackingAnalysisService(new BoardRecognizer(Models), Engine, Nnue);
        using var bs = SKBitmap.Decode(p2);
        var msg = tsvc3.Reset(bs, true, bcal);
        Console.WriteLine($"[black bootstrap] {msg.Replace("\n", " | ")}");
    }
}

// ---------- recognition ----------
var layout = JsonDocument.Parse(File.ReadAllText(Layout)).RootElement;
var corners = layout.GetProperty("corners_px");
var xs = new float[corners.GetArrayLength()];
var ys = new float[corners.GetArrayLength()];
for (var i = 0; i < corners.GetArrayLength(); i++)
{
    xs[i] = corners[i][0].GetSingle();
    ys[i] = corners[i][1].GetSingle();
}
var cal = new Calibration(xs, ys, CropMargin: 15);

var screenshotCases = new (string File, string ExpectedFen)[]
{
    (Base + @"\screenshots\jj_board.png", "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w"),
    (Base + @"\screenshots\jj_mid.png", "2baka1nr/9/1cn1b2R1/p1C1p1p1p/9/9/r1P1P1P1P/2N1C1N2/9/2BAKAB2 w"),
    (Base + @"\screenshots\jj_now.png", "2baka1nr/9/4b2c1/p1C1n1p1p/9/9/r1P1P1P1P/6N2/4N4/2BAKAB2 w"),
};

using (var recognizer = new BoardRecognizer(Models))
{
    foreach (var (file, expected) in screenshotCases)
    {
        using var bmp = SKBitmap.Decode(file);
        var board = recognizer.Analyze(bmp, cal);
        Check($"recognize {Path.GetFileName(file)}", board.Fen, expected);
    }
}

// ---------- engine end-to-end ----------
using (var service = new AnalysisService(Models, Engine, Nnue))
{
    using var bmp = SKBitmap.Decode(Base + @"\screenshots\jj_now.png");
    var result = service.Analyze(bmp, cal, side: "w", movetimeMs: 2500);
    Console.WriteLine($"\n[end-to-end]");
    Console.WriteLine($"  fen  : {result.Fen}");
    Console.WriteLine($"  best : {result.BestMove}  score cp={result.ScoreCp} depth={result.Depth}");
    Console.WriteLine($"  中文 : {result.BestMoveChinese}");
    Console.WriteLine($"  pv   : {string.Join(" ", result.PvChinese)}");
    Check("end-to-end chinese", result.BestMoveChinese ?? "", "兵五进一");
}

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILURES");

if (args.Contains("boxes"))
{
    using var recognizer = new BoardRecognizer(Models);
    using var bmp = SKBitmap.Decode(Base + @"\screenshots\jj_board.png");
    var boxes = recognizer.RunDetector(bmp);
    Console.WriteLine($"\n[boxes] total={boxes.Count}");
    foreach (var b in boxes.OrderBy(b => b.Cy).ThenBy(b => b.Cx))
        Console.WriteLine($"  conf={b.Conf:F3} at ({b.Cx:F0},{b.Cy:F0}) size=({b.W:F0}x{b.H:F0})");
}

if (args.Contains("tensor"))
{
    using var bmp = SKBitmap.Decode(Base + @"\screenshots\jj_board.png");
    var (tensor, scale, offx, offy) = ImageOps.LetterboxRgbChw(bmp, 640);
    Console.WriteLine($"scale={scale:F6} offx={offx} offy={offy} len={tensor.Length}");
    var idx = new[] { 0, 1, 2, 1000, 10000, 12345, 50000, 100000, 200000, 400000, 600000, 1228799 };
    foreach (var i in idx)
        Console.WriteLine($"{i}: {tensor[i]:F6}");
}

if (args.Contains("pilcmp"))
{
    var dump = File.ReadAllBytes(@"C:\Users\Administrator\Documents\Codex\2026-08-06\wo-x\work\dump_resized.bin");
    using var bmp = SKBitmap.Decode(Base + @"\screenshots\jj_board.png");
    var src = ImageOps.GetRgbaBytes(bmp);
    var resized = ImageOps.PilResizeRgb(src, bmp.Width, bmp.Height, 288, 640);
    var mismatches = 0;
    for (var i = 0; i < dump.Length; i++)
    {
        var csVal = resized[(i / 3) * 4 + (i % 3)]; // dump is RGB, resized is RGBA
        if (csVal != dump[i])
        {
            if (mismatches < 10)
                Console.WriteLine($"mismatch byte {i}: py={dump[i]} cs={csVal}");
            mismatches++;
        }
    }
    Console.WriteLine($"mismatch total: {mismatches} / {dump.Length}");
}

if (args.Contains("cells"))
{
    using var recognizer = new BoardRecognizer(Models);
    using var bmp = SKBitmap.Decode(Base + @"\screenshots\jj_mid.png");
    var board = recognizer.Analyze(bmp, cal);
    foreach (var kv in board.Cells.OrderBy(kv => kv.Key.Row * 10 + kv.Key.Col))
    {
        if (kv.Key.Row == 2)
            Console.WriteLine(
                $"[{kv.Key.Row},{kv.Key.Col}] piece={kv.Value.Piece} prob={kv.Value.Prob:F4} " +
                $"corrected={kv.Value.Corrected} alts={string.Join(",", kv.Value.Alts.Select(a => $"{a.Piece}:{a.Prob:F3}"))}");
    }
}

return failures == 0 ? 0 : 1;
