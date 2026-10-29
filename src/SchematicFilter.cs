namespace MapManager;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses "#RRGGBB" (the leading # is optional).</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int value)) return false;
        color = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Colours and thresholds for <see cref="SchematicFilter"/>. The defaults follow the game's own map previews.</summary>
public sealed class SchematicStyle
{
    public Rgb Background { get; set; } = new(30, 35, 41);
    public Rgb Asphalt { get; set; } = new(66, 72, 79);
    public Rgb Concrete { get; set; } = new(104, 111, 119);
    public Rgb AsphaltEdge { get; set; } = new(176, 182, 188);
    public Rgb ConcreteEdge { get; set; } = new(214, 219, 224);

    /// <summary>Pixels more colourful than this (grass, dirt, water) are scenery; greyer pixels are surfaces. 0..1.</summary>
    public float SurfaceSaturationMax { get; set; } = 0.16f;
    /// <summary>Surface pixels darker than this are asphalt, brighter are concrete / buildings. 0..1.</summary>
    public float LightSurfaceLuminance { get; set; } = 0.42f;
    /// <summary>Surface pixels darker than this are treated as shadow/void, not road. 0..1.</summary>
    public float MinSurfaceLuminance { get; set; } = 0.10f;
    /// <summary>3x3 majority passes that remove single-pixel noise.</summary>
    public int SmoothingPasses { get; set; } = 2;
    /// <summary>Outline thickness in pixels; 0 picks one from the image size.</summary>
    public int EdgeWidth { get; set; }
}

/// <summary>
/// Turns a top-down picture of a map (grass, tarmac, buildings) into the flat schematic the game shows for its own
/// maps: dark background, grey road fills, light outlines. Pure image code with no Unity dependency, so it can be
/// tested and tuned off-line.
/// </summary>
public static class SchematicFilter
{
    private const byte Scenery = 0, Asphalt = 1, Concrete = 2;

    /// <summary>Input and output are tightly packed RGBA, row-major, row 0 first.</summary>
    public static byte[] Apply(byte[] rgba, int width, int height, SchematicStyle? style = null)
    {
        style ??= new SchematicStyle();
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Pixel buffer does not match the stated size.");

        byte[] cls = Classify(rgba, width, height, style);
        for (int pass = 0; pass < style.SmoothingPasses; pass++) cls = Majority(cls, width, height);
        RemoveSmallRegions(cls, width, height, Math.Max(8, width * height / 20000));
        int edge = style.EdgeWidth > 0 ? style.EdgeWidth : Math.Max(1, (int)Math.Round(Math.Min(width, height) / 600.0));
        return Render(cls, width, height, style, edge);
    }

    private static byte[] Classify(byte[] rgba, int width, int height, SchematicStyle style)
    {
        var cls = new byte[width * height];
        for (int i = 0; i < cls.Length; i++)
        {
            float r = rgba[i * 4] / 255f, g = rgba[i * 4 + 1] / 255f, b = rgba[i * 4 + 2] / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float luminance = 0.299f * r + 0.587f * g + 0.114f * b;
            float saturation = max > 0f ? (max - min) / max : 0f;
            if (saturation >= style.SurfaceSaturationMax || luminance <= style.MinSurfaceLuminance) cls[i] = Scenery;
            else cls[i] = luminance < style.LightSurfaceLuminance ? Asphalt : Concrete;
        }
        return cls;
    }

    /// <summary>Each pixel becomes the most common class in its 3x3 neighbourhood (ties go to the lower class).</summary>
    private static byte[] Majority(byte[] cls, int width, int height)
    {
        var result = new byte[cls.Length];
        Span<int> counts = stackalloc int[3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                counts.Clear();
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, height - 1);
                    for (int dx = -1; dx <= 1; dx++)
                        counts[cls[yy * width + Math.Clamp(x + dx, 0, width - 1)]]++;
                }
                byte best = 0;
                if (counts[1] > counts[best]) best = 1;
                if (counts[2] > counts[best]) best = 2;
                result[y * width + x] = best;
            }
        }
        return result;
    }

    /// <summary>Clears connected (4-neighbour) groups of surface pixels smaller than <paramref name="minPixels"/>.</summary>
    private static void RemoveSmallRegions(byte[] cls, int width, int height, int minPixels)
    {
        var seen = new bool[cls.Length];
        var stack = new Stack<int>();
        var region = new List<int>();
        for (int start = 0; start < cls.Length; start++)
        {
            if (cls[start] == Scenery || seen[start]) continue;
            region.Clear();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                region.Add(p);
                int x = p % width, y = p / width;
                if (x > 0) Visit(p - 1);
                if (x < width - 1) Visit(p + 1);
                if (y > 0) Visit(p - width);
                if (y < height - 1) Visit(p + width);
            }
            if (region.Count < minPixels)
                foreach (int p in region) cls[p] = Scenery;
        }

        void Visit(int q)
        {
            if (cls[q] == Scenery || seen[q]) return;
            seen[q] = true;
            stack.Push(q);
        }
    }

    private static byte[] Render(byte[] cls, int width, int height, SchematicStyle style, int edge)
    {
        var output = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                byte c = cls[i];
                Rgb color = c switch { Asphalt => style.Asphalt, Concrete => style.Concrete, _ => style.Background };
                if (c != Scenery &&
                    (Differs(x - edge, y, c) || Differs(x + edge, y, c) || Differs(x, y - edge, c) || Differs(x, y + edge, c)))
                    color = c == Asphalt ? style.AsphaltEdge : style.ConcreteEdge;
                output[i * 4] = color.R;
                output[i * 4 + 1] = color.G;
                output[i * 4 + 2] = color.B;
                output[i * 4 + 3] = 255;
            }
        }
        return output;

        // Outside the picture counts as "same", so no outline is drawn along the image border.
        bool Differs(int x, int y, byte c) => x >= 0 && x < width && y >= 0 && y < height && cls[y * width + x] != c;
    }
}
