using System.Diagnostics;

namespace XiangqiCore.Gomoku;

public readonly record struct GomokuMove(int Row, int Col)
{
    public string Name => GomokuPosition.CellNameFull(Row, Col);
}

public sealed record GomokuSearchResult(GomokuMove? Move, double Score, int Depth, bool ForcedWin, string? Note)
{
    public static GomokuSearchResult None(string note) => new(null, 0, 0, false, note);
}

/// <summary>
/// 进程内五子棋搜索引擎(无禁手):候选点启发 + 模式评估 + 迭代加深 alpha-beta + VCF 冲四杀。
/// 定位是打赢休闲对局:必挡活三/冲四、会做双威胁,非 Gomocup 冠军级。
/// </summary>
public sealed class GomokuEngine
{
    private const int CandidateRadius = 2;   // 候选点与已有子的切比雪夫距离上限
    private const int MaxCandidates = 20;    // 每节点参与排序搜索的候选数
    private const int MaxVcfDepth = 24;      // VCF 半回合上限(12 次连续冲四)

    // 模式分:窗口内计数己方子 + 空位,两端开放情况决定"活/眠"
    private const long ScoreFive = 10_000_000L;
    private const long ScoreOpenFour = 1_000_000L;
    private const long ScoreFour = 100_000L;
    private const long ScoreOpenThree = 10_000L;
    private const long ScoreThree = 1_000L;
    private const long ScoreOpenTwo = 100L;

    private readonly Stopwatch _clock = new();
    private long _deadlineMs;
    private bool _aborted;
    private long _nodes;

    /// <summary>搜索当前轮到方的最佳着法。timeBudgetMs 为硬预算(内部留 10% 冗余)。</summary>
    public GomokuSearchResult Search(GomokuPosition pos, int timeBudgetMs)
    {
        _clock.Restart();
        _deadlineMs = _clock.ElapsedMilliseconds + (long)(timeBudgetMs * 0.9);
        _aborted = false;
        _nodes = 0;

        var me = pos.SideToMove;
        if (pos.BlackCount + pos.WhiteCount == 0)
            return new GomokuSearchResult(new GomokuMove(7, 7), 0, 0, false, "空盘,直取天元");

        // 1. 立即着法预判:己方任一点成五 → 直接下
        foreach (var (r, c) in EmptyCells(pos))
            if (pos.IsWinningAt(r, c, me))
                return new GomokuSearchResult(new GomokuMove(r, c), ScoreFive, 0, true, "成五!");

        // 2. 对方下一手能成五的点 → 必挡(挡点即对方成五点)
        var opp = me == GomokuStone.Black ? GomokuStone.White : GomokuStone.Black;
        var mustBlock = new List<GomokuMove>();
        foreach (var (r, c) in EmptyCells(pos))
            if (pos.IsWinningAt(r, c, opp))
                mustBlock.Add(new GomokuMove(r, c));
        if (mustBlock.Count > 0)
        {
            // 若挡点同时自己也接近成五,Search 会处理;但成五已排除,故唯一合理动作就是挡
            if (mustBlock.Count == 1)
                return new GomokuSearchResult(mustBlock[0], ScoreFour, 0, true, "挡对方成五点");
            // 对方双五点无解,仍挡其一(评分说明)
            return new GomokuSearchResult(mustBlock[0], -ScoreFive, 0, false, "对方双成五点,已尽力一挡");
        }

        // 3. VCF 冲四杀:己方连续冲四直到成五,对方只能被动跟挡
        var vcf = SearchVcf(pos, me);
        if (vcf is not null)
            return new GomokuSearchResult(vcf.Value, ScoreFive, 0, true, "VCF 连续冲四杀");

        // 4. 迭代加深 alpha-beta
        GomokuMove? best = null;
        double bestScore = 0;
        var reachedDepth = 0;
        for (var depth = 2; depth <= 10; depth += 2)
        {
            var (move, score) = RootSearch(pos, me, depth);
            if (_aborted) break;
            best = move;
            bestScore = score;
            reachedDepth = depth;
            if (score >= ScoreFive) break; // 已找到必胜
        }

        if (best is null)
        {
            // 预算内连深度 2 都没搜完(极罕见):退回第一候选
            var cands = OrderCandidates(pos, me, int.MaxValue);
            if (cands.Count == 0) return GomokuSearchResult.None("棋盘已满");
            best = cands[0];
            bestScore = 0;
            reachedDepth = 0;
        }

        var note = bestScore >= ScoreOpenFour ? "必胜在手"
            : bestScore <= -ScoreOpenFour ? "对方威胁极大,尽力防守"
            : null;
        return new GomokuSearchResult(best, bestScore, reachedDepth, bestScore >= ScoreFive, note);
    }

