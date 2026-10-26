using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace XiangqiCore.Recognition;

public sealed class BoardRecognizer : IDisposable
{
    public const float ConfThreshold = 0.3f;
    public const float IouThreshold = 0.45f;
    private static readonly float[] Expansions = { -0.05f, 0.0f, 0.05f, 0.10f };
    private static readonly float[] PrimaryRatios = { 0.0f, 0.05f };

    private static readonly string[] ClassNames = { "士", "車", "馬", "卒", "將", "炮", "相" };
    private static readonly Dictionary<string, string> PieceToFen = new()
    {
        ["士"] = "A", ["車"] = "R", ["馬"] = "N", ["卒"] = "P", ["將"] = "K", ["炮"] = "C", ["相"] = "B"
    };

    private static readonly Dictionary<char, int> MaxCounts = new()
    {
        ['R'] = 2, ['N'] = 2, ['B'] = 2, ['A'] = 2, ['C'] = 2, ['P'] = 5, ['K'] = 1,
        ['r'] = 2, ['n'] = 2, ['b'] = 2, ['a'] = 2, ['c'] = 2, ['p'] = 5, ['k'] = 1
    };

    private static readonly HashSet<(int R, int C)> XiangSquares = new()
    {
        (0, 2), (0, 6), (2, 0), (2, 4), (2, 8), (4, 2), (4, 6),
        (9, 2), (9, 6), (7, 0), (7, 4), (7, 8), (5, 2), (5, 6)
    };

    private readonly InferenceSession _detector;
    private readonly InferenceSession _classifier;

    public BoardRecognizer(string modelDir)
    {
        _detector = new InferenceSession(Path.Combine(modelDir, "online_xiangqi_piece_detector.onnx"));
        _classifier = new InferenceSession(Path.Combine(modelDir, "online_xiangqi_classifier.onnx"));
    }

    public BoardRecognizer(byte[] detectorModel, byte[] classifierModel)
    {
        _detector = new InferenceSession(detectorModel);
        _classifier = new InferenceSession(classifierModel);
    }

    public RecognizedBoard Analyze(SKBitmap image, Calibration? cal, string side = "w", bool reverseRows = false)
    {
        var boxes = RunDetector(image).Where(b => b.Conf >= 0.5f).ToList(); // 提高阈值，滤掉光晕/提示点杂框（真棋子通常 0.9+）
        if (cal is not null)
        {
            var minX = cal.Xs.Min();
            var maxX = cal.Xs.Max();
            var minY = cal.Ys.Min();
            var maxY = cal.Ys.Max();
            var m = cal.CropMargin;
            boxes = boxes.Where(b =>
                minX - m <= b.Cx && b.Cx <= maxX + m &&
                minY - m <= b.Cy && b.Cy <= maxY + m).ToList();
        }

        var dets = new List<Detection>();
        foreach (var b in boxes)
        {
            var (name, prob, alts) = ClassifyRoiTopK(image, b);
            if (name is null) continue;
            var x1 = (int)b.X;
            var y1 = (int)b.Y;
            var x2 = Math.Min(image.Width, (int)(b.X + b.W));
            var y2 = Math.Min(image.Height, (int)(b.Y + b.H));
            using var roi = ImageOps.Crop(image, x1, y1, x2, y2);
            var color = DetermineColor(roi);
            var fen = PieceToFen[name];
            fen = color == "red" ? fen.ToUpperInvariant() : fen.ToLowerInvariant();
            var altFen = alts.Select(a => (
                color == "red" ? PieceToFen[a.Name].ToUpperInvariant() : PieceToFen[a.Name].ToLowerInvariant(),
                a.Prob)).ToList();
            b.Piece = fen;
            b.Color = color;
            b.ClassName = name;
            b.Prob = prob;
            b.Alts = altFen;
            dets.Add(b);
        }

        var (board, cells) = DetectionsToBoard(dets, cal, reverseRows);
        ApplyLegalityRules(cells);
        board = RebuildBoard(cells);
        var fenOut = BoardToFen(board, side);
        return new RecognizedBoard { Board = board, Cells = cells, Fen = fenOut };
    }

