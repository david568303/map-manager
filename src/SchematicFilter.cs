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

    /// <summary>
    /// How opaque the scenery is, 0..1. 0 (the default) makes everything that is not a road or building fully
    /// transparent so the map floats over whatever is behind it, like the game's own maps.
    /// </summary>
    public float BackgroundOpacity { get; set; }

    /// <summary>Pixels more colourful than this (grass, dirt, water) are scenery; greyer pixels are surfaces. 0..1.</summary>
    public float SurfaceSaturationMax { get; set; } = 0.16f;
    /// <summary>Surface pixels darker than this are asphalt, brighter are concrete / buildings. 0..1.</summary>
    public float LightSurfaceLuminance { get; set; } = 0.42f;
    /// <summary>Surface pixels darker than this are treated as shadow/void, not road. 0..1.</summary>
    public float MinSurfaceLuminance { get; set; } = 0.10f;
    /// <summary>
    /// How much the shapes are smoothed. 0 keeps the raw outlines; each step widens the blur used to round off jagged
    /// edges and drop speckle. Roughly a pixel radius at 600 px; scaled up for larger pictures.
    /// </summary>
    public int SmoothingPasses { get; set; } = 1;
    /// <summary>Outline thickness in pixels; 0 picks one from the image size.</summary>
    public int EdgeWidth { get; set; }
}

/// <summary>
/// Turns a top-down picture of a map (grass, tarmac, buildings) into the flat schematic the game shows for its own
/// maps: road and building fills with light outlines, scenery transparent or dark. Pure image code with no Unity
/// dependency, so it can be tested and tuned off-line. Multi-threaded; safe to call from any thread.
///
/// Steps: (1) classify each pixel as scenery / asphalt / concrete from its saturation and brightness, after a light
/// blur that averages out tyre marks and texture noise; (2) drop tiny islands and fill tiny holes; (3) turn the
/// classes into soft coverage masks and blur them, so edges become smooth curves instead of pixel staircases;
/// (4) draw fills and outlines from the blurred masks, anti-aliased, with the scenery transparent.
/// </summary>
public static class SchematicFilter
{
    private const byte Scenery = 0, Asphalt = 1, Concrete = 2;
    private const float GaussianPeak = 2.5066283f; // sqrt(2 * pi)

    /// <summary>Input and output are tightly packed RGBA, row-major, row 0 first.</summary>
    public static byte[] Apply(byte[] rgba, int width, int height, SchematicStyle? style = null)
    {
        style ??= new SchematicStyle();
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Pixel buffer does not match the stated size.");

        int n = width * height;
        // Everything scales with the picture, so a 4096 px map gets the same look as a 1024 px one.
        double unit = Math.Max(1.0, Math.Min(width, height) / 600.0);

        byte[] cls = Classify(rgba, width, height, style, Math.Max(1, (int)Math.Round(unit)));
        RemoveSmallRegions(cls, width, height, Scenery, Math.Max(12, n / 15000), onlyInterior: false);   // specks of road in the grass
        RemoveSmallRegions(cls, width, height, Asphalt | Concrete, Math.Max(24, n / 6000), onlyInterior: true); // holes in roads

        double radius = Math.Max(0.0, style.SmoothingPasses * unit);
        return Render(cls, width, height, style, radius, style.EdgeWidth > 0 ? style.EdgeWidth : Math.Max(2.4, 1.6 * unit));
    }

    // ---- 1. classification -------------------------------------------------------------------------------------