    // ---------- 候选点生成与排序 ----------

    private static List<(int Row, int Col)> EmptyCells(GomokuPosition pos)
    {
        var list = new List<(int, int)>();
        for (var r = 0; r < GomokuPosition.Size; r++)
            for (var c = 0; c < GomokuPosition.Size; c++)
                if (pos[r, c] is null) list.Add((r, c));
        return list;
    }

    /// <summary>候选点:与任一已有子切比雪夫距离 ≤2 的空点,按(己方进攻分 + 对方威胁分)降序。</summary>
    private List<GomokuMove> OrderCandidates(GomokuPosition pos, GomokuStone me, int maxCount)
    {
        var opp = me == GomokuStone.Black ? GomokuStone.White : GomokuStone.Black;
        var near = new HashSet<(int, int)>();
        for (var r = 0; r < GomokuPosition.Size; r++)
            for (var c = 0; c < GomokuPosition.Size; c++)
            {
                if (pos[r, c] is null) continue;
                for (var dr = -CandidateRadius; dr <= CandidateRadius; dr++)
                    for (var dc = -CandidateRadius; dc <= CandidateRadius; dc++)
                    {
                        var nr = r + dr; var nc = c + dc;
                        if (nr >= 0 && nr < GomokuPosition.Size && nc >= 0 && nc < GomokuPosition.Size &&
                            pos[nr, nc] is null)
                            near.Add((nr, nc));
                    }
            }

        var scored = new List<(GomokuMove Move, long Score)>();
        foreach (var (r, c) in near)
        {
            long score = 0;
            foreach (var (dr, dc) in DirectionsAll)
                score += LineScore(pos.Cells, r, c, dr, dc, me);
            foreach (var (dr, dc) in DirectionsAll)
                score += LineScore(pos.Cells, r, c, dr, dc, opp) / 2; // 防守价值减半
            scored.Add((new GomokuMove(r, c), score));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(maxCount).Select(x => x.Move).ToList();
    }

    // ---------- 评估:以某空点为中心的四方向模式分 ----------

    private static readonly (int, int)[] DirectionsAll = { (0, 1), (1, 0), (1, 1), (1, -1) };

    /// <summary>
    /// 假设 stone 落在 (row,col) 后,该方向上连续模式的分值。
    /// 以落点为中心向两侧延伸统计"连子数 + 两端开放性",映射到 活/眠 模式分。
    /// </summary>
    private static long LineScore(GomokuStone?[,] cells, int row, int col, int dr, int dc, GomokuStone stone)
    {
        const int size = GomokuPosition.Size;
        var count = 1;
        var openA = false; var openB = false;

        int k = 1;
        while (true)
        {
            var r = row + dr * k; var c = col + dc * k;
            if (r < 0 || r >= size || c < 0 || c >= size) break;
            if (cells[r, c] == stone) { count++; k++; continue; }
            openA = cells[r, c] is null;
            break;
        }
        k = 1;
        while (true)
        {
            var r = row - dr * k; var c = col - dc * k;
            if (r < 0 || r >= size || c < 0 || c >= size) break;
            if (cells[r, c] == stone) { count++; k++; continue; }
            openB = cells[r, c] is null;
            break;
        }

        var opens = (openA ? 1 : 0) + (openB ? 1 : 0);
        return count switch
        {
            >= 5 => ScoreFive,
            4 => opens == 2 ? ScoreOpenFour : opens == 1 ? ScoreFour : 0,
            3 => opens == 2 ? ScoreOpenThree : opens == 1 ? ScoreThree : 0,
            2 => opens == 2 ? ScoreOpenTwo : 0,
            _ => 0,
        };
    }

    /// <summary>
    /// 叶子节点评估(准动态):先按轮走方修正成五威胁 ——
    /// 己方有成五点 = 胜;对方成五点 ≥2 = 败(堵不胜);==1 = 视为被迫堵上后再静态评。
    /// 纯静态评估会把"对方已有四但轮我走"误判成必败(轮走方其实能堵),这里修正之。
    /// </summary>
    private static long Evaluate(GomokuPosition pos, GomokuStone me)
    {
        var opp = me == GomokuStone.Black ? GomokuStone.White : GomokuStone.Black;
        var (myBest, oppBest, oppFives) = ScanBest(pos, me, opp);
        if (myBest >= ScoreFive) return ScoreFive;
        if (oppFives.Count >= 2) return -ScoreFive;
        if (oppFives.Count == 1)
        {
            // 唯一成五点:轮走方必然堵上,按堵完后的局面重评(一层,不递归)
            var blocked = pos.Apply(oppFives[0].Row, oppFives[0].Col, me);
            var (my2, opp2, _) = ScanBest(blocked, me, opp);
            return my2 - opp2 * 2;
        }
        return myBest - oppBest * 2; // 防守权重加倍
    }

    /// <summary>扫描双方最优"假设落子点"的模式分,并收集对方的成五点。</summary>
    private static (long MyBest, long OppBest, List<(int Row, int Col)> OppFives) ScanBest(
        GomokuPosition pos, GomokuStone me, GomokuStone opp)
    {
        long myBest = 0, oppBest = 0;
        var oppFives = new List<(int, int)>();
        for (var r = 0; r < GomokuPosition.Size; r++)
            for (var c = 0; c < GomokuPosition.Size; c++)
            {
                if (pos[r, c] is not null) continue;
                long s = 0, t = 0;
                foreach (var (dr, dc) in DirectionsAll)
                {
                    s += LineScore(pos.Cells, r, c, dr, dc, me);
                    t += LineScore(pos.Cells, r, c, dr, dc, opp);
                }
                if (s > myBest) myBest = s;
                if (t > oppBest) oppBest = t;
                if (t >= ScoreFive) oppFives.Add((r, c));
            }
        return (myBest, oppBest, oppFives);
    }

    // ---------- alpha-beta ----------

    private (GomokuMove? Move, double Score) RootSearch(GomokuPosition pos, GomokuStone me, int depth)
    {
        var cands = OrderCandidates(pos, me, MaxCandidates);
        if (cands.Count == 0) return (null, 0);
        GomokuMove? best = null;
        var alpha = double.NegativeInfinity;
        var beta = double.PositiveInfinity;
        foreach (var mv in cands)
        {
            var next = pos.Apply(mv.Row, mv.Col, me);
            var score = -Negamax(next, depth - 1, -beta, -alpha);
            if (_aborted) break;
            if (score > alpha || best is null) { alpha = score; best = mv; }
        }
        return (best, alpha);
    }

    private double Negamax(GomokuPosition pos, int depth, double alpha, double beta)
    {
        if ((_nodes++ & 1023) == 0 && _clock.ElapsedMilliseconds > _deadlineMs)
        {
            _aborted = true;
            return 0;
        }

        var me = pos.SideToMove;

        // 己方任意空点成五 → 当前方胜(上一手留下的必胜点)
        foreach (var (r, c) in EnumerateNearOrAll(pos))
            if (pos.IsWinningAt(r, c, me))
                return ScoreFive - (10 - depth); // 越浅的胜越优

        if (depth <= 0)
            return Evaluate(pos, me);

        var cands = OrderCandidates(pos, me, MaxCandidates);
        if (cands.Count == 0) return 0;

        var best = double.NegativeInfinity;
        foreach (var mv in cands)
        {
            var next = pos.Apply(mv.Row, mv.Col, me);
            var score = -Negamax(next, depth - 1, -beta, -alpha);
            if (_aborted) return best == double.NegativeInfinity ? 0 : best;
            if (score > best) best = score;
            if (best > alpha) alpha = best;
            if (alpha >= beta) break;
        }
        return best;
    }

    private static IEnumerable<(int Row, int Col)> EnumerateNearOrAll(GomokuPosition pos)
    {
        // 子多时只看邻近点即可(成五点必与己子相邻),空旷时退回全盘
        var any = false;
        for (var r = 0; r < GomokuPosition.Size; r++)
            for (var c = 0; c < GomokuPosition.Size; c++)
            {
                if (pos[r, c] is null) continue;
                any = true;
                for (var dr = -5; dr <= 5; dr++)
                    for (var dc = -5; dc <= 5; dc++)
                    {
                        var nr = r + dr; var nc = c + dc;
                        if (nr >= 0 && nr < GomokuPosition.Size && nc >= 0 && nc < GomokuPosition.Size &&
                            pos[nr, nc] is null)
                            yield return (nr, nc);
                    }
            }
        if (!any)
            for (var r = 0; r < GomokuPosition.Size; r++)
                for (var c = 0; c < GomokuPosition.Size; c++)
                    yield return (r, c);
    }

    // ---------- VCF:连续冲四杀 ----------

    /// <summary>己方只走"冲四"(落子后形成四,对方必须挡)直至成五。找不到返回 null。</summary>
    private GomokuMove? SearchVcf(GomokuPosition pos, GomokuStone me)
    {
        return Vcf(pos, me, 0);
    }

    private GomokuMove? Vcf(GomokuPosition pos, GomokuStone me, int ply)
    {
        if (ply >= MaxVcfDepth) return null;
        if ((_nodes++ & 255) == 0 && _clock.ElapsedMilliseconds > _deadlineMs) return null;

        var opp = me == GomokuStone.Black ? GomokuStone.White : GomokuStone.Black;
        var board = pos.Cells;

        foreach (var (r, c) in EnumerateNearOrAll(pos))
        {
            if (pos[r, c] is not null) continue;
            if (!IsFourMove(board, r, c, me)) continue; // 只扩展冲四点

            // 落子成五直接赢
            if (pos.IsWinningAt(r, c, me)) return new GomokuMove(r, c);

            // 对方必须挡在成五点;枚举我方落子后的成五点(冲四的挡点唯一或成双四)
            var next = pos.Apply(r, c, me);
            var blockPoints = new List<GomokuMove>();
            foreach (var (br, bc) in EnumerateNearOrAll(next))
                if (next.IsWinningAt(br, bc, me)) blockPoints.Add(new GomokuMove(br, bc));

            if (blockPoints.Count == 0) continue;      // 假冲四,无后续
            if (blockPoints.Count >= 2) return new GomokuMove(r, c); // 双威胁,对方挡不住

            // 对方挡唯一成五点后继续冲
            var blocked = next.Apply(blockPoints[0].Row, blockPoints[0].Col, opp);
            var child = Vcf(blocked, me, ply + 2);
            if (child is not null) return new GomokuMove(r, c);
        }
        return null;
    }

    /// <summary>(row,col) 落 stone 后是否形成"四"(含活四/冲四:即下一手存在成五点)。</summary>
    private static bool IsFourMove(GomokuStone?[,] cells, int row, int col, GomokuStone stone)
    {
        var test = (GomokuStone?[,])cells.Clone();
        test[row, col] = stone;
        for (var dr = -1; dr <= 1; dr++)
            for (var dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                // 检查落点周围 5 格内是否存在成五点
                for (var k = -4; k <= 4; k++)
                {
                    var r = row + dr * k; var c = col + dc * k;
                    if (r < 0 || r >= GomokuPosition.Size || c < 0 || c >= GomokuPosition.Size) continue;
                    if (test[r, c] is not null) continue;
                    if (GomokuPosition.IsWinningAt(test, r, c, stone)) return true;
                }
            }
        return false;
    }
}
