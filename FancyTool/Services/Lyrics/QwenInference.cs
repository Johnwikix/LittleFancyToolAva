using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using FancyToolAva.Models;

namespace FancyToolAva.Services.Lyrics;

public sealed class QwenInference : IDisposable
{
    public readonly record struct ScoredTranscription(string Text, double AverageLogProbability, int TokenCount);
    public readonly record struct LanguageScore(string Language, double Probability);

    private InferenceSession _encoder = null!;
    private InferenceSession _init = null!;
    private InferenceSession? _step;
    private FileStream? _embedding;
    private int _hidden;
    private readonly LogMelExtractor _mel = new();
    private readonly QwenTokenizer _tokenizer;
    private readonly bool _aligner;
    private readonly string _directory;
    private readonly bool _quantized;
    private readonly int _threads;
    private readonly int _dmlDeviceId;
    public string Backend { get; private set; } = "CPU";
    public string? GpuFallbackReason { get; private set; }

    public QwenInference(string directory, bool aligner, bool useDml = true, bool quantized = true, int threads = 0, int dmlDeviceId = 0)
    {
        _aligner = aligner;
        _directory = directory;
        _quantized = quantized;
        _threads = threads;
        _dmlDeviceId = dmlDeviceId;
        _tokenizer = new QwenTokenizer(directory);
        bool gpu = useDml && OperatingSystem.IsWindows();
        try { Load(gpu); }
        catch (Exception ex) when (gpu && IsGpuFailure(ex)) { FallBackToCpu(ex); }
    }
    private void Load(bool gpu)
    {
        using var options = new SessionOptions { EnableCpuMemArena = false, EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, IntraOpNumThreads = _threads > 0 ? _threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        if (gpu) options.AppendExecutionProvider_DML(_dmlDeviceId);
        else options.AppendExecutionProvider_CPU(0);
        string suffix = _quantized ? ".int4" : "";
        try
        {
            _encoder = new InferenceSession(Path.Combine(_directory, "encoder" + suffix + ".onnx"), options);
            _init = new InferenceSession(Path.Combine(_directory, "decoder_init" + suffix + ".onnx"), options);
            if (!_aligner)
            {
                _step = new InferenceSession(Path.Combine(_directory, "decoder_step" + suffix + ".onnx"), options);
                _embedding = new FileStream(Path.Combine(_directory, "embed_tokens.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "config.json")));
                _hidden = config.RootElement.GetProperty("decoder").GetProperty("hidden_size").GetInt32();
            }
            Backend = gpu ? "DirectML" : "CPU";
        }
        catch { Dispose(); throw; }
    }
    private static bool IsGpuFailure(Exception ex) => ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException ||
        ex is InvalidDataException { Message: "Non-finite model output." };
    private void FallBackToCpu(Exception ex)
    {
        GpuFallbackReason = ex.Message;
        System.Diagnostics.Trace.TraceWarning("Qwen {0}: DirectML failed; retrying on CPU. {1}", _aligner ? "aligner" : "ASR", ex.Message);
        Dispose();
        Load(false);
    }
    public string Transcribe(float[] audio, string language, CancellationToken ct)
        => TranscribeWithScore(audio, language, false, ct).Text;

    /// <summary>
    /// Runs the same greedy ASR path as <see cref="Transcribe"/> and also returns
    /// the mean log probability of the generated tokens. The score is used only
    /// by automatic language detection; regular extraction keeps the simpler
    /// string API and skips probability calculation.
    /// </summary>
    public ScoredTranscription TranscribeScored(float[] audio, string language, CancellationToken ct)
        => TranscribeWithScore(audio, language, true, ct);

    private ScoredTranscription TranscribeWithScore(float[] audio, string language, bool scoreTokens, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return TranscribeCore(audio, language, scoreTokens, ct); }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (Backend == "DirectML" && IsGpuFailure(ex) && !ct.IsCancellationRequested)
        {
            FallBackToCpu(ex);
            ct.ThrowIfCancellationRequested();
            return TranscribeCore(audio, language, scoreTokens, ct);
        }
    }
    public List<LanguageScore> GetLanguageScores(float[] audio, IReadOnlyList<string> languages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return GetLanguageScoresCore(audio, languages, ct); }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (Backend == "DirectML" && IsGpuFailure(ex) && !ct.IsCancellationRequested)
        {
            FallBackToCpu(ex);
            return GetLanguageScoresCore(audio, languages, ct);
        }
    }
    private List<LanguageScore> GetLanguageScoresCore(float[] audio, IReadOnlyList<string> languages, CancellationToken ct)
    {
        if (_aligner) throw new InvalidOperationException();
        using var run = new RunOptions();
        using var cancellation = ct.Register(() => run.Terminate = true);
        float[] mel = _mel.Extract(audio, out int frames, ct);
        using var input = OrtValue.CreateTensorValueFromMemory(mel, [1, 128, frames]);
        using var features = _encoder.Run(run, ["mel"], [input], ["audio_features"]);
        int featureCount = (int)features[0].GetTensorTypeAndShape().Shape[1];
        var (prompt, audioOffset) = RecognitionPrompt(featureCount, "Auto");
        prompt.AddRange(_tokenizer.Encode("language").Select(i => (long)i));
        long[] ids = prompt.ToArray(), positions = Enumerable.Range(0, ids.Length).Select(i => (long)i).ToArray();
        using var idInput = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        using var posInput = OrtValue.CreateTensorValueFromMemory(positions, [1, ids.Length]);
        using var offInput = OrtValue.CreateTensorValueFromMemory(new long[] { audioOffset }, [1]);
        using var output = _init.Run(run, ["input_ids", "position_ids", "audio_features", "audio_offset"], [idInput, posInput, features[0], offInput], ["logits"]);
        var logits = output[0].GetTensorDataAsSpan<float>()[^151936..];
        var values = new double[languages.Count];
        for (int i = 0; i < languages.Count; i++)
        {
            // All nine supported names are single tokens including their leading
            // space. Compare these logits in one prefill; no lyrics are decoded.
            int[] tokens = _tokenizer.Encode(" " + languages[i]);
            if (tokens.Length != 1) throw new InvalidOperationException("Language name must be a single token: " + languages[i]);
            values[i] = logits[tokens[0]];
            if (!double.IsFinite(values[i])) throw new InvalidDataException("Non-finite model output.");
        }
        double maximum = values.Max(), sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i] = Math.Exp(values[i] - maximum);
        var result = new List<LanguageScore>(values.Length);
        for (int i = 0; i < values.Length; i++) result.Add(new(languages[i], values[i] / sum));
        return result;
    }
    private (List<long> Prompt, int AudioOffset) RecognitionPrompt(int featureCount, string language)
    {
        var prompt = new List<long> { 151644 }; prompt.AddRange(_tokenizer.Encode("system\n").Select(i => (long)i));
        prompt.AddRange([151645, 198, 151644]); prompt.AddRange(_tokenizer.Encode("user\n").Select(i => (long)i)); prompt.Add(151669);
        int audioOffset = prompt.Count;
        prompt.AddRange(Enumerable.Repeat(151676L, featureCount)); prompt.AddRange([151670, 151645, 198, 151644]);
        prompt.AddRange(_tokenizer.Encode("assistant\n").Select(i => (long)i));
        if (language != "Auto") { prompt.AddRange(_tokenizer.Encode("language " + language).Select(i => (long)i)); prompt.Add(151704); }
        return (prompt, audioOffset);
    }
    private ScoredTranscription TranscribeCore(float[] audio, string language, bool scoreTokens, CancellationToken ct)
    {
        if (_aligner) throw new InvalidOperationException();
        using var run = new RunOptions();
        using var cancellation = ct.Register(() => run.Terminate = true);
        float[] mel = _mel.Extract(audio, out int frames, ct);
        using var input = OrtValue.CreateTensorValueFromMemory(mel, [1, 128, frames]);
        using var features = _encoder.Run(run, ["mel"], [input], ["audio_features"]);
        int featureCount = (int)features[0].GetTensorTypeAndShape().Shape[1];
        var (prompt, audioOffset) = RecognitionPrompt(featureCount, language);
        long[] ids = prompt.ToArray(), positions = Enumerable.Range(0, ids.Length).Select(i => (long)i).ToArray();
        using var idInput = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        using var posInput = OrtValue.CreateTensorValueFromMemory(positions, [1, ids.Length]);
        using var offInput = OrtValue.CreateTensorValueFromMemory(new long[] { audioOffset }, [1]);
        var output = _init.Run(run, ["input_ids", "position_ids", "audio_features", "audio_offset"], [idInput, posInput, features[0], offInput], ["logits", "present_keys", "present_values"]);
        var generated = new List<int>();
        try
        {
            var first = ArgMaxWithLogProbability(output[0].GetTensorDataAsSpan<float>(), 151936, scoreTokens);
            int next = first.Index;
            double logProbabilitySum = 0;
            int budget = Math.Clamp((int)(audio.Length / 16000.0 * 15) + 64, 64, 1024), position = ids.Length;
            var embeddings = new float[_hidden]; var bytes = new byte[_hidden * 2];
            while (next is not (151643 or 151645))
            {
                ct.ThrowIfCancellationRequested();
                if (generated.Count >= budget) throw new InvalidDataException("Lyrics.Error_TokenLimit");
                generated.Add(next);
                logProbabilitySum += first.LogProbability;
                _embedding!.Position = (long)next * _hidden * 2; _embedding.ReadExactly(bytes);
                for (int i = 0; i < _hidden; i++) embeddings[i] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i * 2));
                using var embInput = OrtValue.CreateTensorValueFromMemory(embeddings, [1, 1, _hidden]);
                using var positionInput = OrtValue.CreateTensorValueFromMemory(new long[] { position++ }, [1, 1]);
                var newer = _step!.Run(run, ["input_embeds", "position_ids", "past_keys", "past_values"], [embInput, positionInput, output[1], output[2]], ["logits", "present_keys", "present_values"]);
                output.Dispose(); output = newer;
                first = ArgMaxWithLogProbability(output[0].GetTensorDataAsSpan<float>(), 151936, scoreTokens);
                next = first.Index;
            }
            string text = _tokenizer.Decode(generated);
            double average = generated.Count == 0 ? double.NegativeInfinity : logProbabilitySum / generated.Count;
            return new ScoredTranscription(text, average, generated.Count);
        }
        finally { output.Dispose(); }
    }
    public List<LyricsWord> Align(float[] audio, string text, string language, int offsetMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return AlignCore(audio, text, language, offsetMs, ct); }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (Backend == "DirectML" && IsGpuFailure(ex) && !ct.IsCancellationRequested)
        {
            FallBackToCpu(ex);
            ct.ThrowIfCancellationRequested();
            return AlignCore(audio, text, language, offsetMs, ct);
        }
    }
    private List<LyricsWord> AlignCore(float[] audio, string text, string language, int offsetMs, CancellationToken ct)
    {
        if (!_aligner) throw new InvalidOperationException();
        var units = AlignmentUnits(text, language);
        if (units.Count == 0) return [];
        using var run = new RunOptions(); using var cancellation = ct.Register(() => run.Terminate = true);
        float[] mel = _mel.Extract(audio, out int frames, ct);
        using var input = OrtValue.CreateTensorValueFromMemory(mel, [1, 128, frames]);
        using var features = _encoder.Run(run, ["mel"], [input], ["audio_features"]);
        int count = (int)features[0].GetTensorTypeAndShape().Shape[1];
        var tokens = new List<long> { 151669 }; tokens.AddRange(Enumerable.Repeat(151676L, count)); tokens.Add(151670);
        var timestampPositions = new List<int>();
        foreach (string unit in units)
        {
            string clean = string.Concat(unit.EnumerateRunes().Where(r => System.Text.Rune.IsLetterOrDigit(r) || r.Value == '\'').Select(r => r.ToString()));
            tokens.AddRange(_tokenizer.Encode(clean).Select(x => (long)x));
            timestampPositions.Add(tokens.Count); tokens.Add(151705); timestampPositions.Add(tokens.Count); tokens.Add(151705);
        }
        long[] ids = tokens.ToArray(), pos = Enumerable.Range(0, tokens.Count).Select(i => (long)i).ToArray();
        using var idInput = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        using var posInput = OrtValue.CreateTensorValueFromMemory(pos, [1, ids.Length]);
        using var offset = OrtValue.CreateTensorValueFromMemory(new long[] { 1 }, [1]);
        using var output = _init.Run(run, ["input_ids", "position_ids", "audio_features", "audio_offset"], [idInput, posInput, features[0], offset], ["logits"]);
        var logits = output[0].GetTensorDataAsSpan<float>();
        var rawTimes = new int[timestampPositions.Count];
        for (int i = 0; i < rawTimes.Length; i++) rawTimes[i] = ArgMax(logits.Slice(timestampPositions[i] * 5000, 5000), 5000) * 80;
        int[] fixedTimes = TimestampRepair.Repair(rawTimes);
        var result = new List<LyricsWord>(); int duration = audio.Length / 16;
        for (int i = 0; i < units.Count; i++)
        {
            int start = Math.Clamp(fixedTimes[i * 2], 0, duration);
            int end = Math.Clamp(fixedTimes[i * 2 + 1], start, duration);
            result.Add(new LyricsWord { Text = units[i], StartMs = offsetMs + start, EndMs = offsetMs + end });
        }
        return result;
    }
    public static List<string> AlignmentUnits(string text, string language)
    {
        if (language == "Japanese")
        {
            var result = new List<string>();
            foreach (string segment in JapaneseSegmenter.Segment(text))
                if (segment.EnumerateRunes().Any(System.Text.Rune.IsLetterOrDigit)) result.Add(segment);
                else if (result.Count > 0) result[^1] += segment;
            return result;
        }
        // Match Qwen's mixed-CJK tokenization while retaining punctuation for export.
        if (language is "Chinese" or "Cantonese")
        {
            var result = new List<string>();
            foreach (var rune in text.EnumerateRunes())
            {
                if (System.Text.Rune.IsWhiteSpace(rune) || System.Text.Rune.IsPunctuation(rune))
                { if (result.Count > 0) result[^1] += rune; }
                else if (rune.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x2ceaf) result.Add(rune.ToString());
                else if (result.Count > 0 && result[^1].EnumerateRunes().All(r => r.Value < 0x3400)) result[^1] += rune;
                else result.Add(rune.ToString());
            }
            return result;
        }
        var spaced = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\S+\s*"))
            if (match.Value.EnumerateRunes().Any(System.Text.Rune.IsLetterOrDigit)) spaced.Add(match.Value);
            else if (spaced.Count > 0) spaced[^1] += match.Value;
        return spaced;
    }
    private static int ArgMax(ReadOnlySpan<float> logits, int width)
    {
        logits = logits[^width..]; int best = 0;
        for (int i = 1; i < logits.Length; i++) if (logits[i] > logits[best]) best = i;
        if (!float.IsFinite(logits[best])) throw new InvalidDataException("Non-finite model output.");
        return best;
    }
    private static (int Index, double LogProbability) ArgMaxWithLogProbability(ReadOnlySpan<float> logits, int width, bool scoreTokens)
    {
        logits = logits[^width..]; int best = 0;
        for (int i = 1; i < logits.Length; i++) if (logits[i] > logits[best]) best = i;
        float maximum = logits[best];
        if (!float.IsFinite(maximum)) throw new InvalidDataException("Non-finite model output.");
        if (!scoreTokens) return (best, 0);
        double sum = 0;
        for (int i = 0; i < logits.Length; i++) sum += Math.Exp(logits[i] - maximum);
        return (best, logits[best] - maximum - Math.Log(sum));
    }
    public void Dispose()
    {
        _embedding?.Dispose(); _embedding = null;
        _step?.Dispose(); _step = null;
        _init?.Dispose(); _init = null!;
        _encoder?.Dispose(); _encoder = null!;
    }
}
