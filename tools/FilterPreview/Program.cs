using MapManager;

// Off-line harness for the schematic filter.
//   FilterPreview <in.rgba> <out.rgba>   raw file: int32 width, int32 height, then RGBA bytes
//   FilterPreview --selftest             checks the filter's basic behaviour; exit code 1 on failure
//   FilterPreview --bench <size>         times the filter on a synthetic square picture
if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
if (args.Length == 2 && args[0] == "--bench") return Bench(int.Parse(args[1]));
if (args.Length >= 1 && args[0] == "--corners") return Corners(args.Length > 1 ? args[1..].Select(float.Parse).ToArray() : new[] { 0f, 1.2f });
if (args.Length != 2) { Console.Error.WriteLine("usage: FilterPreview <in.rgba> <out.rgba> | --selftest | --bench <size>"); return 2; }

byte[] data = File.ReadAllBytes(args[0]);
int width = BitConverter.ToInt32(data, 0), height = BitConverter.ToInt32(data, 4);
byte[] rgba = data.AsSpan(8).ToArray();
var watch = System.Diagnostics.Stopwatch.StartNew();
byte[] result = SchematicFilter.Apply(rgba, width, height);
Console.WriteLine($"{width}x{height} filtered in {watch.ElapsedMilliseconds} ms");
using (var output = File.Create(args[1]))
{
    output.Write(BitConverter.GetBytes(width));
    output.Write(BitConverter.GetBytes(height));
    output.Write(result);
}
return 0;

// How square do rectangles stay? Draws grass with light "houses" of several sizes (straight and tilted), runs the filter,
// and reports the average corner radius of the result in pixels (0 = perfectly square).
static int Corners(float[] sharpness)
{
    foreach (float amount in sharpness)
    {
        var radii = MeasureCorners(amount, out var labels);
        Console.WriteLine($"sharpness {amount:0.0}: {string.Join("  ", labels.Zip(radii, (l, r) => $"{l}={r:0.0}px"))}");
    }
    return 0;
}

