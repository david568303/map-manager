using MapManager;

// Off-line harness for the schematic filter.
//   FilterPreview <in.rgba> <out.rgba>   raw file: int32 width, int32 height, then RGBA bytes
//   FilterPreview --selftest             checks the filter's basic behaviour; exit code 1 on failure
//   FilterPreview --bench <size>         times the filter on a synthetic square picture
if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
if (args.Length == 2 && args[0] == "--bench") return Bench(int.Parse(args[1]));
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

    foreach (string failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    Console.WriteLine(failures.Count == 0 ? "selftest passed" : $"{failures.Count} check(s) failed");
    return failures.Count == 0 ? 0 : 1;
}