    public List<Detection> RunDetector(SKBitmap img)
    {
        var (tensor, scale, offx, offy) = ImageOps.LetterboxRgbChw(img, ImageOps.DetSize);
        var input = new DenseTensor<float>(tensor, new[] { 1, 3, ImageOps.DetSize, ImageOps.DetSize });
        var output = _detector.Run(new[] { NamedOnnxValue.CreateFromTensor("images", input) }).ToArray();
        var outTensor = output[0].AsTensor<float>();
        output[0].Dispose();
        var dims = outTensor.Dimensions;
        if (dims.Length != 3)
            throw new InvalidOperationException(
                $"unexpected detector output shape: {string.Join("x", dims.ToArray())}");

        var buf = outTensor.ToArray();

        // YOLO output is [1,5,8400] (attrs first) or [1,8400,5] (anchors first).
        var attrsFirst = dims[1] < dims[2];
        var anchors = attrsFirst ? (int)dims[2] : (int)dims[1];
        float Get(int k, int anchor) => attrsFirst ? buf[k * anchors + anchor] : buf[anchor * 5 + k];

        var boxes = new List<Detection>();
        for (var i = 0; i < anchors; i++)
        {
            var conf = Get(4, i);
            if (conf < ConfThreshold) continue;
            var cx = (Get(0, i) - offx) / scale;
            var cy = (Get(1, i) - offy) / scale;
            var w = Get(2, i) / scale;
            var h = Get(3, i) / scale;
            if (cx < 0 || cx > img.Width || cy < 0 || cy > img.Height || w <= 0 || h <= 0)
                continue;
            boxes.Add(new Detection
            {
                X = cx - w / 2, Y = cy - h / 2, W = w, H = h,
                Cx = cx, Cy = cy, Conf = conf
            });
        }
        return Nms(boxes);
    }

    /// <summary>
    /// 仅用检测器得到棋盘占位（哪些格有子），不分类棋子类型。
    /// 我方黑(reverseRows)时 JJ 把棋盘旋转 180° 呈现，故行和列都要翻转：row=9-row、col=8-col。
    /// minConf 提到 0.5 以滤掉走棋高亮光晕产生的低置信度杂框（真棋子通常 0.9+）。
    /// </summary>
    public Occupancy BuildOccupancy(SKBitmap img, Calibration? cal, bool reverseRows = false, int? pinnedColOffset = null, int? pinnedRowOffset = null, float minConf = 0.5f)
    {
        var boxes = RunDetector(img).Where(b => b.Conf >= minConf).ToList();
        var occ = new Occupancy();
        if (cal is not null)
        {
            var fMinX = cal.Xs.Min(); var fMaxX = cal.Xs.Max();
            var fMinY = cal.Ys.Min(); var fMaxY = cal.Ys.Max();
            // 用预估格距 20% 作为最小余量（≥30px），避免 YOLO 检测框中心在棋盘边缘被过度裁剪。
            var estW = (fMaxX - fMinX) / 8.0;
            var estH = (fMaxY - fMinY) / 9.0;
            var margin = Math.Max(cal.CropMargin, (int)Math.Max(30, Math.Min(estW, estH) * 0.2));
            boxes = boxes.Where(b =>
                fMinX - margin <= b.Cx && b.Cx <= fMaxX + margin &&
                fMinY - margin <= b.Cy && b.Cy <= fMaxY + margin).ToList();
        }
        if (boxes.Count == 0) return occ;

        double minX, maxX, minY, maxY;
        if (cal is not null)
        {
            minX = cal.Xs.Min(); maxX = cal.Xs.Max();
            minY = cal.Ys.Min(); maxY = cal.Ys.Max();
        }
        else
        {
            minX = boxes.Min(d => d.Cx); maxX = boxes.Max(d => d.Cx);
            minY = boxes.Min(d => d.Cy); maxY = boxes.Max(d => d.Cy);
            var cell = ((maxX - minX) / 8 + (maxY - minY) / 9) / 2;
            minX -= cell * 0.5; maxX += cell * 0.5;
            minY -= cell * 0.5; maxY += cell * 0.5;
        }
        var cellw = (maxX - minX) / 8;
        var cellh = (maxY - minY) / 9;

        if (pinnedColOffset is not null && pinnedRowOffset is not null)
        {
            occ.ColOffset = pinnedColOffset.Value;
            occ.RowOffset = pinnedRowOffset.Value;
        }
        else
        {
            var (fitCol, fitRow) = FitGridOffsets(boxes, minX, minY, cellw, cellh);
            occ.ColOffset = pinnedColOffset ?? fitCol;
            occ.RowOffset = pinnedRowOffset ?? fitRow;
        }
        var colOffset = occ.ColOffset;
        var rowOffset = occ.RowOffset;

        const float offGridThreshold = 0.45f; // 与 TrackingAnalysisService.SettleThreshold 一致
        foreach (var d in boxes)
        {
            var rowFd = (d.Cy - minY) / cellh;
            var colFd = (d.Cx - minX) / cellw;
            var row = Math.Clamp((int)Math.Round(rowFd) + rowOffset, 0, 9);
            var col = Math.Clamp((int)Math.Round(colFd) + colOffset, 0, 8);
            var frac = Math.Max(Math.Abs(rowFd - Math.Round(rowFd)), Math.Abs(colFd - Math.Round(colFd)));
            if (frac > occ.OffGridFraction) occ.OffGridFraction = (float)frac;
            if (frac > offGridThreshold) occ.OffGridCount++;
            if (reverseRows) { row = 9 - row; col = 8 - col; } // 我方黑：180° 翻转
            if ((uint)row < 10 && (uint)col < 9)
                occ.Filled[row, col] = true;
        }
        return occ;
    }

