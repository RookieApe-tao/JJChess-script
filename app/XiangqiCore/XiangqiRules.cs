using System.Text;

namespace XiangqiCore;

/// <summary>
/// 一步走法（棋盘坐标，沿用 <see cref="Notation.ParseFen"/> 的约定）。
/// board[0] = 黑方底线 / 屏幕顶部；board[9] = 红方底线 / 屏幕底部；大写 = 红。
/// UCI：file a..i = col 0..8，rank 0..9，rank 0 = 红方底线 = board[9]，故 rank = 9 - row。
/// </summary>
public readonly record struct Move(int FromRow, int FromCol, int ToRow, int ToCol)
{
    public string ToUci() =>
        $"{(char)('a' + FromCol)}{9 - FromRow}{(char)('a' + ToCol)}{9 - ToRow}";

    public static Move FromUci(string u)
    {
        var fc = u[0] - 'a';
        var fr = 9 - (u[1] - '0');
        var tc = u[2] - 'a';
        var tr = 9 - (u[3] - '0');
        return new Move(fr, fc, tr, tc);
    }

    public bool IsCapture(char?[,] board) =>
        board[ToRow, ToCol] is not null;
}

/// <summary>
/// 纯 C# 象棋走法生成与合法性判定。不依赖引擎（引擎仅作测试裁判）。
/// 约定：board[0] = 黑方底线（屏幕顶部），大写 = 红；红向 row 递减方向走。
/// 楚河汉界在 row 4/5 之间：红本方 rows 5..9，黑本方 rows 0..4。
/// 红宫 rows 7..9 cols 3..5，黑宫 rows 0..2 cols 3..5。
/// </summary>
public static class XiangqiRules
{
    // -------- 几何 --------
    public static bool InBoard(int r, int c) => (uint)r < 10 && (uint)c < 9;

    public static bool InPalace(int r, int c, bool red) =>
        red ? (r is 7 or 8 or 9 && c is 3 or 4 or 5)
            : (r is 0 or 1 or 2 && c is 3 or 4 or 5);

    public static bool OnOwnSide(int r, bool red) => red ? r >= 5 : r <= 4;

    public static bool IsRedChar(char p) => char.IsUpper(p);

    public static bool IsEnemy(char? p, bool moverRed) =>
        p.HasValue && IsRedChar(p.Value) != moverRed;

    public static bool IsFriend(char? p, bool moverRed) =>
        p.HasValue && IsRedChar(p.Value) == moverRed;

    // -------- 伪走法（不滤己方被将） --------
    /// <summary>从 (fr,fc) 出发的全部伪合法落点（含吃子）。调用方需保证该格有子。</summary>
    public static List<(int R, int C)> PseudoFrom(char?[,] b, int fr, int fc)
    {
        var dests = new List<(int, int)>();
        var piece = b[fr, fc] ?? throw new ArgumentException($"empty cell ({fr},{fc})");
        var red = IsRedChar(piece);
        switch (char.ToUpperInvariant(piece))
        {
            case 'K': GenKing(b, fr, fc, red, dests); break;
            case 'A': GenAdvisor(b, fr, fc, red, dests); break;
            case 'B': GenElephant(b, fr, fc, red, dests); break;
            case 'N': GenKnight(b, fr, fc, red, dests); break;
            case 'R': GenRook(b, fr, fc, red, dests); break;
            case 'C': GenCannon(b, fr, fc, red, dests); break;
            case 'P': GenPawn(b, fr, fc, red, dests); break;
        }
        return dests;
    }

    private static void TryAdd(char?[,] b, int r, int c, bool red, List<(int, int)> dst)
    {
        if (!InBoard(r, c)) return;
        if (IsFriend(b[r, c], red)) return; // 己方挡住，不能落
        dst.Add((r, c));
    }

    private static void GenKing(char?[,] b, int fr, int fc, bool red, List<(int, int)> dst)
    {
        foreach (var (dr, dc) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var (r, c) = (fr + dr, fc + dc);
            if (InPalace(r, c, red)) TryAdd(b, r, c, red, dst);
        }
    }

    private static void GenAdvisor(char?[,] b, int fr, int fc, bool red, List<(int, int)> dst)
    {
        foreach (var (dr, dc) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            var (r, c) = (fr + dr, fc + dc);
            if (InPalace(r, c, red)) TryAdd(b, r, c, red, dst);
        }
    }

