using System.Text;

namespace XiangqiCore;

/// <summary>
/// Xiangqi FEN / UCI move -> Chinese notation.
/// Coordinates follow Pikafish UCI: files a..i (col 0..8, 'a' = FEN col 0),
/// ranks 0..9 (rank 0 = red home / bottom, rank 9 = black home / top).
/// </summary>
public static class Notation
{
    private static readonly Dictionary<char, string> RedPieces = new()
    {
        ['R'] = "车", ['N'] = "马", ['B'] = "相", ['A'] = "仕",
        ['K'] = "帅", ['C'] = "炮", ['P'] = "兵"
    };

    private static readonly Dictionary<char, string> BlackPieces = new()
    {
        ['r'] = "车", ['n'] = "马", ['b'] = "象", ['a'] = "士",
        ['k'] = "将", ['c'] = "炮", ['p'] = "卒"
    };

    private static readonly string RedNum = "一二三四五六七八九";
    private static readonly string BlackNum = "123456789";

    private static readonly HashSet<char> Straight = new("RCPKrcpk");

    public static (char?[,] Board, string Side) ParseFen(string fen)
    {
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var board = new char?[10, 9];
        var ranks = parts[0].Split('/');
        for (var r = 0; r < 10; r++)
        {
            var col = 0;
            foreach (var ch in ranks[r])
            {
                if (char.IsDigit(ch))
                    col += ch - '0';
                else
                    board[r, col++] = ch;
            }
            if (col != 9)
                throw new ArgumentException($"bad rank: {ranks[r]}");
        }
        var side = parts.Length > 1 ? parts[1] : "w";
        return (board, side);
    }

    public static string ApplyMove(string fen, string move)
    {
        var (board, side) = ParseFen(fen);
        var fc = move[0] - 'a';
        var fr = move[1] - '0';
        var tc = move[2] - 'a';
        var tr = move[3] - '0';
        var fRow = 9 - fr;
        var tRow = 9 - tr;
        var piece = board[fRow, fc];
        if (piece is null)
            throw new ArgumentException($"no piece at {move[..2]}");
        board[tRow, tc] = piece;
        board[fRow, fc] = null;

        var rows = new List<string>();
        for (var r = 0; r < 10; r++)
        {
            var sb = new StringBuilder();
            var empty = 0;
            for (var c = 0; c < 9; c++)
            {
                var p = board[r, c];
                if (p is null)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(p);
                }
            }
            if (empty > 0) sb.Append(empty);
            rows.Add(sb.ToString());
        }
        return string.Join("/", rows) + (side == "w" ? " b" : " w");
    }

    public static string MoveToChinese(string fen, string move)
    {
        var (board, side) = ParseFen(fen);
        var fc = move[0] - 'a';
        var fr = move[1] - '0';
        var tc = move[2] - 'a';
        var tr = move[3] - '0';
        var fRow = 9 - fr;
        var tRow = 9 - tr;

        var piece = board[fRow, fc] ?? throw new ArgumentException($"no piece at {move[..2]}");
        var isRed = char.IsUpper(piece);
        if (isRed != (side == "w"))
            throw new ArgumentException("side mismatch");

        var name = (isRed ? RedPieces : BlackPieces)[piece];
        var nums = isRed ? RedNum : BlackNum;
        int fFile, tFile;
        bool advance;
        if (isRed)
        {
            fFile = 9 - fc;
            tFile = 9 - tc;
            advance = tRow < fRow;
        }
        else
        {
            fFile = fc + 1;
            tFile = tc + 1;
            advance = tRow > fRow;
        }

        var prefix = "";
        var sameFileRows = new List<int>();
        for (var r = 0; r < 10; r++)
            if (board[r, fc] == piece && r != fRow)
                sameFileRows.Add(r);
        if (sameFileRows.Count > 0)
        {
            var rows = new List<int>(sameFileRows) { fRow };
            if (isRed)
                rows.Sort(); // smaller row = closer to black = 前
            else
                rows.Sort((a, b) => b.CompareTo(a)); // larger row = closer to red = 前
            var idx = rows.IndexOf(fRow);
            prefix = rows.Count == 3 ? new[] { "前", "中", "后" }[idx] : (idx == 0 ? "前" : "后");
        }

        var verb = advance ? "进" : "退";
        var piecePart = prefix.Length > 0 ? prefix + name : name + nums[fFile - 1];
        if (Straight.Contains(piece))
        {
            if (fc == tc)
            {
                var count = Math.Abs(tr - fr);
                return piecePart + verb + nums[count - 1];
            }
            return piecePart + "平" + nums[tFile - 1];
        }
        return piecePart + verb + nums[tFile - 1];
    }

    public static string BestMoveToChinese(string fen, string bestmove)
    {
        var parts = bestmove.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var move = parts.Length > 1 && parts[0] == "bestmove" ? parts[1] : parts[0];
        return MoveToChinese(fen, move);
    }
}