    /// <summary>
    /// 网格拟合：在 [-2,2]² 偏移候选里选"高置信框中心离格点总距离最小"者。
    /// 替代旧的 ComputeColOffset（中兵吸附）和 ComputeRowOffset（最上棋子对 row 0），
    /// 两条路径坐标强制一致，残局不再错位。
    /// </summary>
    private static (int ColOffset, int RowOffset) FitGridOffsets(List<Detection> boxes, double minX, double minY, double cellw, double cellh)
    {
        if (cellw <= 0 || cellh <= 0 || boxes.Count == 0) return (0, 0);

        int bestCol = 0, bestRow = 0;
        double bestCost = double.MaxValue;

        for (var co = -2; co <= 2; co++)
        {
            for (var ro = -2; ro <= 2; ro++)
            {
                double totalDist = 0;
                var used = new HashSet<(int, int)>();
                foreach (var d in boxes)
                {
                    var colFd = (d.Cx - minX) / cellw;
                    var rowFd = (d.Cy - minY) / cellh;
                    var snapC = (int)Math.Round(colFd) + co;
                    var snapR = (int)Math.Round(rowFd) + ro;
                    if ((uint)snapC > 8 || (uint)snapR > 9) continue; // 出界
                    totalDist += Math.Abs(colFd - Math.Round(colFd)) + Math.Abs(rowFd - Math.Round(rowFd));
                    used.Add((snapR, snapC));
                }
                // 覆盖率惩罚：重叠（多子挤一格）和越界都增加代价
                var overlapPenalty = (boxes.Count - used.Count) * 0.5;
                var cost = totalDist + overlapPenalty;
                if (cost < bestCost) { bestCost = cost; bestCol = co; bestRow = ro; }
            }
        }
        return (bestCol, bestRow);
    }

    private static float Iou(Detection a, Detection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var inter = Math.Max(0f, x2 - x1) * Math.Max(0f, y2 - y1);
        var union = a.W * a.H + b.W * b.H - inter;
        return union > 0 ? inter / union : 0f;
    }

    private static List<Detection> Nms(List<Detection> boxes)
    {
        boxes = boxes.OrderByDescending(b => b.Conf).ToList();
        var keep = new List<Detection>();
        while (boxes.Count > 0)
        {
            var best = boxes[0];
            boxes.RemoveAt(0);
            keep.Add(best);
            boxes = boxes.Where(b => Iou(best, b) <= IouThreshold).ToList();
        }
        return keep;
    }

    private float[]? ClassifyRoiProbs(SKBitmap img, Detection box, float ratio)
    {
        var ex = box.W * ratio;
        var ey = box.H * ratio;
        var x1 = Math.Max(0, (int)(box.X - ex));
        var y1 = Math.Max(0, (int)(box.Y - ey));
        var x2 = Math.Min(img.Width, (int)(box.X + box.W + ex));
        var y2 = Math.Min(img.Height, (int)(box.Y + box.H + ey));
        if (x2 - x1 <= 0 || y2 - y1 <= 0) return null;
        var tensor = ImageOps.CropResizeRgbChw(img, x1, y1, x2, y2, ImageOps.ClsSize);
        var input = new DenseTensor<float>(tensor, new[] { 1, 3, ImageOps.ClsSize, ImageOps.ClsSize });
        var output = _classifier.Run(new[] { NamedOnnxValue.CreateFromTensor("images", input) }).ToArray();
        var probs = output[0].AsTensor<float>();
        var flat = probs.ToArray();
        output[0].Dispose();
        return flat;
    }

