using System.Security.Cryptography;
using System.Text;

namespace FancyToolAva.Services;

/// <summary>给已校验的模型拼接 GPU 像素转换；保留权重、算子域和未知 protobuf 字段。</summary>
internal static class VideoPixelGraph
{
    internal const string Prefix = "fancytool_pixels_v1/";

    internal static string Create(string sourcePath, string kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] model = File.ReadAllBytes(sourcePath);
        var fields = Read(model);
        var graph = Read(fields.Single(f => f.Number == 7).Payload);
        string input = Name(graph.Single(f => f.Number == 11).Payload);
        string output = Name(graph.Single(f => f.Number == 12).Payload);
        var before = Read(Template(kind, "prefix"));
        var after = Read(Template(kind, "suffix"));
        var reduction = Path.GetFileName(sourcePath) == "ravu_lite_r3_fp32.onnx" ? Read(Template("ravu3", "reduction")) : null;
        using var combined = new MemoryStream();
        foreach (var field in before.Where(f => f.Number == 1)) WriteNode(combined, field, input, output);
        foreach (var field in graph.Where(f => f.Number == 1))
        {
            var node = reduction == null ? null : Read(field.Payload);
            if (node != null && Encoding.UTF8.GetString(node.Single(f => f.Number == 4).Payload.Span) == "Einsum")
            {
                var equation = node.Where(f => f.Number == 5).Select(f => Read(f.Payload))
                    .Single(a => Name(a.Single(f => f.Number == 1)) == "equation");
                if (Name(equation.Single(f => f.Number == 4)) != "hwzk,hwk->zhw")
                    throw new NotSupportedException("Unsupported RAVU reduction equation");
                var arguments = node.Where(f => f.Number == 1).Select(Name).ToArray();
                string result = Name(node.Single(f => f.Number == 2));
                foreach (var replacement in reduction!.Where(f => f.Number == 1))
                    WriteNode(combined, replacement, arguments[0], arguments[1], result);
            }
            else combined.Write(field.Raw.Span);
        }
        foreach (var field in after.Where(f => f.Number == 1)) WriteNode(combined, field, input, output);
        foreach (var field in graph.Where(f => f.Number is not (1 or 11 or 12))) combined.Write(field.Raw.Span);
        foreach (var field in before.Where(f => f.Number is 5 or 11)) combined.Write(field.Raw.Span);
        foreach (var field in after.Where(f => f.Number is 5 or 12)) combined.Write(field.Raw.Span);
        if (reduction != null) foreach (var field in reduction.Where(f => f.Number == 5)) combined.Write(field.Raw.Span);
        using var adapted = new MemoryStream(model.Length + (int)combined.Length / 32);
        foreach (var field in fields)
        {
            if (field.Number == 7) Write(adapted, 7, combined.GetBuffer().AsSpan(0, (int)combined.Length));
            else adapted.Write(field.Raw.Span);
        }
        return Save(adapted.ToArray(), ct);
    }

    internal static (string Before, string After) CreateStages(string sourcePath, string kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fields = Read(File.ReadAllBytes(sourcePath));
        string Stage(string part)
        {
            byte[] graph = Template(kind, part);
            using var model = new MemoryStream();
            foreach (var field in fields)
            {
                if (field.Number == 7) Write(model, 7, graph);
                else model.Write(field.Raw.Span);
            }
            return Save(model.ToArray(), ct);
        }
        return (Stage("prefix"), Stage("suffix"));
    }

    private static string Save(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string directory = Path.Combine(AppPaths.ModelsDirectory, "Cache", "VideoPixels");
        Directory.CreateDirectory(directory);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string path = Path.Combine(directory, hash + ".onnx");
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(existing)) == hash) return path;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    private static byte[] Template(string kind, string part)
    {
        using var stream = typeof(VideoPixelGraph).Assembly.GetManifestResourceStream($"FancyToolAva.VideoPixels.{kind}-{part}.pb")
            ?? throw new InvalidOperationException("Missing video pixel adapter");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string Name(ReadOnlyMemory<byte> valueInfo) =>
        Encoding.UTF8.GetString(Read(valueInfo).Single(f => f.Number == 1).Payload.Span);
    private static string Name(Field field) => Encoding.UTF8.GetString(field.Payload.Span);

    private static void WriteNode(Stream target, Field node, string input, string output, string? result = null)
    {
        using var value = new MemoryStream();
        foreach (var field in Read(node.Payload))
        {
            if (field.Number is 1 or 2)
            {
                string name = Encoding.UTF8.GetString(field.Payload.Span);
                if (name is "__source_input__" or "__source_output__" or "__source_result__")
                {
                    Write(value, field.Number, Encoding.UTF8.GetBytes(name switch
                    { "__source_input__" => input, "__source_output__" => output, _ => result ?? throw new InvalidDataException("Missing adapter output") }));
                    continue;
                }
            }
            value.Write(field.Raw.Span);
        }
        Write(target, 1, value.GetBuffer().AsSpan(0, (int)value.Length));
    }

    // ONNX protobuf 只在建会话时处理。原始字段原样复制，不解析或重写网络权重。
    private readonly record struct Field(int Number, ReadOnlyMemory<byte> Raw, ReadOnlyMemory<byte> Payload);
    private static List<Field> Read(ReadOnlyMemory<byte> data)
    {
        var fields = new List<Field>();
        int offset = 0;
        while (offset < data.Length)
        {
            int start = offset;
            ulong tag = Varint(data.Span, ref offset);
            int length;
            switch (tag & 7)
            {
                case 0: Varint(data.Span, ref offset); fields.Add(new((int)(tag >> 3), data[start..offset], default)); continue;
                case 1: length = 8; break;
                case 2: length = checked((int)Varint(data.Span, ref offset)); break;
                case 5: length = 4; break;
                default: throw new InvalidDataException("Unsupported ONNX protobuf wire type");
            }
            if (length > data.Length - offset) throw new InvalidDataException("Truncated ONNX protobuf");
            var payload = data.Slice(offset, length);
            offset += length;
            fields.Add(new((int)(tag >> 3), data[start..offset], payload));
        }
        return fields;
    }
    private static ulong Varint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            byte b = data[offset++];
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
        }
        throw new InvalidDataException("Invalid ONNX protobuf varint");
    }
    private static void Write(Stream stream, int field, ReadOnlySpan<byte> bytes)
    {
        WriteVarint(stream, (uint)(field << 3 | 2));
        WriteVarint(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }
    private static void WriteVarint(Stream stream, uint value)
    {
        while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
    }
}
