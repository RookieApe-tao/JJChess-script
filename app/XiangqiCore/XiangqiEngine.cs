using System.Diagnostics;
using System.Text;

namespace XiangqiCore;

public record EngineAnalysis(
    string? BestMove,
    int? ScoreCp,
    int? Depth,
    string? Pv,
    IReadOnlyList<string> Lines);

/// <summary>Minimal UCI client for Pikafish (works on Windows and Android).</summary>
public sealed class XiangqiEngine : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly List<string> _lines = new();

    private XiangqiEngine(Process proc, StreamWriter stdin)
    {
        _proc = proc;
        _stdin = stdin;
    }

    public static XiangqiEngine Start(string exePath, string nnuePath, int threads = 1, int hashMb = 64)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start engine");
        var engine = new XiangqiEngine(proc, proc.StandardInput);
        var stdoutTask = Task.Run(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
                lock (engine._lines) engine._lines.Add(line);
        });
        var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

        engine.Send("uci");
        if (!engine.WaitFor("uciok", 10000))
            throw new InvalidOperationException("engine did not respond to uci");
        engine.Send($"setoption name Threads value {threads}");
        engine.Send($"setoption name Hash value {hashMb}");
        engine.Send($"setoption name EvalFile value {nnuePath}");
        engine.Send("isready");
        if (!engine.WaitFor("readyok", 10000))
            throw new InvalidOperationException("engine not ready");
        engine._stdoutTask = stdoutTask;
        engine._stderrTask = stderrTask;
        return engine;
    }

    private Task? _stdoutTask;
    private Task? _stderrTask;

    public void Send(string command)
    {
        _stdin.WriteLine(command);
        _stdin.Flush();
    }

    public bool WaitFor(string token, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_lines)
            {
                if (_lines.Any(l => l.Contains(token, StringComparison.Ordinal)))
                    return true;
            }
            Thread.Sleep(20);
        }
        return false;
    }

    public EngineAnalysis Analyze(string fen, int movetimeMs)
    {
        lock (_lines) _lines.Clear();
        Send($"position fen {fen}");
        Send($"go movetime {movetimeMs}");
        if (!WaitFor("bestmove", movetimeMs + 15000))
            return new EngineAnalysis(null, null, null, null, SnapshotLines());

        string? best = null;
        int? score = null;
        int? depth = null;
        string? pv = null;
        var lines = SnapshotLines();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (best is null && line.StartsWith("bestmove", StringComparison.Ordinal))
            {
                best = line;
                continue;
            }
            if (line.StartsWith("info", StringComparison.Ordinal) && line.Contains(" pv "))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var j = 0; j < parts.Length; j++)
                {
                    if (parts[j] == "depth" && int.TryParse(parts[j + 1], out var d)) depth = d;
                    if (parts[j] == "cp" && int.TryParse(parts[j + 1], out var s)) score = s;
                    if (parts[j] == "pv") pv = string.Join(" ", parts[(j + 1)..]);
                }
                break;
            }
        }
        return new EngineAnalysis(best, score, depth, pv, lines);
    }

    private List<string> SnapshotLines()
    {
        lock (_lines) return new List<string>(_lines);
    }

    /// <summary>
    /// 列出某局面当前方的全部合法走法（UCI，如 "b2b9"）。基于 UCI <c>go perft</c>。
    /// 仅作测试/诊断裁判，不在识别热路径调用。注意 <c>go perft</c> 不输出 bestmove，
    /// 故用输出稳定等待而非 token 等待。
    /// </summary>
    public List<string> Perft(string fen, int depth = 1)
    {
        lock (_lines) _lines.Clear();
        Send($"position fen {fen}");
        Send($"go perft {depth}");
        WaitForStable(3000);
        var moves = new List<string>();
        foreach (var l in SnapshotLines())
        {
            var s = l.Trim();
            // 形如 "a3a4: 1" 的行
            if (s.Length >= 5 && s[4] == ':' &&
                s[0] >= 'a' && s[0] <= 'i' && char.IsDigit(s[1]) &&
                s[2] >= 'a' && s[2] <= 'i' && char.IsDigit(s[3]))
                moves.Add(s[..4]);
        }
        return moves;
    }

    /// <summary>等到引擎输出停止增长（150ms 无新行）即认为 perft 输出完成。</summary>
    private bool WaitForStable(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var prev = -1;
        var stable = 0L;
        var last = Environment.TickCount64;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(30);
            var now = Environment.TickCount64;
            int cnt;
            lock (_lines) cnt = _lines.Count;
            stable = cnt == prev ? stable + (now - last) : 0;
            last = now;
            prev = cnt;
            if (cnt > 0 && stable >= 150) return true;
        }
        return false;
    }

    public void Dispose()
    {
        try { Send("quit"); } catch { /* ignore */ }
        try
        {
            if (!_proc.WaitForExit(3000))
                _proc.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
        _proc.Dispose();
    }
}