    private (string? Name, float Prob, List<(string Name, float Prob)> Alts) ClassifyRoiTopK(
        SKBitmap img, Detection box, int k = 3)
    {
        var agg = new Dictionary<string, float>();
        var primaryScores = new Dictionary<string, float>();
        foreach (var ratio in Expansions)
        {
            var probs = ClassifyRoiProbs(img, box, ratio);
            if (probs is null) continue;
            var order = Enumerable.Range(0, probs.Length).OrderByDescending(i => probs[i]).Take(k).ToArray();
            if (PrimaryRatios.Any(r => Math.Abs(ratio - r) < 1e-9))
            {
                var n0 = ClassNames[order[0]];
                var p0 = probs[order[0]];
                if (!primaryScores.TryGetValue(n0, out var ps) || p0 > ps)
                    primaryScores[n0] = p0;
            }
            foreach (var idx in order)
            {
                var name = ClassNames[idx];
                var p = probs[idx];
                if (p < 0.01f) continue;
                if (!agg.TryGetValue(name, out var ap) || p > ap)
                    agg[name] = p;
            }
        }
        if (agg.Count == 0)
            return (null, 0f, new());
        var top1 = primaryScores.Count > 0
            ? primaryScores.OrderByDescending(kv => kv.Value).First().Key
            : agg.OrderByDescending(kv => kv.Value).First().Key;
        var top1Prob = primaryScores.TryGetValue(top1, out var tp) ? tp : agg[top1];
        var alts = agg.OrderByDescending(kv => kv.Value).Take(k)
            .Select(kv => (kv.Key, kv.Value)).ToList();
        return (top1, top1Prob, alts);
    }

