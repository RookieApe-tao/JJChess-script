using System.Text.Json;
using SkiaSharp;
using XiangqiCore;
using XiangqiCore.Gomoku;
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
    var rec = tsvc.TrackOnly(boardImg); // 记录按钮：不跑引擎，同帧 -> NoChange + 手数
    var ok1b = rec.Result == TrackerMatch.Kind.NoChange && rec.MoveCountText == "本局尚未走子";
    if (!ok1b) failures++;
    Console.WriteLine($"{(ok1b ? "OK  " : "FAIL")} track-svc trackonly: result={rec.Result} count={(rec.MoveCountText ?? "").Replace("\n", " | ")}");
    var lt = tsvc.Analyze(midImg, 1500); // 起始 -> 中局（差很多步）-> LostTrack
    var ok2 = lt.Result == TrackerMatch.Kind.LostTrack;
    if (!ok2) failures++;
    Console.WriteLine($"{(ok2 ? "OK  " : "FAIL")} track-svc losttrack start->mid: result={lt.Result}");
    // 应急重校：中局混乱 -> 全量识别重建基线 -> 继续跟踪
    var (rok, rmsg) = tsvc.Resync(midImg, false, jjcal, 1500);
    var rokFen = tsvc.CurrentFen == "2baka1nr/9/1cn1b2R1/p1C1p1p1p/9/9/r1P1P1P1P/2N1C1N2/9/2BAKAB2 w";
    var rokTrack = false;
    if (rok && rokFen)
    {
        var after = tsvc.Analyze(midImg, 1500); // 同帧 -> NoChange，跟踪在新基线上继续
        rokTrack = after.Result == TrackerMatch.Kind.NoChange && !string.IsNullOrEmpty(after.Advice);
    }
    var ok3 = rok && rokFen && rokTrack;
    if (!ok3) failures++;
    Console.WriteLine($"{(ok3 ? "OK  " : "FAIL")} track-svc resync mid: ok={rok} fenOk={rokFen} trackContinues={rokTrack} msg={rmsg.Replace("\n", " | ")}");
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
        var ok = msg.Contains("马二进三") && tsvc2.MoveCount == 1 && tsvc2.MoveLog[0] == "对方 马二进三";
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

// ---------- gomoku: 模型 ----------
{
    // 五连判定:横/竖/斜
    var p1 = new GomokuPosition();
    for (var i = 0; i < 5; i++) p1[7, 2 + i] = GomokuStone.Black;
    Check("gomoku win horizontal", (p1.FindWinner() is { Over: true, Winner: GomokuStone.Black }).ToString(), "True");

    var p2 = new GomokuPosition();
    for (var i = 0; i < 5; i++) p2[3 + i, 8] = GomokuStone.White;
    Check("gomoku win vertical", (p2.FindWinner() is { Over: true, Winner: GomokuStone.White }).ToString(), "True");

    var p3 = new GomokuPosition();
    for (var i = 0; i < 5; i++) p3[4 + i, 4 + i] = GomokuStone.Black;
    Check("gomoku win diagonal", (p3.FindWinner() is { Over: true, Winner: GomokuStone.Black }).ToString(), "True");

    // 六连也算胜(无禁手)
    var p4 = new GomokuPosition();
    for (var i = 0; i < 6; i++) p4[5, 3 + i] = GomokuStone.Black;
    Check("gomoku win overline", (p4.FindWinner() is { Over: true }).ToString(), "True");

    // 四连不算胜
    var p5 = new GomokuPosition();
    for (var i = 0; i < 4; i++) p5[7, 5 + i] = GomokuStone.Black;
    Check("gomoku four not win", (p5.FindWinner() is null).ToString(), "True");

    // 轮到方:黑先
    var p6 = new GomokuPosition();
    Check("gomoku empty side", p6.SideToMove.ToString(), "Black");
    p6[7, 7] = GomokuStone.Black;
    Check("gomoku after black", p6.SideToMove.ToString(), "White");
    p6[8, 8] = GomokuStone.White;
    Check("gomoku after white", p6.SideToMove.ToString(), "Black");

    // 记谱
    Check("gomoku cellname H8", GomokuPosition.CellName(7, 7), "H8");
    Check("gomoku cellname full", GomokuPosition.CellNameFull(7, 7), "H8(8,8)");
    Check("gomoku cellname A1", GomokuPosition.CellName(0, 0), "A1");
    Check("gomoku cellname O15", GomokuPosition.CellName(14, 14), "O15");
}

