namespace ClaudeCodeMonitor.Display;

/// <summary>
/// Minimal GIF89a encoder for tiny looping animations (8-color palette,
/// uncompressed LZW via clear-code resets). Sufficient for 8x8 AWTRIX icons.
/// </summary>
public static class GifWriter
{
    public static byte[] Animated(IReadOnlyList<byte[]> frames, int width, int height, byte[][] palette, int delayCentiseconds)
    {
        if (palette.Length != 8)
        {
            throw new ArgumentException("Palette must contain exactly 8 colors", nameof(palette));
        }

        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());

        bytes.AddRange(BitConverter.GetBytes((ushort)width));
        bytes.AddRange(BitConverter.GetBytes((ushort)height));
        bytes.Add(0b1_111_0_010);
        bytes.Add(0);
        bytes.Add(0);

        foreach (var color in palette)
        {
            bytes.AddRange(color);
        }

        if (frames.Count > 1)
        {
            bytes.AddRange([0x21, 0xFF, 0x0B]);
            bytes.AddRange("NETSCAPE2.0"u8.ToArray());
            bytes.AddRange([0x03, 0x01, 0x00, 0x00, 0x00]);
        }

        foreach (var frame in frames)
        {
            bytes.AddRange([0x21, 0xF9, 0x04, 0x04]);
            bytes.AddRange(BitConverter.GetBytes((ushort)delayCentiseconds));
            bytes.AddRange([0x00, 0x00]);

            bytes.Add(0x2C);
            bytes.AddRange(BitConverter.GetBytes((ushort)0));
            bytes.AddRange(BitConverter.GetBytes((ushort)0));
            bytes.AddRange(BitConverter.GetBytes((ushort)width));
            bytes.AddRange(BitConverter.GetBytes((ushort)height));
            bytes.Add(0x00);

            bytes.Add(3);
            bytes.AddRange(EncodePixels(frame));
        }

        bytes.Add(0x3B);
        return [.. bytes];
    }

    private static List<byte> EncodePixels(byte[] pixels)
    {
        const int clearCode = 8;
        const int endCode = 9;
        const int codeBits = 4;

        var bits = new List<byte>();
        var current = 0;
        var currentBits = 0;

        void Emit(int code)
        {
            current |= code << currentBits;
            currentBits += codeBits;
            while (currentBits >= 8)
            {
                bits.Add((byte)(current & 0xFF));
                current >>= 8;
                currentBits -= 8;
            }
        }

        foreach (var pixel in pixels)
        {
            Emit(clearCode);
            Emit(pixel);
        }

        Emit(endCode);
        if (currentBits > 0)
        {
            bits.Add((byte)(current & 0xFF));
        }

        var blocks = new List<byte>();
        for (var i = 0; i < bits.Count; i += 255)
        {
            var chunk = bits.GetRange(i, Math.Min(255, bits.Count - i));
            blocks.Add((byte)chunk.Count);
            blocks.AddRange(chunk);
        }

        blocks.Add(0x00);
        return blocks;
    }
}
