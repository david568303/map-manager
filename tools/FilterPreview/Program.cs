using MapManager;

// Off-line harness for the schematic filter.
//   FilterPreview <in.rgba> <out.rgba>   raw file: int32 width, int32 height, then RGBA bytes
//   FilterPreview --selftest             checks the filter's basic behaviour; exit code 1 on failure
if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
if (args.Length != 2) { Console.Error.WriteLine("usage: FilterPreview <in.rgba> <out.rgba> | --selftest"); return 2; }

byte[] data = File.ReadAllBytes(args[0]);
int width = BitConverter.ToInt32(data, 0), height = BitConverter.ToInt32(data, 4);
byte[] rgba = data.AsSpan(8).ToArray();
byte[] result = SchematicFilter.Apply(rgba, width, height);
using (var output = File.Create(args[1]))
{
    output.Write(BitConverter.GetBytes(width));
    output.Write(BitConverter.GetBytes(height));
    output.Write(result);
}
return 0;

static int SelfTest()
{
    const int w = 64, h = 64;
    var style = new SchematicStyle();
    var picture = new byte[w * h * 4];
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            // grass everywhere, a dark asphalt band across the middle, a light concrete block in a corner
            (byte r, byte g, byte b) = (84, 100, 40);
            if (y >= 28 && y < 36) (r, g, b) = (60, 60, 58);
            if (x >= 6 && x < 22 && y >= 6 && y < 20) (r, g, b) = (150, 154, 160);
            int i = (y * w + x) * 4;
            picture[i] = r; picture[i + 1] = g; picture[i + 2] = b; picture[i + 3] = 255;
        }
    }
    byte[] result = SchematicFilter.Apply(picture, w, h, style);

    Rgb At(int x, int y) => new(result[(y * w + x) * 4], result[(y * w + x) * 4 + 1], result[(y * w + x) * 4 + 2]);
    var failures = new List<string>();
    if (At(50, 50) != style.Background) failures.Add($"grass should become the background, got {At(50, 50)}");
    if (At(40, 32) != style.Asphalt) failures.Add($"asphalt band interior should be asphalt, got {At(40, 32)}");
    if (At(14, 13) != style.Concrete) failures.Add($"concrete block interior should be concrete, got {At(14, 13)}");
    if (At(40, 28) != style.AsphaltEdge) failures.Add($"asphalt band edge should be outlined, got {At(40, 28)}");
    if (result.Length != picture.Length) failures.Add("output size differs from input size");
    if (!Rgb.TryParse("#1E2329", out Rgb parsed) || parsed != new Rgb(0x1E, 0x23, 0x29)) failures.Add("hex colour parsing failed");

    foreach (string failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    Console.WriteLine(failures.Count == 0 ? "selftest passed" : $"{failures.Count} check(s) failed");
    return failures.Count == 0 ? 0 : 1;
}