// ---------- gomoku: 引擎 ----------
{
    var engine = new GomokuEngine();

    // 空盘 → 天元
    var r0 = engine.Search(new GomokuPosition(), 200);
    Check("gomoku engine empty board tengen", $"{r0.Move!.Value.Row},{r0.Move!.Value.Col}", "7,7");

    // 己方成五点必下:黑在 H8 附近已有四连(两端空)
    var pWin = new GomokuPosition();
    for (var i = 0; i < 4; i++) pWin[7, 5 + i] = GomokuStone.Black; // F8..I8 四连
    pWin[7, 6] = GomokuStone.Black; // 保持黑先(黑5白4 → 轮黑)
    var rWin = engine.Search(pWin, 500);
    var winMove = rWin.Move!.Value;
    // 成五点在 (7,4) 或 (7,9)
    Check("gomoku engine takes five", ((winMove.Row, winMove.Col) is (7, 4) or (7, 9)).ToString(), "True");

    // 对方活四必挡:白四连两端空,黑只有一子(轮黑)。挡点必须是两端之一
    var pBlock = new GomokuPosition();
    for (var i = 0; i < 4; i++) pBlock[7, 5 + i] = GomokuStone.White; // 白四连 F..I
    pBlock[0, 0] = GomokuStone.Black;
    var rBlock = engine.Search(pBlock, 500);
    var bm = rBlock.Move!.Value;
    Check("gomoku engine blocks open four", ((bm.Row == 7 && (bm.Col == 4 || bm.Col == 9))).ToString(), "True");

    // 对方活三,防守优先级:黑必然挡在两端延伸点 F8/J8 或贴邻
    var pThreat = new GomokuPosition();
    for (var i = 0; i < 3; i++) pThreat[7, 6 + i] = GomokuStone.White; // 白活三 G..I
    pThreat[0, 0] = GomokuStone.Black;
    var rT = engine.Search(pThreat, 800);
    var tm = rT.Move!.Value;
    Console.WriteLine($"[gomoku open-three move] {tm.Name} score={rT.Score}");
    var nearLine = (tm.Row == 7 && (tm.Col == 5 || tm.Col == 9)) ||
                   (Math.Abs(tm.Row - 7) <= 1 && tm.Col >= 5 && tm.Col <= 10);
    Check("gomoku engine answers open three", nearLine.ToString(), "True");

    // VCF:黑双威胁(交叉活三),任一延伸即四三,评分应大幅领先
    var pVcf = new GomokuPosition();
    pVcf[7, 6] = GomokuStone.Black; pVcf[7, 7] = GomokuStone.Black; pVcf[7, 8] = GomokuStone.Black; // GHI8 活三
    pVcf[6, 7] = GomokuStone.Black; // H7
    pVcf[8, 7] = GomokuStone.Black; // H9 → H 列 H7/H8/H9 三连
    // 手数平衡:黑5白4 → 轮白。给白加到 5 子,轮黑
    pVcf[0, 0] = GomokuStone.White; pVcf[0, 1] = GomokuStone.White;
    pVcf[1, 0] = GomokuStone.White; pVcf[1, 1] = GomokuStone.White;
    pVcf[14, 14] = GomokuStone.White;
    var rVcf = engine.Search(pVcf, 1000);
    var vm = rVcf.Move!.Value;
    // 正解:下 H 列成四(6,7 或 9,7)或 GHI 延长成四 —— 都是威胁着法;只断言"评分大幅领先"
    Check("gomoku engine vcf/double threat found", (rVcf.Score >= 100_000).ToString(), "True");
}