    private static void GenElephant(char?[,] b, int fr, int fc, bool red, List<(int, int)> dst)
    {
        foreach (var (dr, dc) in new[] { (2, 2), (2, -2), (-2, 2), (-2, -2) })
        {
            var (r, c) = (fr + dr, fc + dc);
            if (!InBoard(r, c)) continue;
            if (!OnOwnSide(r, red)) continue;            // 象不过河
            if (b[fr + dr / 2, fc + dc / 2] is not null) continue; // 塞象眼
            TryAdd(b, r, c, red, dst);
        }
    }

    private static void GenKnight(char?[,] b, int fr, int fc, bool red, List<(int, int)> dst)
    {
        foreach (var (dr, dc) in new[]
        {
            (2, 1), (2, -1), (-2, 1), (-2, -1),
            (1, 2), (1, -2), (-1, 2), (-1, -2),
        })
        {
            var (r, c) = (fr + dr, fc + dc);
            if (!InBoard(r, c)) continue;
            // 蹩马腿：沿“日”字两步方向的第一步
            var (lr, lc) = Math.Abs(dr) == 2 ? (fr + dr / 2, fc) : (fr, fc + dc / 2);
            if (b[lr, lc] is not null) continue;
            TryAdd(b, r, c, red, dst);
        }
    }

    private static void GenRook(char?[,] b, int fr, int fc, bool red, List<(int, int)> dst)
    {
        foreach (var (dr, dc) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var (r, c) = (fr + dr, fc + dc);
            while (InBoard(r, c))
            {
                var p = b[r, c];
                if (p is null) { dst.Add((r, c)); r += dr; c += dc; continue; }
                if (IsEnemy(p, red)) dst.Add((r, c)); // 吃子后停下
                break;
            }
        }
    }

    private static void GenCannon(char?[,] b, int fr, int fc, bool red, List<(int, int)> dst)
    {
        foreach (var (dr, dc) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var (r, c) = (fr + dr, fc + dc);
            // 阶段0：无炮架，沿空格走（不吃）
            while (InBoard(r, c) && b[r, c] is null) { dst.Add((r, c)); r += dr; c += dc; }
            if (!InBoard(r, c)) continue;            // 遇到第一子 = 炮架
            r += dr; c += dc;
            // 阶段1：越过炮架后找下一子，若为敌则吃
            while (InBoard(r, c) && b[r, c] is null) { r += dr; c += dc; }
            if (InBoard(r, c) && IsEnemy(b[r, c], red)) dst.Add((r, c));
        }
    }

    private static void GenPawn(char?[,] b, int fr, int fc, bool red, List<(int, int)> dst)
    {
        var fwd = red ? fr - 1 : fr + 1;              // 红向上(row减)，黑向下(row增)
        TryAdd(b, fwd, fc, red, dst);                 // 永远可前进
        var crossed = red ? fr <= 4 : fr >= 5;        // 过河后可横走
        if (crossed)
        {
            TryAdd(b, fr, fc - 1, red, dst);
            TryAdd(b, fr, fc + 1, red, dst);
        }
    }

    // -------- 攻击 / 将军 --------

