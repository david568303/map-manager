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

    /// <summary>
    /// Pixels more colourful than this (grass, dirt, water) are scenery; greyer pixels are surfaces. 0..1.
    /// 0 (the default) picks the cut-off for each picture from its own colours, between <see cref="AutoSaturationMin"/>
    /// and <see cref="AutoSaturationMax"/>.
    /// </summary>
    public float SurfaceSaturationMax { get; set; }
    public const float AutoSaturationMin = 0.14f;
    public const float AutoSaturationMax = 0.24f;
    /// <summary>Surface pixels darker than this are asphalt, brighter are concrete / buildings. 0..1.</summary>
    public float LightSurfaceLuminance { get; set; } = 0.42f;
    /// <summary>Surface pixels darker than this are treated as shadow/void, not road. 0..1.</summary>
    public float MinSurfaceLuminance { get; set; } = 0.10f;
    /// <summary>
    /// How much the shapes are smoothed. 0 keeps the raw outlines; each step widens the blur used to round off jagged
    /// edges and drop speckle. Roughly a pixel radius at 600 px; scaled up for larger pictures.
    /// </summary>
    public int SmoothingPasses { get; set; } = 1;
    /// <summary>
    /// How strongly corners are restored after the smoothing blur, which otherwise rounds them off (square houses look
    /// round). 0 = off; around 1 keeps corners crisp without bringing back the wobble in road edges.
    /// </summary>
    public float CornerSharpness { get; set; } = 1.2f;
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
    /// <summary>Bump whenever the filter's output changes, so cached results from an older algorithm are not reused.</summary>
    public const int Algorithm = 18;

    private const byte Scenery = 0, Asphalt = 1, Concrete = 2;
    private const float GaussianPeak = 2.5066283f; // sqrt(2 * pi)
    private const float LightSurfaceSaturationMax = 0.14f;
    private const double SnapFill = 0.80;   // at least this much of its tilted bounding rectangle (a round blob scores about 0.785): draw the rectangle
    private const double LooseFill = 0.72;  // at least this much: keep the traced shape but skip the heavy smoothing

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
        // Buildings are found on a classification made without the blur: the blur closes the thin gaps between
        // neighbouring buildings and would fuse them into one shape. Only specks and pinholes are cleaned here.
        byte[] sharp = Classify(rgba, width, height, style, 0);
        RemoveSmallRegions(sharp, width, height, Scenery, 12, onlyInterior: false);
        RemoveSmallRegions(sharp, width, height, Asphalt | Concrete, 24, onlyInterior: true);
        RemoveSmallRegions(cls, width, height, Scenery, Math.Max(12, n / 15000), onlyInterior: false);   // specks of road in the grass
        RemoveSmallRegions(cls, width, height, Asphalt | Concrete, Math.Max(24, n / 6000), onlyInterior: true); // holes in roads

        double radius = Math.Max(0.0, style.SmoothingPasses * unit);
        return Render(cls, sharp, width, height, style, radius, style.EdgeWidth > 0 ? style.EdgeWidth : Math.Max(2.4, 1.6 * unit), unit);
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

        float saturationMax = style.SurfaceSaturationMax > 0f
            ? style.SurfaceSaturationMax
            : AutoSaturationCutoff(saturation, luminance, style.MinSurfaceLuminance);

        var cls = new byte[n];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                // Dark roads (asphalt, dirt tracks) are only slightly less colourful than dry grass, so they need the
                // generous cut-off. Light surfaces (concrete, buildings) are clearly grey, and their soft edges that fade
                // into the grass would turn ragged under it, so they get a stricter one.
                float limit = luminance[i] >= style.LightSurfaceLuminance ? Math.Min(saturationMax, LightSurfaceSaturationMax) : saturationMax;
                if (saturation[i] >= limit || luminance[i] <= style.MinSurfaceLuminance) cls[i] = Scenery;
                else cls[i] = luminance[i] < style.LightSurfaceLuminance ? Asphalt : Concrete;
            }
        });
        return cls;
    }

    /// <summary>
    /// Otsu's method on the saturation histogram: the cut-off that best separates "grey" (roads, buildings) from
    /// "colourful" (vegetation), limited to a sane range so an unusual map cannot swing it too far.
    /// </summary>
    private static float AutoSaturationCutoff(float[] saturation, float[] luminance, float minLuminance)
    {
        const int Bins = 64;
        var histogram = new long[Bins];
        long total = 0;
        for (int i = 0; i < saturation.Length; i++)
        {
            if (luminance[i] <= minLuminance) continue;
            histogram[Math.Min(Bins - 1, (int)(saturation[i] * Bins))]++;
            total++;
        }
        if (total == 0) return 0.5f * (SchematicStyle.AutoSaturationMin + SchematicStyle.AutoSaturationMax);

        double sumAll = 0;
        for (int b = 0; b < Bins; b++) sumAll += b * (double)histogram[b];
        double sumBelow = 0, best = -1;
        long below = 0;
        int bestBin = 0;
        for (int b = 0; b < Bins; b++)
        {
            below += histogram[b];
            if (below == 0 || below == total) continue;
            sumBelow += b * (double)histogram[b];
            double meanBelow = sumBelow / below, meanAbove = (sumAll - sumBelow) / (total - below);
            double between = (double)below * (total - below) * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best) { best = between; bestBin = b; }
        }
        float cutoff = (bestBin + 1f) / Bins;
        return Math.Clamp(cutoff, SchematicStyle.AutoSaturationMin, SchematicStyle.AutoSaturationMax);
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

    /// <summary>Each pixel becomes the most common class in its (2r+1)x(2r+1) neighbourhood; ties go to the lower class.</summary>
    private static byte[] Majority(byte[] cls, int width, int height, int radius)
    {
        var result = new byte[cls.Length];
        Parallel.For(0, height, y =>
        {
            Span<int> counts = stackalloc int[3];
            for (int x = 0; x < width; x++)
            {
                counts.Clear();
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int row = Math.Clamp(y + dy, 0, height - 1) * width;
                    for (int dx = -radius; dx <= radius; dx++)
                        counts[cls[row + Math.Clamp(x + dx, 0, width - 1)]]++;
                }
                byte best = 0;
                if (counts[1] > counts[best]) best = 1;
                if (counts[2] > counts[best]) best = 2;
                result[y * width + x] = best;
            }
        });
        return result;
    }

    /// <summary>
    /// Finds compact, rectangle-like objects (houses, pads): small connected groups of surface pixels that fill almost
    /// all of their own tilted bounding rectangle. Those have clean straight edges and sharp corners in the source, so
    /// they are drawn without the heavy smoothing that gives roads their smooth edges (which would round their corners
    /// into pills), and near-perfect ones are replaced by the fitted rectangle itself.
    ///
    /// Buildings that touch at a corner or along a thin bridge are told apart by shrinking every shape by
    /// <paramref name="erode"/> pixels first: the contact vanishes, each building keeps its own core, and the core's
    /// rectangle is then grown back to full size.
    /// </summary>
    private static bool[] FindBlocks(byte[] cls, int width, int height, int maxSide, int erode, out bool[] concreteBlock)
    {
        int n = cls.Length;
        var block = new bool[n];
        var concrete = new bool[n];    // for block pixels: true = draw as concrete (light), false = asphalt (dark)
        concreteBlock = concrete;
        var label = new int[n];        // which building a block pixel belongs to, so touching buildings can get a seam
        int nextLabel = 0;
        bool[] core = Erode(cls, width, height, erode);
        var seen = new bool[n];
        var stack = new int[n];
        var region = new List<int>();
        for (int start = 0; start < n; start++)
        {
            if (seen[start] || !core[start]) continue;
            region.Clear();
            int top = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            bool tooBig = false;
            stack[top++] = start;
            seen[start] = true;
            while (top > 0)
            {
                int p = stack[--top];
                region.Add(p);
                int x = p % width, y = p / width;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (maxX - minX > maxSide || maxY - minY > maxSide) tooBig = true;
                if (x > 0 && !seen[p - 1] && core[p - 1]) { seen[p - 1] = true; stack[top++] = p - 1; }
                if (x < width - 1 && !seen[p + 1] && core[p + 1]) { seen[p + 1] = true; stack[top++] = p + 1; }
                if (y > 0 && !seen[p - width] && core[p - width]) { seen[p - width] = true; stack[top++] = p - width; }
                if (y < height - 1 && !seen[p + width] && core[p + width]) { seen[p + width] = true; stack[top++] = p + width; }
            }
            if (tooBig || region.Count < 8) continue;
            HandleRegion(new List<int>(region), 0);
        }

        void HandleRegion(List<int> pixels, int depth)
        {
            // Smallest bounding rectangle over a set of angles. A rectangle at any tilt fills ~100% of it at the right
            // angle (squares have no principal axis, so the angles are simply tried); a ring, a curve or a ragged blob
            // fills much less at every angle.
            double mx = 0, my = 0;
            foreach (int p in pixels) { mx += p % width; my += p / width; }
            mx /= pixels.Count; my /= pixels.Count;
            double boxArea = double.MaxValue, bestCos = 1, bestSin = 0, bestUMin = 0, bestUMax = 0, bestVMin = 0, bestVMax = 0;
            for (int degrees = 0; degrees < 90; degrees += 3)
            {
                double angle = degrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
                double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
                foreach (int p in pixels)
                {
                    double dx = p % width - mx, dy = p / width - my;
                    double u = dx * cos + dy * sin, v = -dx * sin + dy * cos;
                    if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                    if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                }
                double area = (uMax - uMin + 1) * (vMax - vMin + 1);
                if (area < boxArea)
                {
                    boxArea = area; bestCos = cos; bestSin = sin;
                    bestUMin = uMin - 0.5; bestUMax = uMax + 0.5; bestVMin = vMin - 0.5; bestVMax = vMax + 0.5;
                }
            }
            int concreteCount = 0;
            foreach (int p in pixels) if (cls[p] == Concrete) concreteCount++;
            bool isConcrete = concreteCount * 2 >= pixels.Count;
            double fill = pixels.Count / boxArea;

            // Two buildings that touch (or overlap a little) form a stepped shape; if one cut
            // turns it into two near-perfect rectangles, treat them as two buildings with a thin seam between them.
            if (depth < 2 && fill < 0.97 && pixels.Count >= 40 && TrySplit(pixels, mx, my, bestCos, bestSin, fill, fill >= SnapFill ? 0.97 : 0.78, width, out List<int> first, out List<int> second, out double cutU, out double cutV, out bool cutAlongU))
            {
                int labelFloor = nextLabel;
                HandleRegion(first, depth + 1);
                HandleRegion(second, depth + 1);
                // carve the seam: a band about two pixels wide along the cut, so the two outlines stay separate
                double reach = Math.Abs(bestUMax - bestUMin) + Math.Abs(bestVMax - bestVMin) + 2 * erode;
                int x0 = Math.Max(0, (int)(mx - reach)), x1 = Math.Min(width - 1, (int)(mx + reach));
                int y0 = Math.Max(0, (int)(my - reach)), y1 = Math.Min(height - 1, (int)(my + reach));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        double dx = x - mx, dy = y - my;
                        double u = dx * bestCos + dy * bestSin, v = -dx * bestSin + dy * bestCos;
                        double along = cutAlongU ? u - cutU : v - cutV;
                        if (Math.Abs(along) <= 1.0 && block[y * width + x] && label[y * width + x] > labelFloor) block[y * width + x] = false;
                    }
                }
                return;
            }

            int id = ++nextLabel;
            if (fill >= SnapFill)
            {
                // A real rectangle with a noisy traced edge: draw the rectangle itself (the core's, grown back by the
                // erosion), straight sides and sharp corners.
                bestUMin -= erode; bestUMax += erode; bestVMin -= erode; bestVMax += erode;
                double reach = Math.Abs(bestUMax - bestUMin) + Math.Abs(bestVMax - bestVMin);
                int x0 = Math.Max(0, (int)(mx - reach)), x1 = Math.Min(width - 1, (int)(mx + reach));
                int y0 = Math.Max(0, (int)(my - reach)), y1 = Math.Min(height - 1, (int)(my + reach));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        double dx = x - mx, dy = y - my;
                        double u = dx * bestCos + dy * bestSin, v = -dx * bestSin + dy * bestCos;
                        if (u >= bestUMin && u <= bestUMax && v >= bestVMin && v <= bestVMax) { block[y * width + x] = true; concrete[y * width + x] = isConcrete; label[y * width + x] = id; }
                    }
                }
                return;
            }

            if (fill >= LooseFill)
            {
                // Roughly rectangular or round, but not a clean rectangle: grow the core back to full size, then draw it
                // with straight lines only (a simplified polygon) or, when it is round, as a perfect circle.
                var grown = new HashSet<int>(pixels);
                var frontier = new List<int>(pixels);
                for (int step = 0; step < erode; step++)
                {
                    var next = new List<int>();
                    foreach (int p in frontier)
                    {
                        int x = p % width, y = p / width;
                        void Grow(int q) { if (!block[q] && cls[q] != Scenery && grown.Add(q)) next.Add(q); }
                        if (x > 0) Grow(p - 1);
                        if (x < width - 1) Grow(p + 1);
                        if (y > 0) Grow(p - width);
                        if (y < height - 1) Grow(p + width);
                    }
                    frontier = next;
                }
                DrawStraight(grown, isConcrete, id);
            }
        }

        // Draws a shape using straight edges only (Douglas-Peucker simplified outline), or a perfect circle if it is round.
        void DrawStraight(HashSet<int> shape, bool isConcrete, int id)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            double cx = 0, cy = 0;
            foreach (int p in shape)
            {
                int x = p % width, y = p / width;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                cx += x; cy += y;
            }
            cx /= shape.Count; cy /= shape.Count;
            int bw = maxX - minX + 3, bh = maxY - minY + 3;
            var mask = new bool[bw * bh];
            foreach (int p in shape) mask[(p / width - minY + 1) * bw + (p % width - minX + 1)] = true;
            List<(double X, double Y)> contour = TraceOutline(mask, bw, bh);
            if (contour.Count < 3) return;

            void Paint(Func<double, double, bool> inside)
            {
                for (int y = minY - 1; y <= maxY + 1; y++)
                    for (int x = minX - 1; x <= maxX + 1; x++)
                    {
                        if (x < 0 || y < 0 || x >= width || y >= height || !inside(x - minX + 1, y - minY + 1)) continue;
                        block[y * width + x] = true; concrete[y * width + x] = isConcrete; label[y * width + x] = id;
                    }
            }

            double radius = Math.Sqrt(shape.Count / Math.PI), worst = 0;
            double ccx = cx - minX + 1, ccy = cy - minY + 1;
            foreach ((double X, double Y) c in contour) worst = Math.Max(worst, Math.Abs(Math.Sqrt((c.X - ccx) * (c.X - ccx) + (c.Y - ccy) * (c.Y - ccy)) - radius));
            if (radius >= 4 && worst <= Math.Max(0.9, radius * 0.07))
            {
                Paint((x, y) => (x - ccx) * (x - ccx) + (y - ccy) * (y - ccy) <= (radius + 0.5) * (radius + 0.5));
                return;
            }

            List<(double X, double Y)> polygon = Simplify(contour, Math.Max(1.3, erode * 0.7));
            if (polygon.Count < 3) return;
            Paint((x, y) =>
            {
                bool inside = false;
                double nearest = double.MaxValue;
                for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                {
                    (double X, double Y) a = polygon[i], b = polygon[j];
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                    double ex = b.X - a.X, ey = b.Y - a.Y, len2 = ex * ex + ey * ey;
                    double t = len2 > 0 ? Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / len2, 0, 1) : 0;
                    double dx = x - (a.X + t * ex), dy = y - (a.Y + t * ey);
                    nearest = Math.Min(nearest, dx * dx + dy * dy);
                }
                return inside || nearest <= 0.6;
            });
        }

        // Buildings whose grown-back rectangles touch or overlap: leave a thin seam where two different buildings meet,
        // so each keeps its own outline instead of merging into one shape.
        var seam = new List<int>();
        for (int p = 0; p < n; p++)
        {
            if (!block[p]) continue;
            int x = p % width, y = p / width;
            if ((x > 0 && block[p - 1] && label[p - 1] != label[p]) || (x < width - 1 && block[p + 1] && label[p + 1] != label[p]) ||
                (y > 0 && block[p - width] && label[p - width] != label[p]) || (y < height - 1 && block[p + width] && label[p + width] != label[p]))
                seam.Add(p);
        }
        foreach (int p in seam) block[p] = false;
        return block;
    }

    /// <summary>Share of the shape's convex hull that it fills: 1 for convex shapes, lower where there are notches.</summary>
    private static double Solidity(List<int> pixels, int width)
    {
        var points = new List<(double X, double Y)>(pixels.Count);
        foreach (int p in pixels) points.Add((p % width, p / width));
        points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double X, double Y)>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            for (int i = 0; i < points.Count; i++)
            {
                int k = pass == 0 ? i : points.Count - 1 - i;
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], points[k]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(points[k]);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        if (hull.Count < 3) return 1;
        double area = 0, perimeter = 0;
        for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++)
        {
            area += hull[j].X * hull[i].Y - hull[i].X * hull[j].Y;
            perimeter += Math.Sqrt((hull[i].X - hull[j].X) * (hull[i].X - hull[j].X) + (hull[i].Y - hull[j].Y) * (hull[i].Y - hull[j].Y));
        }
        double hullArea = Math.Abs(area) / 2 + perimeter / 2 + 1; // from pixel centres to pixel areas
        return Math.Min(1.0, pixels.Count / hullArea);
    }

    /// <summary>Area of the smallest tilted rectangle (angles tried in 5 degree steps) around points[from..to).</summary>
    private static double BestBoxArea(double[] xs, double[] ys, int from, int to)
    {
        double best = double.MaxValue;
        for (int degrees = 0; degrees < 90; degrees += 5)
        {
            double angle = degrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            for (int i = from; i < to; i++)
            {
                double u = xs[i] * cos + ys[i] * sin, v = -xs[i] * sin + ys[i] * cos;
                if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                if (v < vMin) vMin = v; if (v > vMax) vMax = v;
            }
            best = Math.Min(best, (uMax - uMin + 1) * (vMax - vMin + 1));
        }
        return best;
    }

    /// <summary>
    /// Looks for a straight cut (across or along the shape's best-fit rectangle) that turns the shape into two parts that
    /// are each close to a perfect rectangle: the pixel count over the sum of the two parts' bounding rectangles must
    /// be high, and clearly higher than for the shape as a whole.
    /// </summary>
    private static bool TrySplit(List<int> pixels, double mx, double my, double cos, double sin, double wholeFill, double minScore, int width,
        out List<int> first, out List<int> second, out double cutU, out double cutV, out bool cutAlongU)
    {
        first = second = new List<int>();
        cutU = cutV = 0;
        cutAlongU = true;
        int count = pixels.Count;
        if (Solidity(pixels, width) >= 0.93) return false; // convex (a single building, however skewed): nothing to split
        var us = new double[count];
        var vs = new double[count];
        double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            double dx = pixels[i] % width - mx, dy = pixels[i] / width - my;
            us[i] = dx * cos + dy * sin;
            vs[i] = -dx * sin + dy * cos;
            if (us[i] < uMin) uMin = us[i]; if (us[i] > uMax) uMax = us[i];
            if (vs[i] < vMin) vMin = vs[i]; if (vs[i] > vMax) vMax = vs[i];
        }

        double bestScore = 0, bestCut = 0;
        bool bestAlongU = true;
        for (int axis = 0; axis < 2; axis++)
        {
            double[] along = axis == 0 ? us : vs;
            double lo = axis == 0 ? uMin : vMin, hi = axis == 0 ? uMax : vMax;
            if (hi - lo < 12) continue;
            // pixels ordered along the axis, so each cut's two parts are a prefix and a suffix
            int[] order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (p, q) => along[p].CompareTo(along[q]));
            var xs = new double[count]; var ys = new double[count]; var sorted = new double[count];
            for (int i = 0; i < count; i++) { xs[i] = us[order[i]]; ys[i] = vs[order[i]]; sorted[i] = along[order[i]]; }
            double length = hi - lo;
            int minPart = Math.Max(5, (int)(length * 0.15));
            for (int cut = minPart; cut <= (int)length - minPart; cut++)
            {
                double position = lo + cut;
                int split = Array.BinarySearch(sorted, position);
                if (split < 0) split = ~split;
                if (split < 12 || count - split < 12) continue;
                double areaA = BestBoxArea(xs, ys, 0, split), areaB = BestBoxArea(xs, ys, split, count);
                double score = count / (areaA + areaB);
                if (score > bestScore) { bestScore = score; bestCut = position; bestAlongU = axis == 0; }
            }
        }
        if (bestScore < minScore || bestScore < wholeFill + (wholeFill >= SnapFill ? 0.12 : 0.04)) return false;

        first = new List<int>();
        second = new List<int>();
        for (int i = 0; i < count; i++) ((bestAlongU ? us[i] : vs[i]) < bestCut ? first : second).Add(pixels[i]);
        if (first.Count < 12 || second.Count < 12) return false;
        cutAlongU = bestAlongU;
        cutU = bestCut; cutV = bestCut;
        return true;
    }

    /// <summary>Outer outline of the first shape found in the mask, as the centres of its boundary pixels in order (Moore tracing).</summary>
    private static List<(double X, double Y)> TraceOutline(bool[] mask, int w, int h)
    {
        int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 }, dy = { 0, 1, 1, 1, 0, -1, -1, -1 };
        var outline = new List<(double X, double Y)>();
        int start = Array.IndexOf(mask, true);
        if (start < 0) return outline;
        int cx = start % w, cy = start / w, back = 4, firstDir = -1, guard = 0;
        while (guard++ < w * h * 4)
        {
            outline.Add((cx, cy));
            int found = -1;
            for (int i = 1; i <= 8; i++)
            {
                int d = (back + i) % 8, nx = cx + dx[d], ny = cy + dy[d];
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && mask[ny * w + nx]) { found = d; break; }
            }
            if (found < 0) break;
            if (firstDir < 0) firstDir = found;
            else if (cx == start % w && cy == start / w && found == firstDir) { outline.RemoveAt(outline.Count - 1); break; }
            int emptyDir = (found + 7) % 8;
            int ex = cx + dx[emptyDir], ey = cy + dy[emptyDir];
            cx += dx[found]; cy += dy[found];
            int bx = ex - cx, by = ey - cy;
            back = 0;
            for (int d = 0; d < 8; d++) if (dx[d] == bx && dy[d] == by) { back = d; break; }
        }
        return outline;
    }

    /// <summary>Douglas-Peucker on a closed outline: the fewest straight edges that stay within <paramref name="tolerance"/> pixels.</summary>
    private static List<(double X, double Y)> Simplify(List<(double X, double Y)> outline, double tolerance)
    {
        int n = outline.Count, far = 0;
        double best = -1;
        for (int i = 1; i < n; i++)
        {
            double d = (outline[i].X - outline[0].X) * (outline[i].X - outline[0].X) + (outline[i].Y - outline[0].Y) * (outline[i].Y - outline[0].Y);
            if (d > best) { best = d; far = i; }
        }
        var keep = new bool[n];
        keep[0] = keep[far] = true;
        void Run(int from, int to)
        {
            if (to - from < 2) return;
            (double X, double Y) a = outline[from % n], b = outline[to % n];
            double ex = b.X - a.X, ey = b.Y - a.Y, len = Math.Sqrt(ex * ex + ey * ey), worst = -1;
            int index = -1;
            for (int i = from + 1; i < to; i++)
            {
                (double X, double Y) p = outline[i % n];
                double d = len > 0 ? Math.Abs((p.X - a.X) * ey - (p.Y - a.Y) * ex) / len : Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
                if (d > worst) { worst = d; index = i; }
            }
            if (worst <= tolerance) return;
            keep[index % n] = true;
            Run(from, index);
            Run(index, to);
        }
        Run(0, far);
        Run(far, n);
        var result = new List<(double X, double Y)>();
        for (int i = 0; i < n; i++) if (keep[i]) result.Add(outline[i]);
        return result;
    }

    /// <summary>True where the whole (2r+1)x(2r+1) square around the pixel is surface; the picture's border counts as scenery.</summary>
    private static bool[] Erode(byte[] cls, int width, int height, int radius)
    {
        int n = cls.Length;
        var horizontal = new bool[n];
        Parallel.For(0, height, y =>
        {
            int row = y * width, bad = 0;
            for (int k = -radius; k <= radius; k++) if (k < 0 || k >= width || cls[row + k] == Scenery) bad++;
            for (int x = 0; x < width; x++)
            {
                horizontal[row + x] = bad == 0;
                int add = x + radius + 1, remove = x - radius;
                if (add < 0 || add >= width || cls[row + add] == Scenery) bad++;
                if (remove < 0 || remove >= width || cls[row + remove] == Scenery) bad--;
            }
        });
        var result = new bool[n];
        int bands = Math.Max(1, Math.Min(Environment.ProcessorCount * 2, width / 64));
        Parallel.For(0, bands, band =>
        {
            int x0 = band * width / bands, x1 = (band + 1) * width / bands, span = x1 - x0;
            var bad = new int[span];
            for (int k = -radius; k <= radius; k++)
                for (int x = 0; x < span; x++)
                    if (k < 0 || k >= height || !horizontal[k * width + x0 + x]) bad[x]++;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < span; x++) result[y * width + x0 + x] = bad[x] == 0;
                int add = y + radius + 1, remove = y - radius;
                for (int x = 0; x < span; x++)
                {
                    if (add < 0 || add >= height || !horizontal[add * width + x0 + x]) bad[x]++;
                    if (remove < 0 || remove >= height || !horizontal[remove * width + x0 + x]) bad[x]--;
                }
            }
        });
        return result;
    }

    // ---- 3 + 4. smoothing and drawing --------------------------------------------------------------------------

    private static byte[] Render(byte[] cls, byte[] sharp, int width, int height, SchematicStyle style, double radius, double edgePixels, double unit)
    {
        int n = width * height;
        var asphalt = new float[n];
        var concrete = new float[n];
        var surface = new float[n];
        // Compact rectangle-like objects keep their sharp corners: they skip the heavy smoothing and are added back as-is.
        bool[] block = FindBlocks(sharp, width, height, (int)Math.Max(40, 90 * unit), Math.Max(2, (int)Math.Round(unit * 1.3)), out bool[] concreteBlock);
        // Keep the road smoothing out of the few pixels around a block, or it would draw a rounded ghost of the building.
        var nearBlock = new float[n];
        Parallel.For(0, height, y => { for (int i = y * width, end = i + width; i < end; i++) nearBlock[i] = block[i] ? 1f : 0f; });
        BoxBlur(nearBlock, width, height, Math.Max(2, (int)Math.Round(unit * 1.5)), passes: 1);
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                asphalt[i] = cls[i] == Asphalt ? 1f : 0f;
                concrete[i] = cls[i] == Concrete ? 1f : 0f;
                surface[i] = cls[i] != Scenery && nearBlock[i] < 0.001f ? 1f : 0f;
            }
        });

        // Two scales. A light blur keeps thin trails and decides asphalt vs concrete. A heavy blur gives wide roads
        // and buildings their smooth outline, because it averages out the wobble in the source picture's edges.
        int fine = (int)Math.Round(radius);
        int heavy = fine > 0 ? fine + 2 : 0;
        if (fine > 0)
        {
            BoxBlur(asphalt, width, height, fine, passes: 3);
            BoxBlur(concrete, width, height, fine, passes: 3);
            BoxBlur(surface, width, height, heavy, passes: 3);
            if (style.CornerSharpness > 0f)
            {
                // Unsharp mask on the blurred coverage. Along a straight edge the coverage is 0.5 either way, so edges
                // do not move; at a corner the blur lowers the coverage, the mask pushes it back over 0.5, and the
                // corner comes back instead of staying rounded.
                var soft = (float[])surface.Clone();
                BoxBlur(soft, width, height, heavy, passes: 3);
                float amount = style.CornerSharpness;
                Parallel.For(0, height, y =>
                {
                    for (int i = y * width, end = i + width; i < end; i++)
                        surface[i] = Math.Clamp(surface[i] + amount * (surface[i] - soft[i]), 0f, 1f);
                });
            }
        }

        // Shape = wide parts (heavy blur says "mostly road here") plus thin parts (the fine blur sees a road that the
        // heavy blur smeared away). A thin part is only taken where there is no wide part within a few pixels, so the
        // little bumps along the edge of a wide road are not added back; only free-standing trails are.
        var wide = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++) wide[i] = surface[i] >= 0.5f ? 1f : 0f;
        });
        var nearWide = (float[])wide.Clone();
        BoxBlur(nearWide, width, height, Math.Max(2, fine + 1), passes: 1);
        var shape = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                float fineCover = Math.Min(1f, asphalt[i] + concrete[i]);
                // 0.35 rather than 0.5: free-standing thin roads get a little extra body so they read as roads, not hairlines.
                bool thin = fineCover >= 0.35f && nearWide[i] < 0.001f && nearBlock[i] < 0.001f;
                shape[i] = wide[i] > 0f || thin || block[i] ? 1f : 0f;
            }
        });
        // A last small blur only anti-aliases the (already smooth) outline; it is the same everywhere, so every edge
        // gets the same crisp width. It grows with the picture, so the outline keeps the same look at any resolution.
        int antiAliasRadius = Math.Clamp((int)Math.Round(unit * 0.8), 1, 8);
        BoxBlur(shape, width, height, antiAliasRadius, passes: 2);
        double sigma = Math.Sqrt(2.0 * ((2.0 * antiAliasRadius + 1) * (2.0 * antiAliasRadius + 1) - 1) / 12.0);

        // Slope of the blurred edge at 0.5, per pixel. It turns "N pixels" into a coverage width. The outline band is
        // capped well below the whole 0..1 range, or on a large picture it would swallow the scenery too.
        float slope = (float)(1.0 / (sigma * GaussianPeak));
        float aa = Math.Max(0.5f * slope, 0.02f);                 // anti-aliasing half-width, about one pixel
        float outline = Math.Clamp(0.5f * (float)edgePixels * slope, aa, 0.4f);
        float bgOpacity = Math.Clamp(style.BackgroundOpacity, 0f, 1f);

        var output = new byte[n * 4];
        Parallel.For(0, height, y =>
        {
            for (int i = y * width, end = i + width; i < end; i++)
            {
                float a = asphalt[i], c = concrete[i];
                float cover = shape[i];
                float concreteShare = block[i] ? (concreteBlock[i] ? 1f : 0f) : (a + c > 0.001f ? c / (a + c) : 0f);

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