// ---------- gomoku: 识别(合成图) ----------
SkiaSharp.SKBitmap MakeBoard(bool tilt, int lastMoveRow = -1, int lastMoveCol = -1)
{
    // 700x700 木板底色 + 15×15 网格(tilt = x 方向剪切 3%,线与子同歪)+ 黑白圆子
    var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(700, 700, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
    using (var c = new SkiaSharp.SKCanvas(bmp))
    {
        c.Clear(new SkiaSharp.SKColor(220, 179, 92)); // 木板
        var line = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(60, 40, 20), StrokeWidth = 2f };
        var step = 620f / 14f;
        var ox = 40f;
        float X(float gx, float gy) => ox + gx + (tilt ? (gy - 40f) * 0.03f : 0f); // 剪切:往下越深越往右
        for (var i = 0; i < 15; i++)
        {
            // 竖线:整列同样剪切;横线:各点随 y 剪切 → 网格平行四边形化
            var vx = ox + i * step;
            c.DrawLine(X(i * step, 40f), 40f, X(i * step, 660f), 660f, line);
            var hy = 40 + i * step;
            c.DrawLine(X(0f, hy), hy, X(620f, hy), hy, line);
        }
        void Stone(int r, int col, bool black)
        {
            var cx = X(col * step, 40f + r * step);
            var cy = 40 + r * step;
            var fill = new SkiaSharp.SKPaint { Color = black ? new SkiaSharp.SKColor(10, 10, 10) : new SkiaSharp.SKColor(245, 245, 245) };
            c.DrawCircle(cx, cy, step * 0.4f, fill);
        }
        // 一局对杀:黑白各 5 子
        Stone(7, 7, true); Stone(7, 8, false);
        Stone(6, 8, true); Stone(8, 6, false);
        Stone(6, 6, true); Stone(8, 8, false);
        Stone(5, 9, true); Stone(6, 9, false);
        Stone(0, 14, true); Stone(14, 0, false); // 角落子
        if (lastMoveRow >= 0)
        {
            var cx = X(lastMoveCol * step, 40f + lastMoveRow * step);
            var cy = 40 + lastMoveRow * step;
            var red = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 0, 0) };
            c.DrawCircle(cx, cy, step * 0.12f, red);
        }
        c.Flush();
    }
    return bmp;
}

{

    var recognizer = new GomokuRecognizer();

    using (var board = MakeBoard(tilt: false, lastMoveRow: 5, lastMoveCol: 9))
    {
        var rect = new GomokuRect(20, 20, 680, 680);
        var detected = recognizer.RefineGrid(board, rect);
        Check("gomoku grid detected", detected.ToString(), "True");
        var read = recognizer.Read(board, rect);
        var pos = read.Position;
        Check("gomoku read stone counts", $"{pos.BlackCount},{pos.WhiteCount}", "5,5");
        Check("gomoku read H8 black", pos[7, 7].ToString(), "Black");
        Check("gomoku read I8 white", pos[7, 8].ToString(), "White");
        Check("gomoku read corner black", pos[0, 14].ToString(), "Black");
        Check("gomoku read corner white", pos[14, 0].ToString(), "White");
        Check("gomoku read empty cell", (pos[0, 0] is null).ToString(), "True");
        Check("gomoku last move hint", $"{read.LastMoveHint?.Row},{read.LastMoveHint?.Col}", "5,9");
        // 屏幕坐标映射回棋盘中心附近(格点 ~ (40+col*44.286))
        var (sx, sy) = recognizer.ScreenPoint(7, 7, rect);
        var near = Math.Abs(sx - 40 - 7 * 620f / 14f - (20 - 28 /* pad 4% of 660 */)) < 8;
        Check("gomoku screen point H8", near.ToString(), "True");
    }

    using (var boardTilt = MakeBoard(tilt: true))
    {
        var rect = new GomokuRect(20, 20, 680, 680);
        var detected = recognizer.RefineGrid(boardTilt, rect);
        // 剪切 3% 超出投影法直线假设(整条竖线在投影里弥散);此时应回退等分而不是误检
        Check("gomoku sheared grid fallback (no misdetect)", (detected == false).ToString(), "True");
        var read = recognizer.Read(boardTilt, rect);
        var pos = read.Position;
        // 回退等分下中央区域仍应识别(剪切偏差在角落最大)
        Check("gomoku sheared H8 black", pos[7, 7].ToString(), "Black");
    }
}