    /// <summary>(tr,tc) 是否被 `byRed` 方的车/炮/马/兵攻击。飞将另由 <see cref="KingsFace"/> 处理。</summary>
    public static bool IsAttacked(char?[,] b, int tr, int tc, bool byRed)
    {
        // 车 / 炮：沿四条正交线扫描
        foreach (var (dr, dc) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var (r, c) = (tr + dr, tc + dc);
            var screen = false;
            while (InBoard(r, c))
            {
                var p = b[r, c];
                if (p is null) { r += dr; c += dc; continue; }
                var ch = p.Value;
                var pred = IsRedChar(ch) == byRed;
                var up = char.ToUpperInvariant(ch);
                if (!screen)
                {
                    if (pred && up == 'R') return true; // 车沿空线将军
                    screen = true;                      // 第一子 = 炮架
                }
                else
                {
                    if (pred && up == 'C') return true; // 炮隔一子将军
                    break;                              // 炮架后第一子若非敌炮则到此为止
                }
                r += dr; c += dc;
            }
        }

        // 马：敌方马在“日”字反位且不蹩腿则攻击 (tr,tc)
        foreach (var (dr, dc) in new[]
        {
            (2, 1), (2, -1), (-2, 1), (-2, -1),
            (1, 2), (1, -2), (-1, 2), (-1, -2),
        })
        {
            var (kr, kc) = (tr - dr, tc - dc); // 反推马的位置
            if (!InBoard(kr, kc)) continue;
            var p = b[kr, kc];
            if (p is null) continue;
            var ch = p.Value;
            if (IsRedChar(ch) != byRed || char.ToUpperInvariant(ch) != 'N') continue;
            var (lr, lc) = Math.Abs(dr) == 2 ? (kr + dr / 2, kc) : (kr, kc + dc / 2);
            if (b[lr, lc] is null) return true; // 马腿空 → 可将军
        }

        // 兵：敌方兵在能吃到 (tr,tc) 的位置
        if (byRed)
        {
            // 红兵向上走(row减)：红兵在 (tr+1,tc) 可直进吃 (tr,tc)
            if (InBoard(tr + 1, tc) && b[tr + 1, tc] == 'P') return true;
            // 过河红兵(row<=4)可横走：同行的红兵可吃 (tr,tc)
            if (tr <= 4)
            {
                if (InBoard(tr, tc - 1) && b[tr, tc - 1] == 'P') return true;
                if (InBoard(tr, tc + 1) && b[tr, tc + 1] == 'P') return true;
            }
        }
        else
        {
            if (InBoard(tr - 1, tc) && b[tr - 1, tc] == 'p') return true;
            if (tr >= 5)
            {
                if (InBoard(tr, tc - 1) && b[tr, tc - 1] == 'p') return true;
                if (InBoard(tr, tc + 1) && b[tr, tc + 1] == 'p') return true;
            }
        }

        return false;
    }

    /// <summary>飞将（两王同列且中间无子）。</summary>
    public static bool KingsFace(char?[,] b)
    {
        int redR = -1, redC = -1, blkR = -1, blkC = -1;
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
            {
                if (b[r, c] == 'K') { redR = r; redC = c; }
                else if (b[r, c] == 'k') { blkR = r; blkC = c; }
            }
        if (redR < 0 || blkR < 0 || redC != blkC) return false;
        var lo = Math.Min(redR, blkR);
        var hi = Math.Max(redR, blkR);
        for (var r = lo + 1; r < hi; r++)
            if (b[r, redC] is not null) return false;
        return true;
    }

    /// <summary>红方/黑方王是否被将。</summary>
    public static bool InCheck(char?[,] b, bool redKing)
    {
        var king = redKing ? 'K' : 'k';
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
                if (b[r, c] == king)
                    return IsAttacked(b, r, c, byRed: !redKing) || KingsFace(b);
        return true; // 找不到王（已被吃）视为被将，阻止该非法走法
    }

    // -------- 落子（纯函数，不改输入） --------
    public static char?[,] CloneAndApply(char?[,] b, Move m)
    {
        var nb = (char?[,])b.Clone();
        nb[m.ToRow, m.ToCol] = nb[m.FromRow, m.FromCol];
        nb[m.FromRow, m.FromCol] = null;
        return nb;
    }

    /// <summary>走法是否合法：落子后己方王不被将。</summary>
    public static bool IsLegal(char?[,] b, Move m)
    {
        var red = IsRedChar(b[m.FromRow, m.FromCol]!.Value);
        var nb = CloneAndApply(b, m);
        return !InCheck(nb, red);
    }

    /// <summary>某方全部合法走法。</summary>
    public static List<Move> LegalMoves(char?[,] b, bool redToMove)
    {
        var moves = new List<Move>();
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
            {
                var p = b[r, c];
                if (p is null || IsRedChar(p.Value) != redToMove) continue;
                foreach (var (tr, tc) in PseudoFrom(b, r, c))
                {
                    var m = new Move(r, c, tr, tc);
                    if (IsLegal(b, m)) moves.Add(m);
                }
            }
        return moves;
    }

    /// <summary>从指定格出发的合法走法（匹配器用，更省）。</summary>
    public static List<Move> LegalMovesFrom(char?[,] b, int fr, int fc)
    {
        var moves = new List<Move>();
        foreach (var (tr, tc) in PseudoFrom(b, fr, fc))
        {
            var m = new Move(fr, fc, tr, tc);
            if (IsLegal(b, m)) moves.Add(m);
        }
        return moves;
    }
}