static List<double> MeasureCorners(float amount, out List<string> labels)
{
    labels = new List<string>();
    const int size = 900;
    var rects = new List<(double cx, double cy, double w, double h, double angle)>
    {
        (120, 120, 24, 16, 0), (240, 120, 40, 24, 0), (400, 130, 90, 50, 0), (600, 120, 30, 30, 0),
        (120, 300, 24, 16, 30), (260, 300, 40, 24, 30), (440, 300, 90, 50, 30), (640, 300, 30, 30, 30),
        (150, 520, 16, 60, 0), (300, 520, 14, 80, 0), (500, 520, 20, 120, 0), (700, 520, 50, 12, 0),
    };
    var picture = new byte[size * size * 4];
    var inside = new bool[rects.Count][];
    for (int k = 0; k < rects.Count; k++) inside[k] = new bool[size * size];
    var random = new Random(3);
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            (int r, int g, int b) = (84 + random.Next(-6, 6), 100 + random.Next(-6, 6), 40 + random.Next(-4, 4));
            for (int k = 0; k < rects.Count; k++)
            {
                var (cx, cy, w, h, angle) = rects[k];
                double a = angle * Math.PI / 180, dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double u = dx * Math.Cos(a) + dy * Math.Sin(a), v = -dx * Math.Sin(a) + dy * Math.Cos(a);
                if (Math.Abs(u) <= w / 2 && Math.Abs(v) <= h / 2) { inside[k][y * size + x] = true; (r, g, b) = (150 + random.Next(-4, 4), 154, 160); }
            }
            int i = (y * size + x) * 4;
            picture[i] = (byte)r; picture[i + 1] = (byte)g; picture[i + 2] = (byte)b; picture[i + 3] = 255;
        }
    }
    {
        byte[] result = SchematicFilter.Apply(picture, size, size, new SchematicStyle { CornerSharpness = amount });
        if (Environment.GetEnvironmentVariable("CORNERS_DUMP") is { } dump) { using var f = File.Create($"{dump}_{amount:0.0}.rgba"); f.Write(BitConverter.GetBytes(size)); f.Write(BitConverter.GetBytes(size)); f.Write(result); }
        var radii = new List<double>();
        for (int k = 0; k < rects.Count; k++)
        {
            // Only look near this rectangle, so neighbours do not count as misses.
            var (cx, cy, rw, rh, _) = rects[k];
            int reach = (int)(Math.Max(rw, rh) / 2 + 14);
            long both = 0, either = 0;
            for (int y = Math.Max(0, (int)cy - reach); y < Math.Min(size, (int)cy + reach); y++)
            {
                for (int x = Math.Max(0, (int)cx - reach); x < Math.Min(size, (int)cx + reach); x++)
                {
                    int i = y * size + x;
                    bool got = result[i * 4 + 3] >= 128, want = inside[k][i];
                    if (got && want) both++;
                    if (got || want) either++;
                }
            }
            // Corner radius: walk in from each corner along the diagonal until the picture is filled. A sharp corner is
            // filled at once; a corner rounded with radius r is only filled 0.414 * r pixels in.
            double radiusSum = 0;
            double ang = rects[k].angle * Math.PI / 180;
            foreach ((int sx, int sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                double distance = 0;
                for (double t = -1.5; t <= 20; t += 0.25)
                {
                    double ux = sx * (rw / 2 - t / Math.Sqrt(2) * 1.0), uy = sy * (rh / 2 - t / Math.Sqrt(2) * 1.0);
                    int px = (int)Math.Round(cx + ux * Math.Cos(ang) - uy * Math.Sin(ang)), py = (int)Math.Round(cy + ux * Math.Sin(ang) + uy * Math.Cos(ang));
                    if (px >= 0 && py >= 0 && px < size && py < size && result[(py * size + px) * 4 + 3] >= 128) { distance = Math.Max(0, t); break; }
                    distance = t;
                }
                radiusSum += distance / 0.414;
            }
            radii.Add(radiusSum / 4);
            labels.Add($"{rw}x{rh}{(rects[k].angle != 0 ? "@30" : "")}");
        }
        return radii;
    }
}

static byte[] Synthetic(int size)
{
    var picture = new byte[size * size * 4];
    var random = new Random(1);
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            // grass with noise, a dark asphalt ring, a light concrete block
            (int r, int g, int b) = (84 + random.Next(-12, 12), 100 + random.Next(-12, 12), 40 + random.Next(-8, 8));
            double dx = x - size / 2.0, dy = y - size / 2.0, d = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(d - size * 0.3) < size * 0.04) (r, g, b) = (60 + random.Next(-6, 6), 60 + random.Next(-6, 6), 58);
            if (x > size / 10 && x < size / 4 && y > size / 10 && y < size / 4) (r, g, b) = (150, 154, 160);
            int i = (y * size + x) * 4;
            picture[i] = (byte)r; picture[i + 1] = (byte)g; picture[i + 2] = (byte)b; picture[i + 3] = 255;
        }
    }
    return picture;
}

static int Bench(int size)
{
    byte[] picture = Synthetic(size);
    SchematicFilter.Apply(picture, size, size); // warm up the JIT
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 3; i++) SchematicFilter.Apply(picture, size, size);
    Console.WriteLine($"{size}x{size}: {watch.ElapsedMilliseconds / 3} ms per call on {Environment.ProcessorCount} cores");
    return 0;
}