// ---------- gomoku: 编排服务 ----------
{
    // 复用上面合成图思路做一次端到端(识别+引擎)
    using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(700, 700, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
    using (var c = new SkiaSharp.SKCanvas(bmp))
    {
        c.Clear(new SkiaSharp.SKColor(220, 179, 92));
        var line = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(60, 40, 20), StrokeWidth = 2f };
        var step = 620f / 14f;
        for (var i = 0; i < 15; i++)
        {
            c.DrawLine(40 + i * step, 40, 40 + i * step, 660, line);
            c.DrawLine(40, 40 + i * step, 660, 40 + i * step, line);
        }
        void Stone(int r, int col, bool black)
        {
            var fill = new SkiaSharp.SKPaint { Color = black ? new SkiaSharp.SKColor(10, 10, 10) : new SkiaSharp.SKColor(245, 245, 245) };
            c.DrawCircle(40 + col * step, 40 + r * step, step * 0.4f, fill);
        }
        Stone(7, 7, true); Stone(7, 8, false); Stone(6, 8, true); // 黑2白1 → 轮白
        c.Flush();
    }
    using var svc = new GomokuAnalysisService();
    var calMsg = svc.Calibrate(bmp, new GomokuRect(20, 20, 680, 680));
    Console.WriteLine($"[gomoku calibrate] {calMsg.Replace("\n", " | ")}");
    var advice = svc.Analyze(bmp, GomokuStone.White, 800);
    Console.WriteLine($"[gomoku advice] {advice.Text.Replace("\n", " | ")} marker={advice.MarkerPoint}");
    Check("gomoku svc analyze ok", advice.Ok.ToString(), "True");
    Check("gomoku svc has marker", (advice.MarkerPoint is not null).ToString(), "True");
    Check("gomoku svc mentions white", advice.Text.Contains("白").ToString(), "True");
    var resetMsg = svc.Reset();
    Check("gomoku svc reset", resetMsg.Contains("清盘").ToString(), "True");
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

// gomoku 诊断:dotnet run -- gomoku <图片> [x1 y1 x2 y2]
// 不给矩形时用整图。打印格点、亮度矩阵、ASCII 盘面,供真实截图调阈值。
if (args.Contains("gomoku"))
{
    var idx = Array.IndexOf(args, "gomoku");
    var img = args.Length > idx + 1 ? args[idx + 1] : Base + @"\screenshots\gomoku_sample.png";
    if (!File.Exists(img))
    {
        Console.WriteLine($"[gomoku] 图片不存在: {img}");
    }
    else
    {
        using var bmp = SKBitmap.Decode(img);
        GomokuRect rect = args.Length > idx + 5
            ? new GomokuRect(float.Parse(args[idx + 2]), float.Parse(args[idx + 3]), float.Parse(args[idx + 4]), float.Parse(args[idx + 5]))
            : new GomokuRect(0, 0, bmp.Width - 1, bmp.Height - 1);
        var recog = new GomokuRecognizer();
        var detected = recog.RefineGrid(bmp, rect);
        var info = recog.GridInfo;
        Console.WriteLine($"[gomoku] grid detected={detected} step={(info?.Step ?? 0):F1}px " +
                          $"xs=[{string.Join(",", info!.Value.Xs.Select(v => v.ToString("F0")))}] " +
                          $"ys=[{string.Join(",", info.Value.Ys.Select(v => v.ToString("F0")))}]");
        var read = recog.Read(bmp, rect);
        Console.WriteLine($"[gomoku] black={read.Position.BlackCount} white={read.Position.WhiteCount} " +
                          $"hint={(read.LastMoveHint is { } h ? GomokuPosition.CellNameFull(h.Row, h.Col) : "无")} warn={read.Warning ?? "无"}");
        Console.WriteLine("[gomoku] 亮度矩阵(空=空格,黑/白棋子位显示数值):");
        for (var r = 0; r < GomokuPosition.Size; r++)
        {
            var rowTxt = "";
            for (var c = 0; c < GomokuPosition.Size; c++)
            {
                var s = read.Position[r, c];
                rowTxt += s is null ? "  ." : (s == GomokuStone.Black ? " B" : " W");
            }
            Console.WriteLine($"  {(char)('A' + 0)}{rowTxt}  brightness[0..3]={read.Brightness[r, 0]},{read.Brightness[r, 1]},{read.Brightness[r, 2]},{read.Brightness[r, 3]}");
        }
    }
}

return failures == 0 ? 0 : 1;
