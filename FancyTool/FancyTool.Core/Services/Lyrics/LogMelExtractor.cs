using System.Numerics;

namespace FancyToolAva.Services.Lyrics;

/// <summary>Qwen audio frontend: 16 kHz, periodic Hann 400, hop 160, 128 Slaney mel bands.</summary>
public sealed class LogMelExtractor
{
    private readonly double[] _hann = new double[400];
    private readonly Complex[] _roots = new Complex[400];
    private readonly float[,] _filters = new float[128, 201];
    public LogMelExtractor()
    {
        for (int i = 0; i < 400; i++)
        {
            _hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 400);
            _roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / 400);
        }
        static double Mel(double f) => f < 1000 ? f / (200.0 / 3) : 15 + Math.Log(f / 1000) / (Math.Log(6.4) / 27);
        static double Hz(double m) => m < 15 ? m * (200.0 / 3) : 1000 * Math.Exp((m - 15) * Math.Log(6.4) / 27);
        double[] edges = Enumerable.Range(0, 130).Select(i => Hz(Mel(8000) * i / 129)).ToArray();
        for (int m = 0; m < 128; m++)
            for (int b = 0; b <= 200; b++)
            {
                double f = b * 40;
                _filters[m, b] = (float)(Math.Max(0, Math.Min((f - edges[m]) / (edges[m + 1] - edges[m]),
                    (edges[m + 2] - f) / (edges[m + 2] - edges[m + 1]))) * 2 / (edges[m + 2] - edges[m]));
            }
    }
    public float[] Extract(ReadOnlySpan<float> audio, out int frames, CancellationToken ct)
    {
        if (audio.Length < 2) throw new InvalidDataException("Audio is too short.");
        frames = Math.Max(1, audio.Length / 160);
        var result = new float[128 * frames];
        var input = new double[400]; var spectrum = new Complex[400]; var power = new double[201];
        float maximum = float.NegativeInfinity;
        for (int t = 0; t < frames; t++)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < 400; i++)
            {
                int index = t * 160 + i - 200;
                while (index < 0 || index >= audio.Length)
                    index = index < 0 ? -index : 2 * audio.Length - 2 - index;
                input[i] = audio[index] * _hann[i];
            }
            Fourier(input, 0, 1, 400, spectrum, 0);
            for (int b = 0; b <= 200; b++) power[b] = spectrum[b].Real * spectrum[b].Real + spectrum[b].Imaginary * spectrum[b].Imaginary;
            for (int m = 0; m < 128; m++)
            {
                double sum = 0;
                for (int b = 0; b <= 200; b++) sum += power[b] * _filters[m, b];
                float value = (float)Math.Log10(Math.Max(1e-10, sum));
                result[m * frames + t] = value; maximum = Math.Max(maximum, value);
            }
        }
        for (int i = 0; i < result.Length; i++) result[i] = (Math.Max(result[i], maximum - 8) + 4) / 4;
        return result;
    }
    private void Fourier(double[] input, int start, int stride, int n, Complex[] output, int offset)
    {
        if (n == 1) { output[offset] = new Complex(input[start], 0); return; }
        int radix = n % 2 == 0 ? 2 : 5, length = n / radix;
        for (int r = 0; r < radix; r++) Fourier(input, start + r * stride, stride * radix, length, output, offset + r * length);
        Span<Complex> values = stackalloc Complex[5];
        for (int k = 0; k < length; k++)
        {
            for (int r = 0; r < radix; r++) values[r] = output[offset + r * length + k];
            for (int j = 0; j < radix; j++)
            {
                Complex sum = Complex.Zero;
                for (int r = 0; r < radix; r++) sum += values[r] * _roots[(r * (k + j * length) * (400 / n)) % 400];
                output[offset + j * length + k] = sum;
            }
        }
    }
}