static int SelfTest()
{
    const int w = 96, h = 96;
    var style = new SchematicStyle();
    var picture = new byte[w * h * 4];
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            // grass everywhere, a dark asphalt band across the middle, a light concrete block in a corner
            (byte r, byte g, byte b) = (84, 100, 40);
            if (y >= 40 && y < 56) (r, g, b) = (60, 60, 58);
            if (x >= 8 && x < 36 && y >= 8 && y < 30) (r, g, b) = (150, 154, 160);
            int i = (y * w + x) * 4;
            picture[i] = r; picture[i + 1] = g; picture[i + 2] = b; picture[i + 3] = 255;
        }
    }
    byte[] result = SchematicFilter.Apply(picture, w, h, style);

    (Rgb Colour, byte Alpha) At(byte[] pixels, int x, int y) => (new Rgb(pixels[(y * w + x) * 4], pixels[(y * w + x) * 4 + 1], pixels[(y * w + x) * 4 + 2]), pixels[(y * w + x) * 4 + 3]);
    var failures = new List<string>();
    if (At(result, 70, 80).Alpha != 0) failures.Add($"grass should be transparent, alpha is {At(result, 70, 80).Alpha}");
    if (At(result, 70, 48) != (style.Asphalt, (byte)255)) failures.Add($"asphalt band interior should be solid asphalt, got {At(result, 70, 48)}");
    if (At(result, 22, 19) != (style.Concrete, (byte)255)) failures.Add($"concrete block interior should be solid concrete, got {At(result, 22, 19)}");
    // Across the band's top edge there must be a light outline: a pixel clearly brighter than the asphalt fill.
    bool outlined = false;
    for (int y = 36; y <= 44; y++) if (At(result, 70, y).Alpha > 128 && At(result, 70, y).Colour.R > style.Asphalt.R + 40) outlined = true;
    if (!outlined) failures.Add("the asphalt band should have a light outline along its edge");
    if (result.Length != picture.Length) failures.Add("output size differs from input size");
    if (!Rgb.TryParse("#1E2329", out Rgb parsed) || parsed != new Rgb(0x1E, 0x23, 0x29)) failures.Add("hex colour parsing failed");

    style.BackgroundOpacity = 1f;
    byte[] opaque = SchematicFilter.Apply(picture, w, h, style);
    if (At(opaque, 70, 80) != (style.Background, (byte)255)) failures.Add($"with BackgroundOpacity 1 the grass should be the opaque background, got {At(opaque, 70, 80)}");

    // A one-pixel speck of road in the grass must not survive, and a one-pixel hole in a road must be filled.
    var speck = (byte[])picture.Clone();
    int s = (80 * w + 20) * 4; speck[s] = speck[s + 1] = speck[s + 2] = 60;
    int hole = (48 * w + 70) * 4; speck[hole] = 84; speck[hole + 1] = 100; speck[hole + 2] = 40;
    byte[] cleaned = SchematicFilter.Apply(speck, w, h, new SchematicStyle());
    if (At(cleaned, 20, 80).Alpha != 0) failures.Add("a one-pixel speck of road in the grass should be removed");
    if (At(cleaned, 70, 48) != (style.Asphalt, (byte)255)) failures.Add($"a one-pixel hole in the road should be filled, got {At(cleaned, 70, 48)}");

    // Large pictures: outline width used to be able to swallow the whole picture (everything opaque grey).
    foreach (int size in new[] { 1000, 1800 })
    {
        byte[] large = Synthetic(size);
        byte[] largeResult = SchematicFilter.Apply(large, size, size, new SchematicStyle());
        int transparent = 0, total = size * size;
        for (int i = 3; i < largeResult.Length; i += 4) if (largeResult[i] == 0) transparent++;
        double share = transparent / (double)total;
        if (share < 0.5) failures.Add($"at {size}x{size} the scenery should stay transparent, but only {share:P0} of the picture is");
    }

    // Buildings that touch must stay separate: a small tile against a big pad, and two stacked tiles with no gap.
    foreach ((string name, (int x, int y, int w, int h)[] houses, int expected) in new[]
    {
        ("two stacked tiles", new[] { (60, 40, 26, 34), (66, 74, 26, 34) }, 2),
        ("tile touching a pad's corner", new[] { (50, 30, 50, 80), (36, 60, 15, 22) }, 2),
        ("three in a row", new[] { (20, 40, 22, 30), (43, 40, 22, 30), (66, 40, 22, 30) }, 3),
    })
    {
        const int sz = 140;
        var pic = new byte[sz * sz * 4];
        for (int py = 0; py < sz; py++)
            for (int px = 0; px < sz; px++)
            {
                int i = (py * sz + px) * 4; (byte r, byte g, byte b) = (84, 100, 40);
                foreach (var (hx, hy, hw, hh) in houses) if (px >= hx && px < hx + hw && py >= hy && py < hy + hh) (r, g, b) = (150, 154, 160);
                pic[i] = r; pic[i + 1] = g; pic[i + 2] = b; pic[i + 3] = 255;
            }
        byte[] res = SchematicFilter.Apply(pic, sz, sz, new SchematicStyle());
        // count blobs of visible fill (alpha >= 200 and clearly the grey fill, not the light outline)
        var seen = new bool[sz * sz]; int blobs = 0;
        for (int start = 0; start < sz * sz; start++)
        {
            if (seen[start] || res[start * 4 + 3] < 200 || res[start * 4] > 140) continue;
            blobs++; var stack = new Stack<int>(); stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                int q = stack.Pop(); int qx = q % sz, qy = q / sz;
                foreach (int nb in new[] { qx > 0 ? q - 1 : -1, qx < sz - 1 ? q + 1 : -1, qy > 0 ? q - sz : -1, qy < sz - 1 ? q + sz : -1 })
                    if (nb >= 0 && !seen[nb] && res[nb * 4 + 3] >= 200 && res[nb * 4] <= 140) { seen[nb] = true; stack.Push(nb); }
            }
        }
        if (blobs != expected) failures.Add($"{name}: expected {expected} separate buildings, got {blobs}");
    }

    // A round house stays a perfect circle; a small square one must not turn into one.
    foreach ((string name, bool round) in new[] { ("round house", true), ("small square house", false) })
    {
        const int sz = 100;
        var pic = new byte[sz * sz * 4];
        for (int py = 0; py < sz; py++)
            for (int px = 0; px < sz; px++)
            {
                int i = (py * sz + px) * 4;
                bool inside = round ? (px - 50) * (px - 50) + (py - 50) * (py - 50) <= 12 * 12 : Math.Abs(px - 50) <= 7 && Math.Abs(py - 50) <= 7;
                (pic[i], pic[i + 1], pic[i + 2], pic[i + 3]) = inside ? ((byte)150, (byte)154, (byte)160, (byte)255) : ((byte)84, (byte)100, (byte)40, (byte)255);
            }
        byte[] res = SchematicFilter.Apply(pic, sz, sz, new SchematicStyle());
        int filled = 0;
        for (int i = 3; i < res.Length; i += 4) if (res[i] >= 128) filled++;
        double ideal = round ? Math.PI * 12 * 12 : 15 * 15;
        // the outline adds a ring; compare the covered area with the ideal shape grown by a small margin
        double ratio = filled / ideal;
        if (ratio < 0.95 || ratio > 1.8) failures.Add($"{name}: covers {ratio:0.00}x its true area");
        // corner pixel of the square's bounding box must stay empty for a circle and filled for a square
        int corner = ((50 - (round ? 12 : 7)) * sz + 50 - (round ? 12 : 7)) * 4 + 3;
        bool cornerFilled = res[corner] >= 128;
        if (round && cornerFilled) failures.Add("round house: its bounding-box corner is filled, so it is not round");
        if (!round && !cornerFilled) failures.Add("small square house: its corner is missing, so it came out round");
    }

    // Houses must stay square: no rectangle's corners may be rounded by more than a few pixels (they used to be ~15).
    List<double> cornerRadii = MeasureCorners(new SchematicStyle().CornerSharpness, out List<string> cornerLabels);
    for (int k = 0; k < cornerRadii.Count; k++)
        if (cornerRadii[k] > 8.0) failures.Add($"a {cornerLabels[k]} house has corners rounded by about {cornerRadii[k]:0.0} px; they should stay sharp");

    foreach (string failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    Console.WriteLine(failures.Count == 0 ? "selftest passed" : $"{failures.Count} check(s) failed");
    return failures.Count == 0 ? 0 : 1;
}
