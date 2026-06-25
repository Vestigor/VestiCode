using System.Diagnostics;
using System.Text;

namespace VestiCode.Cli.Tui;

/// <summary>
/// 内联底部活动区域（1~2 行，\r + 光标上移原地刷新）：
/// - Thinking 模式（两行）：<c>● Thinking 1.2k tokens</c> + <c>✻ Synthesizing… (esc 中断)</c>
/// - Status 模式（一行）：<c>✻ Synthesizing… (esc 中断)</c>
/// （<c>● Thought for Xs</c> 等是由调用方提交在上方的“步骤行”，与此活动区域无关）
/// 通过 <see cref="WriteAbove"/> 在活动区域上方提交内容（活动区域始终在最底部，上方内容可被终端原生选中）。
/// 非 TTY（管道）下退化为直接打印。
/// </summary>
public sealed class StatusIndicator
{
    private static readonly string[] Frames = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];

    private static readonly string[] Words =
    [
        "Synthesizing", "Perusing", "Effecting", "Actualizing", "Crunching", "Coalescing",
        "Clauding", "Calculating", "Forging", "Creating", "Musing", "Booping",
        "Puzzling", "Manifesting", "Hatching", "Pondering", "Cogitating", "Noodling",
        "Vibing", "Conjuring", "Tinkering", "Brewing", "Wrangling", "Finagling",
        "Spelunking", "Percolating", "Ruminating", "Marinating", "Schlepping", "Frolicking",
        "Simmering", "Distilling", "Orchestrating", "Architecting", "Untangling", "Deliberating",
        "Incubating", "Channeling", "Improvising", "Galloping",
    ];

    private const int FrameMs = 50;    // 渲染间隔（20 fps，更顺滑）
    private const int WordCycle = 60;  // 每个词存活帧数（60 × 50ms = 3s）
    private const int GlyphEvery = 2;  // 每 2 帧换一次 ✻ 字形（≈100ms/帧，脉动不至于太快）

    private const string Esc = "";
    private const string ClearLine = $"{Esc}[2K";
    private const string HideCursor = $"{Esc}[?25l";
    private const string ShowCursor = $"{Esc}[?25h";
    private const string Reset = $"{Esc}[0m";
    private const string Pink = $"{Esc}[38;5;213m";
    private const string Gray = $"{Esc}[90m";
    private const string Dim = $"{Esc}[2m";

    private enum Mode { Off, Thinking, Status }

    private readonly bool _enabled = !Console.IsOutputRedirected;
    private readonly Lock _gate = new();
    private readonly Stopwatch _stopwatch = new();

    private CancellationTokenSource? _cts;
    private Task? _ticker;
    private Mode _mode = Mode.Off;
    private bool _suspended;
    private int _frame;
    private int _wordIndex;
    private int _wordTick;
    private string _prevWord = ""; // 上一个状态词（用于打字机原地覆盖过渡）
    private int _renderedLines; // 当前活动区域已绘制的行数（用于原地刷新/清除）
    private IReadOnlyList<string>? _lastRegionLines; // 最近一次区域内容（供 WriteAbove 立即重锚，消除空行抖动）

    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;

    public void Start()
    {
        if (_ticker is not null)
        {
            return;
        }
        if (!_enabled)
        {
            return;
        }
        Console.Write(HideCursor); // 活动期间隐藏光标，避免闪烁的光标停在 ●/✻ 上像“选中”
        _cts = new CancellationTokenSource();
        _ticker = Task.Run(() => TickAsync(_cts.Token));
    }

    /// <summary>进入 Thinking 模式（重置计时，供 "Thought for Xs" 计算耗时）。</summary>
    public void SetThinking()
    {
        lock (_gate)
        {
            _mode = Mode.Thinking;
            _stopwatch.Restart();
        }
    }

    /// <summary>进入 Status 模式（✻ + 循环词）。</summary>
    public void SetStatus()
    {
        lock (_gate)
        {
            _mode = Mode.Status;
        }
    }

    /// <summary>在活动区域上方提交一行（已含 ANSI）。</summary>
    public void WriteAbove(string line)
    {
        lock (_gate)
        {
            if (_enabled && _ticker is not null)
            {
                ClearRegion();
                Console.WriteLine(line);
                // 立即把活动区重锚到新内容下方，避免等下一帧（120ms）才重绘导致空行/✻ 抖动。
                if (_mode != Mode.Off && !_suspended && _lastRegionLines is not null)
                {
                    DrawRegion(_lastRegionLines);
                }
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }

    /// <summary>临时隐藏活动区域（如弹 HITL 时），之后用 <see cref="Resume"/> 恢复。</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _suspended = true;
            if (_enabled)
            {
                ClearRegion();
                Console.Write(ShowCursor); // 挂起期间（如 HITL 提示）恢复光标
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _suspended = false;
            if (_enabled)
            {
                Console.Write(HideCursor);
            }
        }
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_ticker is not null)
            {
                try
                {
                    await _ticker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 正常停止
                }
            }
            _cts.Dispose();
            _cts = null;
        }
        _ticker = null;
        _mode = Mode.Off;
        _stopwatch.Stop();
        lock (_gate)
        {
            if (_enabled)
            {
                ClearRegion();
                Console.Write(ShowCursor); // 恢复光标，供后续行输入
            }
        }
    }


    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Render();
                await Task.Delay(FrameMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private void Render()
    {
        lock (_gate)
        {
            if (_suspended || _mode == Mode.Off)
            {
                return;
            }
            var glyph = Frames[_frame++ / GlyphEvery % Frames.Length];

            var pos = _wordTick++ % WordCycle;
            if (pos == 0)
            {
                _prevWord = Words[_wordIndex % Words.Length]; // 切词前记下旧词，供原地覆盖
                _wordIndex++;
            }
            var word = Words[_wordIndex % Words.Length];

            // 打字机原地覆盖：左边 [..k] 逐帧变成新词，右边保留旧词尾 [k..]，光标 ▋ 在交界处；
            // 旧词尾依次被吃掉，全部覆盖完显示省略号、光标消失。
            var k = pos; // 1 字/帧 ≈ 50ms/字（20fps 下已顺滑且偏快）
            var left = word[..Math.Min(k, word.Length)];
            var right = k < _prevWord.Length ? _prevWord[k..] : "";
            var done = k >= Math.Max(word.Length, _prevWord.Length);
            var cursor = done ? "" : $"{Pink}▋{Reset}";
            var suffix = done ? "…" : "";
            var statusLine =
                $"{Pink}{glyph}{Reset} {Esc}[38;5;245m{left}{Reset}{cursor}{Esc}[38;5;240m{right}{Reset}{Dim}{suffix}  (esc 中断){Reset}";

            // 顶部留一行空行 → 活动区位于最后内容的"下两行"，更透气。
            // 思考与执行期都只显示 ✻ 状态行；思考结束由调用方提交 "● Thought for Xs"。
            DrawRegion(["", statusLine]);
        }
    }

    /// <summary>原地重绘活动区域（光标进入/离开时都停在区域首行行首）。会清除上一次多出的行。</summary>
    private void DrawRegion(IReadOnlyList<string> lines)
    {
        var n = Math.Max(_renderedLines, lines.Count);
        var sb = new StringBuilder();
        sb.Append('\r');
        for (var i = 0; i < n; i++)
        {
            sb.Append(ClearLine);
            if (i < lines.Count)
            {
                sb.Append(lines[i]);
            }
            if (i < n - 1)
            {
                sb.Append('\n');
            }
        }
        if (n > 1)
        {
            sb.Append($"{Esc}[{n - 1}A"); // 光标移回区域首行
        }
        sb.Append('\r');
        Console.Write(sb.ToString());
        _renderedLines = lines.Count;
        _lastRegionLines = lines;
    }

    /// <summary>清空整个活动区域，光标停在区域首行行首，置 <see cref="_renderedLines"/>=0。</summary>
    private void ClearRegion()
    {
        if (_renderedLines == 0)
        {
            return;
        }
        var sb = new StringBuilder();
        sb.Append('\r');
        for (var i = 0; i < _renderedLines; i++)
        {
            sb.Append(ClearLine);
            if (i < _renderedLines - 1)
            {
                sb.Append('\n');
            }
        }
        if (_renderedLines > 1)
        {
            sb.Append($"{Esc}[{_renderedLines - 1}A");
        }
        sb.Append('\r');
        Console.Write(sb.ToString());
        _renderedLines = 0;
    }
}
