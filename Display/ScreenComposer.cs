using System.Text.Json.Serialization;
using ClaudeCodeMonitor.Configuration;
using Microsoft.Extensions.Options;

namespace ClaudeCodeMonitor.Display;

public class ScreenComposer(IOptions<MonitorOptions> options)
{
    private const string Cyan = "#59d0ff";
    private const string Orange = "#ff7a3d";
    private const string Purple = "#9184d9";
    private const string DimCyan = "#12384a";
    private const string DimPurple = "#2b2650";
    private const string Grey = "#4a4f5e";
    private const string DimGrey = "#23262f";
    private const string Red = "#ff4526";

    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = ["111", "101", "101", "101", "111"],
        ['1'] = ["010", "110", "010", "010", "111"],
        ['2'] = ["111", "001", "111", "100", "111"],
        ['3'] = ["111", "001", "111", "001", "111"],
        ['4'] = ["101", "101", "111", "001", "001"],
        ['5'] = ["111", "100", "111", "001", "111"],
        ['6'] = ["111", "100", "111", "101", "111"],
        ['7'] = ["111", "001", "010", "010", "010"],
        ['8'] = ["111", "101", "111", "101", "111"],
        ['9'] = ["111", "101", "111", "001", "111"],
        [':'] = ["0", "1", "0", "1", "0"],
        [' '] = ["0", "0", "0", "0", "0"],
        ['%'] = ["11001", "11010", "00100", "01011", "10011"],
        ['d'] = ["001", "001", "111", "101", "111"],
        ['h'] = ["100", "100", "111", "101", "101"],
        ['W'] = ["10001", "10001", "10101", "10101", "01010"],
        ['E'] = ["111", "100", "111", "100", "111"],
        ['K'] = ["101", "101", "110", "101", "101"],
        ['!'] = ["1", "1", "1", "0", "1"],
    };

    private readonly MonitorOptions _options = options.Value;

    public object ComposeSessionIconApp(int sessionUtilization, TimeSpan? untilReset)
    {
        var used = Math.Clamp(sessionUtilization, 0, 100);
        var pctLeft = 100 - used;
        var (icon, text, color) = pctLeft switch
        {
            <= 0 => ("clawd_sad", "100%", Red),
            < 10 => ("clawd_panic", $"{used}%", Red),
            _ => ("clawd", $"{used}%", Cyan),
        };

        var grid = NewGrid();
        SessionTimeBar(grid, untilReset);
        // Drawn (not the clock's text renderer) so the position is exact: right-aligned,
        // ending 1px from the display edge.
        Text(grid, 31 - TextWidth(text), 1, text, color);

        return new AwtrixApp
        {
            Icon = icon,
            Draw = DrawList(grid),
            Lifetime = _options.AppLifetimeSeconds,
            LifetimeMode = 0,
        };
    }

    public object ComposeWeekApp(int weeklyUtilization, TimeSpan? untilReset)
    {
        var used = Math.Clamp(weeklyUtilization, 0, 100);
        var grid = NewGrid();
        Text(grid, 0, 1, "WEEK", Orange);
        DrawPercent(grid, used, used > 90 ? Red : Cyan);
        WeekTimeBar(grid, untilReset);
        return ToApp(grid);
    }

    private static void SessionTimeBar(string?[][] grid, TimeSpan? untilReset, string lit = Purple, string dim = DimPurple)
    {
        var fraction = untilReset is { } left ? 1 - Math.Clamp(left.TotalMinutes / 300.0, 0, 1) : 0;
        SegmentedBar(grid, fraction, segments: 5, segmentWidth: 5, lit, dim);
    }

    private static void WeekTimeBar(string?[][] grid, TimeSpan? untilReset)
    {
        var fraction = untilReset is { } left ? 1 - Math.Clamp(left.TotalHours / 168.0, 0, 1) : 0;
        SegmentedBar(grid, fraction, segments: 7, segmentWidth: 3);
    }

    private static void SegmentedBar(
        string?[][] grid, double fraction, int segments, int segmentWidth, string lit = Purple, string dim = DimPurple)
    {
        var litPixels = (int)Math.Round(fraction * segments * segmentWidth);
        var n = 0;
        for (var segment = 0; segment < segments; segment++)
        {
            for (var p = 0; p < segmentWidth; p++)
            {
                grid[7][1 + segment * (segmentWidth + 1) + p] = n++ < litPixels ? lit : dim;
            }
        }
    }

    public object ComposeCelebrationApp(long? frame = null)
    {
        var grid = NewGrid();
        var tick = frame ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 800;

        string[] confetti = [Cyan, Purple, Orange];
        for (var i = 0; i < 7; i++)
        {
            var x = 13 + (int)((i * 5 + tick * 3) % 18);
            var y = (int)((i * 3 + tick) % 7);
            grid[y][x] = confetti[i % 3];
        }

        var legs = tick % 2 == 0 ? "..O..O.." : ".O....O.";
        Sprite(grid, 1, 1, [".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", legs],
            new() { ['O'] = Orange, ['B'] = "#2ea8ff" });

        return ToApp(grid);
    }

    // Same layout as the session screen (last known values, segmented time bar) but
    // in grey, so a stale screen never reads as a live 0%. The clock's native progress
    // bar is deliberately not used: it draws a solid strip from column 9 that looks
    // like the hour segments squashed together.
    public object ComposeDataIssueIconApp(int sessionUtilization, TimeSpan? untilReset)
    {
        var used = Math.Clamp(sessionUtilization, 0, 100);
        var grid = NewGrid();
        SessionTimeBar(grid, untilReset, Grey, DimGrey);
        var text = $"{used}%";
        Text(grid, 31 - TextWidth(text), 1, text, Grey);
        // Drawn, not sent as `text`: the clock does not render the text field reliably
        // alongside a draw layer, so the warning mark never showed up.
        Text(grid, 15, 1, "!", Orange);
        return new AwtrixApp
        {
            Icon = "clawd_grey",
            Draw = DrawList(grid),
            Lifetime = _options.AppLifetimeSeconds,
            LifetimeMode = 0,
        };
    }


    private static void DrawPercent(string?[][] grid, int pct, string color)
    {
        var digits = pct.ToString();
        Text(grid, 25 - TextWidth(digits), 1, digits, color);
        Text(grid, 26, 1, "%", color);
    }

    private static string?[][] NewGrid()
    {
        var grid = new string?[8][];
        for (var y = 0; y < 8; y++)
        {
            grid[y] = new string?[32];
        }

        return grid;
    }

    private static void Sprite(string?[][] grid, int x, int y, string[] rows, Dictionary<char, string> palette)
    {
        for (var dy = 0; dy < rows.Length; dy++)
        {
            for (var dx = 0; dx < rows[dy].Length; dx++)
            {
                if (palette.TryGetValue(rows[dy][dx], out var color) &&
                    y + dy is >= 0 and < 8 && x + dx is >= 0 and < 32)
                {
                    grid[y + dy][x + dx] = color;
                }
            }
        }
    }

    private static void Text(string?[][] grid, int x, int y, string text, string color)
    {
        var cx = x;
        foreach (var ch in text)
        {
            var glyph = Font.GetValueOrDefault(ch, Font[' ']);
            Sprite(grid, cx, y, glyph, new() { ['1'] = color });
            cx += glyph[0].Length + 1;
        }
    }

    private static int TextWidth(string text) =>
        text.Sum(ch => Font.GetValueOrDefault(ch, Font[' '])[0].Length + 1) - 1;

    private static void Bar(string?[][] grid, int pct, string lit, string dim)
    {
        var n = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100.0 * 32);
        for (var x = 0; x < 32; x++)
        {
            grid[7][x] = x < n ? lit : dim;
        }
    }

    private static List<object> DrawList(string?[][] grid)
    {
        var draw = new List<object>();
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                if (grid[y][x] is { } color)
                {
                    draw.Add(new { dp = new object[] { x, y, color } });
                }
            }
        }

        return draw;
    }

    private object ToApp(string?[][] grid)
    {
        return new AwtrixApp
        {
            Draw = DrawList(grid),
            Lifetime = _options.AppLifetimeSeconds,
            LifetimeMode = 0,
        };
    }

    private sealed class AwtrixApp
    {
        [JsonPropertyName("draw")]
        public List<object>? Draw { get; init; }

        [JsonPropertyName("icon")]
        public string? Icon { get; init; }


        [JsonPropertyName("lifetime")]
        public int Lifetime { get; init; }

        [JsonPropertyName("lifetimeMode")]
        public int LifetimeMode { get; init; }
    }
}