    private static byte[] Classify(byte[] rgba, int width, int height, SchematicStyle style, int blurRadius)
    {
        int n = width * height;
        var luminance = new float[n];
        var saturation = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                float r = rgba[i * 4] / 255f, g = rgba[i * 4 + 1] / 255f, b = rgba[i * 4 + 2] / 255f;
                float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                luminance[i] = 0.299f * r + 0.587f * g + 0.114f * b;
                saturation[i] = max > 0f ? (max - min) / max : 0f;
            }
        });
        // Light blur first: tyre marks, lane paint and grass speckle average out instead of flipping the class.
        BoxBlur(luminance, width, height, blurRadius, passes: 1);
        BoxBlur(saturation, width, height, blurRadius, passes: 1);

        var cls = new byte[n];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                if (saturation[i] >= style.SurfaceSaturationMax || luminance[i] <= style.MinSurfaceLuminance) cls[i] = Scenery;
                else cls[i] = luminance[i] < style.LightSurfaceLuminance ? Asphalt : Concrete;
            }
        });
        return cls;
    }

    // ---- 2. clean-up -------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds connected (4-neighbour) groups of pixels whose class is in <paramref name="classMask"/> (a bit set:
    /// Scenery = 0 is matched on its own; 3 means "any surface") and, if smaller than <paramref name="minPixels"/>,
    /// flips them to the neighbouring class that surrounds them. With <paramref name="onlyInterior"/> a group that
    /// touches the picture's border is kept, so the scenery around a map is never "filled in".
    /// </summary>
    private static void RemoveSmallRegions(byte[] cls, int width, int height, int classMask, int minPixels, bool onlyInterior)
    {
        // For classMask == Scenery we remove small *surface* islands; for 3 we remove small *scenery* holes.
        bool removeSurfaces = classMask == Scenery;
        bool InRegion(byte c) => removeSurfaces ? c != Scenery : c == Scenery;

        int n = cls.Length;
        var seen = new bool[n];
        var stack = new int[n];
        var region = new List<int>();
        var neighbourVotes = new int[3];
        int top = 0;
        for (int start = 0; start < n; start++)
        {
            if (seen[start] || !InRegion(cls[start])) continue;
            // Surfaces can hold both asphalt and concrete, so an island is a group of *any* surface pixels.
            region.Clear();
            Array.Clear(neighbourVotes);
            top = 0;
            stack[top++] = start;
            seen[start] = true;
            bool touchesBorder = false;
            while (top > 0)
            {
                int p = stack[--top];
                region.Add(p);
                int x = p % width, y = p / width;
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1) touchesBorder = true;
                if (x > 0) Visit(p - 1);
                if (x < width - 1) Visit(p + 1);
                if (y > 0) Visit(p - width);
                if (y < height - 1) Visit(p + width);
            }
            if (region.Count >= minPixels || (onlyInterior && touchesBorder)) continue;

            byte replacement = removeSurfaces ? Scenery : (byte)(neighbourVotes[Concrete] > neighbourVotes[Asphalt] ? Concrete : Asphalt);
            foreach (int p in region) cls[p] = replacement;
        }

        void Visit(int q)
        {
            if (InRegion(cls[q]))
            {
                if (seen[q]) return;
                seen[q] = true;
                stack[top++] = q;
            }
            else
            {
                neighbourVotes[cls[q]]++;
            }
        }
    }

    // ---- 3 + 4. smoothing and drawing --------------------------------------------------------------------------

    private static byte[] Render(byte[] cls, int width, int height, SchematicStyle style, double radius, double edgePixels)
    {
        int n = width * height;
        var asphalt = new float[n];
        var concrete = new float[n];
        var surface = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                asphalt[i] = cls[i] == Asphalt ? 1f : 0f;
                concrete[i] = cls[i] == Concrete ? 1f : 0f;
                surface[i] = cls[i] != Scenery ? 1f : 0f;
            }
        });

        // Two scales. A light blur keeps thin trails and decides asphalt vs concrete. A heavy blur gives wide roads
        // and buildings their smooth outline, because it averages out the wobble in the source picture's edges.
        int fine = (int)Math.Round(radius);
        int heavy = fine > 0 ? fine * 2 + 1 : 0;
        if (fine > 0)
        {
            BoxBlur(asphalt, width, height, fine, passes: 3);
            BoxBlur(concrete, width, height, fine, passes: 3);
            BoxBlur(surface, width, height, heavy, passes: 3);
        }

        // Shape = wide parts (heavy blur says "mostly road here") plus thin parts (the fine blur sees a road that the
        // heavy blur smeared away). The difference test keeps the bumps beside a wide road out of the thin set.
        var shape = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                float fineCover = Math.Min(1f, asphalt[i] + concrete[i]);
                float heavyCover = surface[i];
                bool wide = heavyCover >= 0.5f;
                bool thin = fineCover >= 0.45f && fineCover - heavyCover > 0.1f;
                shape[i] = wide || thin ? 1f : 0f;
            }
        });
        // A last small blur only anti-aliases the (already smooth) outline; it is the same everywhere, so every edge
        // gets the same crisp width.
        const int AntiAliasRadius = 1;
        BoxBlur(shape, width, height, AntiAliasRadius, passes: 2);
        double sigma = Math.Sqrt(2.0 * ((2.0 * AntiAliasRadius + 1) * (2.0 * AntiAliasRadius + 1) - 1) / 12.0);

        // Slope of the blurred edge at 0.5, per pixel. It turns "N pixels" into a coverage width.
        float slope = (float)(1.0 / (sigma * GaussianPeak));
        float aa = Math.Max(0.5f * slope, 0.02f);                 // anti-aliasing half-width, about one pixel
        float outline = Math.Max(0.5f * (float)edgePixels * slope, aa);
        float bgOpacity = Math.Clamp(style.BackgroundOpacity, 0f, 1f);

        var output = new byte[n * 4];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                float a = asphalt[i], c = concrete[i];
                float cover = shape[i];
                float concreteShare = a + c > 0.001f ? c / (a + c) : 0f;

                float fill = SmoothStep(0.5f - aa, 0.5f + aa, cover);
                float edge = 1f - SmoothStep(outline * 0.5f, outline, Math.Abs(cover - 0.5f)); // full strength in the core, soft shoulders
                float mix = SmoothStep(0.5f - 0.2f, 0.5f + 0.2f, concreteShare);
                float inner = 0.6f * Math.Clamp(1f - Math.Abs(concreteShare - 0.5f) / 0.12f, 0f, 1f) * SmoothStep(0.6f, 0.9f, cover);
                float visible = Math.Max(fill, edge);

                // Colour: asphalt -> concrete fill, outlined in the matching light colour. Left independent of
                // coverage so transparent edges don't pick up a dark halo from the background colour.
                float r = Lerp(style.Asphalt.R, style.Concrete.R, mix);
                float g = Lerp(style.Asphalt.G, style.Concrete.G, mix);
                float b = Lerp(style.Asphalt.B, style.Concrete.B, mix);
                float er = Lerp(style.AsphaltEdge.R, style.ConcreteEdge.R, mix);
                float eg = Lerp(style.AsphaltEdge.G, style.ConcreteEdge.G, mix);
                float eb = Lerp(style.AsphaltEdge.B, style.ConcreteEdge.B, mix);
                r = Lerp(r, er, edge); g = Lerp(g, eg, edge); b = Lerp(b, eb, edge);
                r = Lerp(r, style.ConcreteEdge.R, inner); g = Lerp(g, style.ConcreteEdge.G, inner); b = Lerp(b, style.ConcreteEdge.B, inner);

                float alpha = bgOpacity + (1f - bgOpacity) * visible;
                if (bgOpacity > 0f)
                {
                    r = Lerp(style.Background.R, r, visible);
                    g = Lerp(style.Background.G, g, visible);
                    b = Lerp(style.Background.B, b, visible);
                }
                output[i * 4] = (byte)Math.Clamp(r + 0.5f, 0f, 255f);
                output[i * 4 + 1] = (byte)Math.Clamp(g + 0.5f, 0f, 255f);
                output[i * 4 + 2] = (byte)Math.Clamp(b + 0.5f, 0f, 255f);
                output[i * 4 + 3] = (byte)Math.Clamp(alpha * 255f + 0.5f, 0f, 255f);
            }
        });
        return output;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // ---- box blur ----------------------------------------------------------------------------------------------

    /// <summary>Separable box blur with clamped edges, run <paramref name="passes"/> times, in place.</summary>
    private static void BoxBlur(float[] data, int width, int height, int radius, int passes)
    {
        if (radius <= 0) return;
        var temp = new float[data.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurRows(data, temp, width, height, radius);
            BlurColumns(temp, data, width, height, radius);
        }
    }

    private static void BlurRows(float[] source, float[] destination, int width, int height, int radius)
    {
        float inverse = 1f / (2 * radius + 1);
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            float sum = 0f;
            for (int k = -radius; k <= radius; k++) sum += source[row + Math.Clamp(k, 0, width - 1)];
            for (int x = 0; x < width; x++)
            {
                destination[row + x] = sum * inverse;
                sum += source[row + Math.Min(width - 1, x + radius + 1)] - source[row + Math.Max(0, x - radius)];
            }
        });
    }

    /// <summary>
    /// Vertical pass. Each worker owns a band of columns and walks down the picture keeping one running sum per
    /// column, so memory is read row by row (cache friendly) rather than jumping a full row for every pixel.
    /// </summary>
    private static void BlurColumns(float[] source, float[] destination, int width, int height, int radius)
    {
        float inverse = 1f / (2 * radius + 1);
        int bands = Math.Max(1, Math.Min(Environment.ProcessorCount * 2, width / 64));
        Parallel.For(0, bands, band =>
        {
            int x0 = band * width / bands, x1 = (band + 1) * width / bands, span = x1 - x0;
            var sums = new float[span];
            for (int k = -radius; k <= radius; k++)
            {
                int row = Math.Clamp(k, 0, height - 1) * width;
                for (int x = 0; x < span; x++) sums[x] += source[row + x0 + x];
            }
            for (int y = 0; y < height; y++)
            {
                int outRow = y * width + x0;
                int add = Math.Min(height - 1, y + radius + 1) * width + x0;
                int remove = Math.Max(0, y - radius) * width + x0;
                for (int x = 0; x < span; x++)
                {
                    destination[outRow + x] = sums[x] * inverse;
                    sums[x] += source[add + x] - source[remove + x];
                }
            }
        });
    }
}
