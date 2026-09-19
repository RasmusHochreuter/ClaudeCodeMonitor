namespace ClaudeCodeMonitor.Display;

/// <summary>
/// Builds the 8x8 animated GIF icons the clock plays locally in icon mode.
/// </summary>
public static class ClawdIcons
{
    public static readonly byte[][] Palette =
    [
        [0x00, 0x00, 0x00],
        [0xFF, 0x7A, 0x3D],
        [0x2E, 0xA8, 0xFF],
        [0xE9, 0xE9, 0xED],
        [0x1D, 0x6F, 0xA8],
        [0x59, 0xD0, 0xFF],
        [0x4A, 0x4F, 0x5E],
        [0x00, 0x00, 0x00],
    ];

    private const byte Orange = 1;
    private const byte Eye = 2;
    private const byte White = 3;
    private const byte SadEye = 4;
    private const byte Tear = 5;
    private const byte Grey = 6;

    public sealed record Animation(IReadOnlyList<byte[]> Frames, int DelayCentiseconds);

    public static IReadOnlyDictionary<string, byte[]> Build() => Animations().ToDictionary(
        icon => icon.Key,
        icon => GifWriter.Animated(icon.Value.Frames, 32, 8, Palette, icon.Value.DelayCentiseconds));

    public static IReadOnlyDictionary<string, Animation> Animations() => new Dictionary<string, Animation>
    {
        ["clawd"] = new Animation(
            [
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, Eye),
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", ".O....O."], Orange, Eye),
                Frame([".OOOOOO.", ".OOOOOO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, Eye),
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", ".O....O."], Orange, Eye),
                Frame([".OOOOOO.", ".OOBOOB.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, Eye),
                Frame([".OOOOOO.", ".OOBOOB.", "OOOOOOOO", ".OOOOOO.", ".O....O."], Orange, Eye),
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, Eye),
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", ".O....O."], Orange, Eye),
            ],
            80),

        ["clawd_panic"] = new Animation(
            [
                Frame([".OOOOOO.", ".OWOOWO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, White),
                Frame([".OOOOOO.", ".OWOOWO.", "OOOOOOOO", ".OOOOOO.", ".O....O."], Orange, White, xOffset: -1),
                Frame([".OOOOOO.", ".OWOOWO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, White, xOffset: -1),
                Frame([".OOOOOO.", ".OWOOWO.", "OOOOOOOO", ".OOOOOO.", ".O....O."], Orange, White),
            ],
            30),

        ["clawd_sad"] = new Animation(
            [
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, SadEye, tearRow: 2),
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, SadEye, tearRow: 3),
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, SadEye, tearRow: 4),
                Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Orange, SadEye),
            ],
            80),

        ["clawd_grey"] = new Animation(
            [Frame([".OOOOOO.", ".OBOOBO.", "OOOOOOOO", ".OOOOOO.", "..O..O.."], Grey, Grey)],
            80),
    };

    private static byte[] Frame(string[] rows, byte body, byte eye, int? tearRow = null, int xOffset = 0)
    {
        // Full-width 32x8 canvas so Clawd can sit past the 8px icon slot; +2 puts the
        // body at columns 3-8 with the arm tip at 9.
        var pixels = new byte[256];
        for (var dy = 0; dy < rows.Length; dy++)
        {
            for (var dx = 0; dx < 8; dx++)
            {
                var value = rows[dy][dx] switch
                {
                    'O' => body,
                    'B' or 'W' => eye,
                    _ => (byte)0,
                };
                var x = dx + xOffset + 2;
                if (value != 0 && x is >= 0 and < 32)
                {
                    pixels[(dy + 1) * 32 + x] = value;
                }
            }
        }

        if (tearRow is { } row)
        {
            pixels[row * 32 + 8] = Tear;
        }

        return pixels;
    }
}
