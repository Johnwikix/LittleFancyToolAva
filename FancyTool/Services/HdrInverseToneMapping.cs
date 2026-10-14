using System.Globalization;
using System.Text;

namespace FancyToolAva.Services;

/// <summary>ITU-R BT.2446-1 Method A: nonlinear BT.2020 SDR RGB to linear 1000-nit HDR RGB.</summary>
internal static class HdrInverseToneMapping
{
    private static readonly object Gate = new();
    private const int LutSize = 65;

    public static string GetLutPath()
    {
        // Generated locally from the reference functions in section 4.2, Table 4:
        // https://www.itu.int/dms_pub/itu-r/opb/rep/R-REP-BT.2446-1-2021-PDF-E.pdf
        string path = Path.Combine(AppPaths.ModelsDirectory, "ColorLuts", "bt2446a-1000nits-v1.cube");
        lock (Gate)
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("TITLE \"BT.2446-1 Method A / linear HDR / 1000 nits\"");
                    writer.WriteLine($"LUT_3D_SIZE {LutSize}");
                    writer.WriteLine("DOMAIN_MIN 0 0 0");
                    writer.WriteLine("DOMAIN_MAX 1 1 1");
                    for (int b = 0; b < LutSize; b++)
                        for (int g = 0; g < LutSize; g++)
                            for (int r = 0; r < LutSize; r++)
                            {
                                var rgb = Convert(r / (double)(LutSize - 1), g / (double)(LutSize - 1), b / (double)(LutSize - 1));
                                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{rgb.R:G9} {rgb.G:G9} {rgb.B:G9}"));
                            }
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
    }

    private static (double R, double G, double B) Convert(double r, double g, double b)
    {
        double y = 0.2627 * r + 0.6780 * g + 0.0593 * b;
        if (y <= 0) return (0, 0, 0);
        double code = 255 * y;
        double exponent = code <= 70
            ? 1.8712e-5 * code * code - 2.7334e-3 * code + 1.3141
            : 2.8305e-6 * code * code - 7.4622e-4 * code + 1.2528;
        double hdrY = Math.Pow(code, exponent);
        double chromaScale = 1.075 * hdrY / y;
        double cb = (b - y) / 1.8814 * chromaScale;
        double cr = (r - y) / 1.4746 * chromaScale;
        return (Linear(hdrY + 1.4746 * cr),
            Linear(hdrY - 0.16455 * cb - 0.57135 * cr), Linear(hdrY + 1.8814 * cb));
    }

    private static double Linear(double value) => Math.Pow(Math.Clamp(value / 1000, 0, 1), 2.4);
}