    private static string DetermineColor(SKBitmap roi)
    {
        var w = roi.Width;
        var h = roi.Height;
        var cw = Math.Max(1, (int)(w * 0.5));
        var ch = Math.Max(1, (int)(h * 0.5));
        var cx1 = (int)(w * 0.25);
        var cy1 = (int)(h * 0.25);
        using var center = ImageOps.Crop(roi, cx1, cy1, cx1 + cw, cy1 + ch);
        var (rgba, _, _) = ImageOps.ToRgba(center);
        var scores = new List<int>(rgba.Length / 4);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var r = rgba[i];
            var g = rgba[i + 1];
            var b = rgba[i + 2];
            scores.Add(r - Math.Max(g, b));
        }
        scores.Sort();
        var rank = (scores.Count - 1) * 0.95;
        var lo = (int)Math.Floor(rank);
        var hi = Math.Min(scores.Count - 1, lo + 1);
        var frac = (float)(rank - lo);
        var value = scores[lo] + (scores[hi] - scores[lo]) * frac;
        return value > 60 ? "red" : "black";
    }

    private static (string?[,] Board, Dictionary<(int, int), CellInfo> Cells) DetectionsToBoard(
        List<Detection> dets, Calibration? cal, bool reverseRows = false)
    {
        var cells = new Dictionary<(int, int), CellInfo>();
        var board = new string?[10, 9];
        if (dets.Count == 0)
            return (board, cells);

        float minX, maxX, minY, maxY;
        if (cal is not null)
        {
            minX = cal.Xs.Min(); maxX = cal.Xs.Max();
            minY = cal.Ys.Min(); maxY = cal.Ys.Max();
        }
        else
        {
            minX = dets.Min(d => d.Cx); maxX = dets.Max(d => d.Cx);
            minY = dets.Min(d => d.Cy); maxY = dets.Max(d => d.Cy);
            var cell = ((maxX - minX) / 8 + (maxY - minY) / 9) / 2;
            minX -= cell * 0.5f; maxX += cell * 0.5f;
            minY -= cell * 0.5f; maxY += cell * 0.5f;
        }
        var cellw = (maxX - minX) / 8;
        var cellh = (maxY - minY) / 9;

        var raw = new List<(int Row, int Col, Detection D)>();
        var pawns = new List<(int Col, Detection D)>();
        foreach (var d in dets)
        {
            var row = (int)Math.Round((d.Cy - minY) / cellh);
            var col = (int)Math.Round((d.Cx - minX) / cellw);
            if (reverseRows)
                row = 9 - row; // 我方执黑时黑方在截图下方：翻转行，让红方回到标准 FEN 的 rank 0
            raw.Add((row, col, d));
            if (d.Piece is "P" or "p")
                pawns.Add((col, d));
        }

        var colOffset = 0;
        if (pawns.Count > 0)
        {
            var best = pawns.OrderBy(pc => Math.Abs(pc.Col - 4)).First();
            colOffset = 4 - best.Col;
        }

        foreach (var (row, col, d) in raw)
        {
            var c = col + colOffset;
            if (row >= 0 && row < 10 && c >= 0 && c < 9)
            {
                cells[(row, c)] = new CellInfo
                {
                    Piece = d.Piece,
                    Alts = d.Alts,
                    Prob = d.Prob,
                    Corrected = false
                };
            }
        }

        RebuildInto(board, cells);
        return (board, cells);
    }

    private static string?[,] RebuildBoard(Dictionary<(int Row, int Col), CellInfo> cells)
    {
        var board = new string?[10, 9];
        RebuildInto(board, cells);
        return board;
    }

    private static void RebuildInto(string?[,] board, Dictionary<(int Row, int Col), CellInfo> cells)
    {
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
                board[r, c] = cells.TryGetValue((r, c), out var cell) ? cell.Piece : null;
    }

    private static void ApplyLegalityRules(Dictionary<(int Row, int Col), CellInfo> cells)
    {
        var passes = 0;
        while (passes < 10)
        {
            var changed = false;
            var cnt = new Dictionary<char, int>();
            foreach (var c in cells.Values)
            {
                if (c.Piece.Length == 1)
                    cnt[c.Piece[0]] = cnt.GetValueOrDefault(c.Piece[0]) + 1;
            }

            foreach (var kv in cells.ToList())
            {
                var key = kv.Key;
                var cell = kv.Value;
                var top = cell.Piece[0];
                if (top is 'b' or 'B' && !XiangSquares.Contains((key.Row, key.Col)))
                {
                    var repl = NextAlt(cell, top, cnt);
                    if (repl is not null)
                    {
                        cnt[top]--;
                        cnt[repl.Value] = cnt.GetValueOrDefault(repl.Value) + 1;
                        cell.Piece = repl.Value.ToString();
                        cell.Corrected = true;
                        changed = true;
                    }
                }
            }

            foreach (var piece in cnt.Keys.ToList())
            {
                if (cnt[piece] > MaxCounts[piece])
                {
                    var offenders = cells.Where(kv => kv.Value.Piece.Length == 1 && kv.Value.Piece[0] == piece)
                        .Select(kv => kv.Key).ToList();
                    var worst = offenders.OrderBy(k => cells[k].Prob).First();
                    var repl = NextAlt(cells[worst], piece, cnt);
                    if (repl is not null)
                    {
                        cnt[piece]--;
                        cnt[repl.Value] = cnt.GetValueOrDefault(repl.Value) + 1;
                        cells[worst].Piece = repl.Value.ToString();
                        cells[worst].Corrected = true;
                        changed = true;
                    }
                }
            }

            if (!changed) break;
            passes++;
        }
    }

    private static char? NextAlt(CellInfo cell, char exclude, Dictionary<char, int> cnt)
    {
        foreach (var (fen, _prob) in cell.Alts)
        {
            if (fen.Length != 1) continue;
            var f = fen[0];
            if (f == exclude || !MaxCounts.ContainsKey(f)) continue;
            if (cnt.GetValueOrDefault(f) < MaxCounts[f])
                return f;
        }
        return null;
    }

    public static string BoardToFen(string?[,] board, string side = "w")
    {
        var rows = new List<string>();
        for (var r = 0; r < 10; r++)
        {
            var s = "";
            var e = 0;
            for (var c = 0; c < 9; c++)
            {
                var p = board[r, c];
                if (p is null) e++;
                else
                {
                    if (e > 0) { s += e; e = 0; }
                    s += p;
                }
            }
            if (e > 0) s += e;
            rows.Add(s);
        }
        return string.Join("/", rows) + " " + side;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _classifier.Dispose();
    }
}
