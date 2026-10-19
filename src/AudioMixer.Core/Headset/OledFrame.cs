namespace AudioMixer.Core.Headset;

/// <summary>
/// A 1-bit image for the base station's 128×64 OLED. The base station takes images through feature report 0x93
/// (spec: draw_bitmap): <c>06 93 x y width height</c>, then the pixels column by column, top to bottom, eight rows
/// per byte with the top row in the lowest bit. One report carries at most 64 columns, so a full screen is two reports.
/// </summary>
public sealed class OledFrame
{
    public const int Width = 128;
    public const int Height = 64;
    const int ColumnsPerReport = 64;
    const int ReportSize = 1024;

    readonly bool[] _pixels = new bool[Width * Height];

    public bool this[int x, int y]
    {
        get => x is >= 0 and < Width && y is >= 0 and < Height && _pixels[y * Width + x];
        set
        {
            if (x is >= 0 and < Width && y is >= 0 and < Height)
                _pixels[y * Width + x] = value;
        }
    }

    /// <summary>A frame from 8-bit grey pixels (row by row, <see cref="Width"/> wide); anything at or above the threshold is lit.</summary>
    public static OledFrame FromGray(ReadOnlySpan<byte> gray, int stride, byte threshold = 128)
    {
        var frame = new OledFrame();
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                frame._pixels[y * Width + x] = gray[y * stride + x] >= threshold;
        return frame;
    }

    public bool SameAs(OledFrame? other) => other is not null && _pixels.AsSpan().SequenceEqual(other._pixels);

    /// <summary>The feature reports that draw this frame over the whole screen.</summary>
    public IEnumerable<byte[]> ToReports()
    {
        for (var x0 = 0; x0 < Width; x0 += ColumnsPerReport)
        {
            var report = new byte[ReportSize];
            report[0] = NovaProProtocol.ReportId;
            report[1] = NovaProProtocol.DrawBitmap;
            report[2] = (byte)x0;
            report[3] = 0;
            report[4] = ColumnsPerReport;
            report[5] = Height; // already a multiple of 8
            for (var x = 0; x < ColumnsPerReport; x++)
                for (var y = 0; y < Height; y++)
                {
                    if (!_pixels[y * Width + x0 + x])
                        continue;
                    var bit = x * Height + y;
                    report[6 + bit / 8] |= (byte)(1 << (bit % 8));
                }
            yield return report;
        }
    }
}
