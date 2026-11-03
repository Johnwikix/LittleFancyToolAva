using System.Text;

namespace FancyToolAva.Cli;

/// <summary>CLI 选项元数据；Name 使用 “-i, --input” 形式，帮助与参数校验共用。</summary>
public sealed record CliOptionSpec(string Name, string? Value, string DescriptionZh, string DescriptionEn, bool Required = false)
{
    public IEnumerable<string> Tokens => Name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>一条 CLI 命令的完整说明；合并的路径同时用于帮助、技能文档与分发校验。</summary>
public sealed record CliCommandSpec(string Path, string SummaryZh, string SummaryEn, string Usage,
    IReadOnlyList<CliOptionSpec> Options, IReadOnlyList<string> Examples)
{
    public string Group => Path.Split(' ', 2)[0];
}

/// <summary>GUI 与 CLI 共用的命令目录：每一项 GUI 功能都有对应命令和独立 -help。</summary>
public static class CommandCatalog
{
    public const string ToolName = "fancy-tool";

    public static IReadOnlyList<CliOptionSpec> GlobalOptions { get; } =
    [
        new("-h, --help", null, "显示当前命令或命令组的帮助", "Show help for the command or group"),
        new("-lang, --lang", "<zh|en>", "帮助与错误消息语言（默认跟随系统）", "Help and error language (default: system)"),
        new("--json", null, "以 JSON 输出机器可读结果", "Emit machine-readable JSON"),
        new("-q, --quiet", null, "不输出进度，只保留结果与错误", "Suppress progress output; keep results and errors"),
        new("--version", null, "显示版本", "Show version"),
    ];

    private static readonly CliOptionSpec Text = new("-t, --text", "<text>", "直接输入文本", "Literal input text");
    private static readonly CliOptionSpec Input = new("-i, --input", "<file>", "从文件读取输入", "Read input from a file");
    private static readonly CliOptionSpec Stdin = new("--stdin", null, "从标准输入读取", "Read input from standard input");
    private static readonly CliOptionSpec Out = new("-o, --out", "<file>", "写入结果文件（默认输出到控制台）", "Write the result to a file (default: stdout)");
    private static readonly CliOptionSpec Key = new("-k, --key", "<value>", "密钥或密钥文件", "Key value or key file");
    private static readonly CliOptionSpec KeyValue = new("--key-value", "<text>", "直接传入密钥文本（与 -k 二选一）", "Inline key text (instead of -k)");
    private static readonly CliOptionSpec Iv = new("--iv", "<value>", "初始化向量；缺省时自动生成", "Initialization vector; generated when omitted");
    private static readonly CliOptionSpec KeyType = new("--key-type", "<text|base64|hex>", "密钥/IV 编码，默认 text", "Key/IV encoding (default: text)");
    private static readonly CliOptionSpec KeyLength = new("--key-length", "<bits>", "密钥长度（AES 128/192/256，DES 64，SM4 128）", "Key size (AES 128/192/256, DES 64, SM4 128)");
    private static readonly CliOptionSpec Padding = new("--padding", "<mode>", "填充模式", "Padding mode");
    private static readonly CliOptionSpec CipherMode = new("--mode", "<ECB|CBC>", "分组模式，默认 CBC", "Cipher mode (default: CBC)");
    private static readonly CliOptionSpec OutputFormat = new("--output-format", "<base64|hex>", "密文输出编码，默认 base64", "Ciphertext encoding (default: base64)");
    private static readonly CliOptionSpec Lower = new("--lower", null, "使用小写十六进制", "Use lowercase hexadecimal");
    private static readonly CliOptionSpec Force = new("--force", null, "覆盖已有文件或受管内容", "Overwrite existing files or managed blocks");

    public static IReadOnlyList<CliCommandSpec> Commands { get; } = Build();

    public static CliCommandSpec? Find(string path)
    {
        foreach (var command in Commands)
        {
            if (string.Equals(command.Path, path, StringComparison.OrdinalIgnoreCase)) return command;
        }
        return null;
    }

    public static IEnumerable<IGrouping<string, CliCommandSpec>> Groups =>
        Commands.GroupBy(c => c.Group, StringComparer.OrdinalIgnoreCase);

    public static bool IsGroup(string name) =>
        Commands.Any(c => string.Equals(c.Group, name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> GroupOrder { get; } =
    [
        "aes", "des", "sm4", "rsa", "sm2", "hash", "base64", "file", "image", "video", "audio",
        "serial", "tcp", "udp", "linktest", "models", "lyrics", "ffmpeg", "skill",
    ];

    private static IReadOnlyList<CliCommandSpec> Build() =>
    [
        // ── 对称加密 ──────────────────────────────────────────────────────
        .. Symmetric("aes", "AES", "AES", ["PKCS7", "Zeros", "None"], "128|192|256", "128"),
        .. Symmetric("des", "DES", "DES", ["PKCS7", "Zeros", "None"], "64", "64"),
        .. Symmetric("sm4", "SM4", "SM4", ["PKCS7", "ISO10126", "ZEROBYTE"], "128", "128"),

        // ── RSA ──────────────────────────────────────────────────────────
        new("rsa keygen", "生成 RSA 密钥对", "Generate an RSA key pair",
            "rsa keygen [-l <bits>] [--format <PKCS#1|PKCS#8>] [--out-dir <dir>] [--prefix <name>] [--json]",
            [
                new("-l, --key-length", "<bits>", "密钥长度 1024/2048/4096，默认 2048", "Key size 1024/2048/4096 (default: 2048)"),
                new("--format", "<PKCS#1|PKCS#8>", "私钥格式，默认 PKCS#1", "Private key format (default: PKCS#1)"),
                new("--out-dir", "<dir>", "写入 PEM 文件而不是输出到控制台", "Write PEM files instead of printing them"),
                new("--prefix", "<name>", "输出文件名前缀，默认 rsa", "Output file prefix (default: rsa)"),
                Force,
                new("--json", null, "以 JSON 输出", "Emit JSON"),
            ],
            ["fancy-tool rsa keygen -l 4096 --out-dir .\\keys --prefix app"]),
        new("rsa encrypt", "使用 RSA 公钥加密文本", "Encrypt text with an RSA public key",
            "rsa encrypt (-t <text> | -i <file> | --stdin) (-k <public.pem> | --key-value <pem>) [--padding <mode>] [-o <file>]",
            [Text, Input, Stdin, Key, KeyValue,
                new("--padding", "<Pkcs1|OaepSHA1|OaepSHA256|OaepSHA384|OaepSHA512>", "填充模式，默认 Pkcs1", "Padding mode (default: Pkcs1)"),
                new("-l, --key-length", "<bits>", "密钥长度提示，默认 2048", "Key size hint (default: 2048)"), Out],
            ["fancy-tool rsa encrypt -t \"hello\" -k public.pem"]),
        new("rsa decrypt", "使用 RSA 私钥解密文本", "Decrypt text with an RSA private key",
            "rsa decrypt (-t <base64> | -i <file> | --stdin) (-k <private.pem> | --key-value <pem>) [--padding <mode>] [-o <file>]",
            [Text, Input, Stdin, Key, KeyValue,
                new("--padding", "<Pkcs1|OaepSHA1|OaepSHA256|OaepSHA384|OaepSHA512>", "填充模式，默认 Pkcs1", "Padding mode (default: Pkcs1)"),
                new("-l, --key-length", "<bits>", "密钥长度提示，默认 2048", "Key size hint (default: 2048)"), Out],
            ["fancy-tool rsa decrypt -t <base64> -k private.pem"]),
        new("rsa file-encrypt", "使用 RSA 公钥加密文件（分块）", "Encrypt a file in blocks with an RSA public key",
            "rsa file-encrypt -i <file> -o <file> (-k <public.pem> | --key-value <pem>) [--force]",
            [Input, Out, Key, KeyValue, Force,
                new("-l, --key-length", "<bits>", "密钥长度提示，默认 2048", "Key size hint (default: 2048)")],
            ["fancy-tool rsa file-encrypt -i secret.zip -o secret.zip.rsa -k public.pem"]),
        new("rsa file-decrypt", "使用 RSA 私钥解密文件", "Decrypt a file with an RSA private key",
            "rsa file-decrypt -i <file> -o <file> (-k <private.pem> | --key-value <pem>) [--force]",
            [Input, Out, Key, KeyValue, Force,
                new("-l, --key-length", "<bits>", "密钥长度提示，默认 2048", "Key size hint (default: 2048)")],
            ["fancy-tool rsa file-decrypt -i secret.zip.rsa -o secret.zip -k private.pem"]),

        // ── SM2 ──────────────────────────────────────────────────────────
        new("sm2 keygen", "生成 SM2 密钥对", "Generate an SM2 key pair",
            "sm2 keygen [--out-dir <dir>] [--prefix <name>] [--json]",
            [new("--out-dir", "<dir>", "写入密钥文件而不是输出到控制台", "Write key files instead of printing them"),
                new("--prefix", "<name>", "输出文件名前缀，默认 sm2", "Output file prefix (default: sm2)"),
                Force,
                new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool sm2 keygen --out-dir .\\keys"]),
        new("sm2 encrypt", "使用 SM2 公钥加密文本", "Encrypt text with an SM2 public key",
            "sm2 encrypt (-t <text> | -i <file> | --stdin) (-k <public.txt> | --key-value <base64>) [--mode <C1C2C3|C1C3C2>] [-o <file>]",
            [Text, Input, Stdin, Key, KeyValue,
                new("--mode", "<C1C2C3|C1C3C2>", "密文排列，默认 C1C2C3", "Ciphertext layout (default: C1C2C3)"), Out],
            ["fancy-tool sm2 encrypt -t \"hello\" -k public.txt"]),
        new("sm2 decrypt", "使用 SM2 私钥解密文本", "Decrypt text with an SM2 private key",
            "sm2 decrypt (-t <base64> | -i <file> | --stdin) (-k <private.txt> | --key-value <base64>) [--mode <C1C2C3|C1C3C2>] [-o <file>]",
            [Text, Input, Stdin, Key, KeyValue,
                new("--mode", "<C1C2C3|C1C3C2>", "密文排列，默认 C1C2C3", "Ciphertext layout (default: C1C2C3)"), Out],
            ["fancy-tool sm2 decrypt -t <base64> -k private.txt"]),

        // ── 哈希 ─────────────────────────────────────────────────────────
        Hash("md5", "MD5", "MD5"),
        Hash("sha1", "SHA-1", "SHA-1"),
        Hash("sha256", "SHA-256", "SHA-256"),
        Hash("sha384", "SHA-384", "SHA-384"),
        Hash("sha512", "SHA-512", "SHA-512"),
        Hash("sm3", "SM3", "SM3"),

        // ── Base64 ───────────────────────────────────────────────────────
        new("base64 encode", "将文本或文件编码为 Base64", "Encode text or a file as Base64",
            "base64 encode (-t <text> | -i <file> | --stdin) [-o <file>]",
            [Text, Input, Stdin, Out],
            ["fancy-tool base64 encode -t \"hello\"", "fancy-tool base64 encode -i image.png -o image.b64"]),
        new("base64 decode", "解码 Base64 文本", "Decode Base64 text",
            "base64 decode (-t <base64> | -i <file> | --stdin) [-o <file>]",
            [Text, Input, Stdin, Out],
            ["fancy-tool base64 decode -t aGVsbG8="]),

        // ── 文件 ─────────────────────────────────────────────────────────
        new("file encrypt", "使用 AES 加密文件（GUI 文件加密页同等行为）", "Encrypt a file with AES (same as the GUI file-encryption page)",
            "file encrypt -i <file> [-i <file> ...] [-o <file> | --out-dir <dir>] [-k <key> | --key-file <key-iv.json>] [--iv <iv>] [--key-type <text|base64|hex>] [--key-length <128|192|256>] [-q]",
            [Input, Out,
                new("--out-dir", "<dir>", "批量输出目录；可重复 -i", "Batch output directory; -i can be repeated"), Key, Iv, KeyType,
                new("--key-file", "<json>", "读取 GUI 导出的 Key/Iv/Format JSON", "Read a GUI-exported Key/Iv/Format JSON file"),
                KeyLength with { DescriptionZh = "密钥长度：128|192|256，默认 128", DescriptionEn = "Key size: 128|192|256 (default: 128)" },
                Force,
                new("-q, --quiet", null, "不输出进度", "Suppress progress output")],
            ["fancy-tool file encrypt -i report.pdf -o report.pdf.enc --key-type hex -k <hex-key> --iv <hex-iv>"]),
        new("file decrypt", "使用 AES 解密文件", "Decrypt a file with AES",
            "file decrypt -i <file> [-i <file> ...] [-o <file> | --out-dir <dir>] [-k <key> | --key-file <key-iv.json>] [--iv <iv>] [--key-type <text|base64|hex>] [--key-length <128|192|256>] [-q]",
            [Input, Out,
                new("--out-dir", "<dir>", "批量输出目录；可重复 -i", "Batch output directory; -i can be repeated"), Key, Iv, KeyType,
                new("--key-file", "<json>", "读取 GUI 导出的 Key/Iv/Format JSON", "Read a GUI-exported Key/Iv/Format JSON file"),
                KeyLength with { DescriptionZh = "密钥长度：128|192|256，默认 128", DescriptionEn = "Key size: 128|192|256 (default: 128)" },
                Force,
                new("-q, --quiet", null, "不输出进度", "Suppress progress output")],
            ["fancy-tool file decrypt -i report.pdf.enc --key-type hex -k <hex-key> --iv <hex-iv>"]),
        new("file keygen", "生成 AES 文件加密密钥与 IV（可导出为 GUI 兼容 JSON）", "Generate an AES file key and IV (GUI-compatible JSON export)",
            "file keygen [--key-length <128|192|256>] [--key-type <text|base64|hex>] [-o <key-iv.json>] [--force] [--json]",
            [KeyType,
                KeyLength with { DescriptionZh = "密钥长度：128|192|256，默认 128", DescriptionEn = "Key size: 128|192|256 (default: 128)" },
                new("-o, --out", "<key-iv.json>", "写入 GUI 兼容的 Key/Iv/Format JSON", "Write a GUI-compatible Key/Iv/Format JSON file"),
                Force, new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool file keygen --key-type hex --out key-iv.json"]),
        new("file compare", "比较两个文件夹（GUI 文件夹比较页同等行为）", "Compare two folders (same as the GUI folder-compare page)",
            "file compare -s <source-folder> -d <target-folder> [--hash] [--music-title] [--json] [--csv <file>]",
            [new("-s, --source", "<dir>", "源文件夹", "Source folder"),
                new("-d, --target", "<dir>", "目标文件夹", "Target folder"),
                new("--hash", null, "使用 SHA-256 内容哈希比较同名文件", "Compare same-name files by SHA-256 content hash"),
                new("--music-title", null, "按音乐标题/艺术家匹配文件", "Match audio files by title/artist"),
                new("--json", null, "以 JSON 输出", "Emit JSON"),
                new("--csv", "<file>", "将结果写入 CSV", "Write the result to a CSV file")],
            ["fancy-tool file compare -s D:\\music -d E:\\backup --hash --csv diff.csv"]),

        // ── 图像 ─────────────────────────────────────────────────────────
        new("image base64", "将图片转换为 Base64", "Convert an image to Base64",
            "image base64 -i <image> [-o <file>]",
            [Input, Out],
            ["fancy-tool image base64 -i photo.png"]),
        new("image from-base64", "将 Base64 解码为图片文件", "Decode Base64 into an image file",
            "image from-base64 (-t <base64> | -i <file> | --stdin) -o <image.png>",
            [Text, Input, Stdin, Out],
            ["fancy-tool image from-base64 -i image.b64 -o image.png"]),
        new("image convert", "转换图片格式、缩放或超分辨率（GUI 图像转换页同等行为）", "Convert, resize, or super-resolve an image (same as the GUI image page)",
            "image convert -i <image> [-i <image> ...] (-o <image> | --out-dir <dir>) [--format <jpg|png|gif|bmp|webp|heic|ico>] [--max-dimension <px>] [--scale-percent <n>] [--filter <type>] [--ico-size <px>] [--sr-model <model>] [--sr-scale <2|4>] [--sr-use-npu] [--force]",
            [Input, Out, Force,
                new("--out-dir", "<dir>", "批量输出目录；可重复 -i", "Batch output directory; -i can be repeated"),
                new("--format", "<jpg|png|gif|bmp|webp|heic|ico>", "输出格式；缺省按 -o 扩展名", "Output format; inferred from -o when omitted"),
                new("--ico-size", "<px>", "ICO 尺寸，默认 256", "ICO size in pixels (default: 256)"),
                new("--max-dimension", "<px>", "长边最大像素", "Maximum long-edge pixels"),
                new("--scale-percent", "<n>", "按百分比缩放", "Scale by percentage"),
                new("--filter", "<type>", "缩放滤镜名称", "Resize filter name"),
                new("--sr-model", "<model>", "超分模型：realesrgan-x4plus|realesrgan-x4plus-anime|realesr-general-x4v3|clearreality", "Super-resolution model"),
                new("--sr-scale", "<2|4>", "超分倍数，默认 4", "Super-resolution scale (default: 4)"),
                new("--sr-use-npu", null, "优先使用 NPU", "Prefer the NPU")],
            ["fancy-tool image convert -i input.jpg -o output.webp --max-dimension 1920", "fancy-tool image convert -i low.png -o high.png --sr-model realesrgan-x4plus --sr-scale 4"]),

        // ── 视频 ─────────────────────────────────────────────────────────
        new("video probe", "读取视频/音频流信息", "Read video and audio stream metadata",
            "video probe -i <file> [--json]",
            [Input, new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool video probe -i movie.mkv --json"]),
        new("video preview", "导出指定时间点的一帧 PNG", "Export a PNG frame at a given position",
            "video preview -i <file> --at <seconds> -o <frame.png>",
            [Input, Out, new("--at", "<seconds>", "源时间轴位置（秒）", "Position on the source timeline (seconds)")],
            ["fancy-tool video preview -i movie.mp4 --at 12.5 -o frame.png"]),
        new("video transcode", "转码单个视频（GUI 视频转码页同等行为）", "Transcode one video (same as the GUI video page)",
            "video transcode -i <file> [-i <file> ...] (-o <file> | --out-dir <dir>) [encoding/filter/AI options]",
            [Out,
                new("--out-dir", "<dir>", "批量输出目录；可重复 -i", "Batch output directory; -i can be repeated"),
                .. VideoOptions()],
            ["fancy-tool video transcode -i input.mov -o output.mp4 --video-codec h264 --crf 20",
                "fancy-tool video transcode -i input.mp4 -o output.mp4 --sr --sr-model anime4k-cnn-s --sr-scale 2"]),
        new("video concat", "按片段顺序拼接转码（GUI 拼接模式同等行为）", "Concatenate and transcode segments (same as the GUI concat mode)",
            "video concat -o <file> --segment <file[;loops[;start;end]]> [--segment ...] [encoding/filter/AI options]",
            [Out,
                new("--segment", "<file[;loops[;start;end]]>", "输入片段；可重复，loops 默认 1", "Input segment; repeatable; loops defaults to 1"),
                new("--loop-count", "<n>", "配合 --segment 指定循环次数", "Loop count for the previous --segment"),
                new("--trim-start", "<seconds>", "配合 --segment 指定起始裁剪", "Trim start for the previous --segment"),
                new("--trim-end", "<seconds>", "配合 --segment 指定结束裁剪", "Trim end for the previous --segment"),
                .. VideoOptions(includeInput: false)],
            ["fancy-tool video concat -o out.mp4 --segment intro.mp4 --segment main.mp4;2 --segment end.mp4;1;2.5;8"]),

        // ── 音频 ─────────────────────────────────────────────────────────
        new("audio probe", "读取音频信息", "Read audio metadata",
            "audio probe -i <file> [--json]",
            [Input, new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool audio probe -i song.flac"]),
        new("audio convert", "转换音频格式（GUI 音频转换页同等行为）", "Convert audio (same as the GUI audio page)",
            "audio convert -i <file> [-i <file> ...] (-o <file> | --out-dir <dir>) [--format <mp3|m4a|flac|alac|wav|aiff|ogg|opus|wma|ac3|mp2>] [options]",
            [Input, Out, Force,
                new("--out-dir", "<dir>", "输出目录；可重复 -i", "Output directory; -i can be repeated"),
                new("--format", "<format>", "输出格式，默认 mp3", "Output format (default: mp3)"),
                new("--bitrate", "<kbps>", "有损码率，默认 320", "Lossy bitrate in kbps (default: 320)"),
                new("--bit-depth", "<auto|s16|s24|s32|f32>", "无损 PCM 位深", "Lossless PCM bit depth"),
                new("--sample-rate", "<hz>", "目标采样率；0=跟随源", "Target sample rate; 0 = source"),
                new("--enhance", null, "启用频谱增强", "Enable spectral enhancement"),
                new("--upsample", "<1|2|4>", "有损升频倍率，默认 2", "Upsampling multiplier (default: 2)"),
                new("--upsampling-backend", "<id>", "升频后端，默认 managed-dsp", "Upsampling backend (default: managed-dsp)"),
                new("--gain-db", "<db>", "增益 -30..30 dB", "Gain in dB (-30..30)"),
                new("--no-metadata", null, "不写入源标签/封面", "Do not copy source tags/cover"),
                new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool audio convert -i song.flac --out-dir out --format m4a --bitrate 256"]),

        // ── 串口 ─────────────────────────────────────────────────────────
        new("serial list", "列出本机串口", "List local serial ports",
            "serial list [--json]",
            [new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool serial list"]),
        new("serial send", "打开串口发送数据，可选同时接收", "Open a serial port and send data, optionally receiving",
            "serial send --port <COM3> [--baud <n>] [--data <text>] [--hex] [--encoding <auto|utf8|ascii|gb2312>] [--parity <none|odd|even|mark|space>] [--data-bits <5..8>] [--stop-bits <one|two>] [--duration <seconds>] [--listen] [--frame-break-ms <ms>]",
            [.. SerialOptions(),
                new("--data", "<text>", "要发送的数据；省略则只监听", "Data to send; omit to listen only"),
                new("--hex", null, "数据按十六进制解析/显示", "Interpret and display data as hexadecimal"),
                new("--duration", "<seconds>", "监听时长；0=直到 Ctrl+C", "Listen duration; 0 = until Ctrl+C"),
                new("--listen", null, "发送后继续接收", "Keep receiving after sending")],
            ["fancy-tool serial send --port COM3 --baud 115200 --data \"AT+RST\" --duration 5"]),
        new("serial listen", "监听串口数据", "Listen for serial data",
            "serial listen --port <COM3> [--baud <n>] [--hex] [--encoding <mode>] [--parity <mode>] [--data-bits <n>] [--stop-bits <mode>] [--duration <seconds>] [--frame-break-ms <ms>] [--json]",
            [.. SerialOptions(),
                new("--hex", null, "以十六进制显示", "Display as hexadecimal"),
                new("--duration", "<seconds>", "监听时长；0=直到 Ctrl+C", "Listen duration; 0 = until Ctrl+C"),
                new("--json", null, "按行输出 JSON 事件", "Emit one JSON event per line")],
            ["fancy-tool serial listen --port COM3 --baud 9600 --duration 10 --hex"]),

        // ── TCP ──────────────────────────────────────────────────────────
        new("tcp server", "启动 TCP 服务器并收发数据", "Start a TCP server and exchange data",
            "tcp server --port <n> [--bind <address>] [--send <text>] [--hex] [--duration <seconds>] [--json]",
            [new("--port", "<n>", "监听端口", "Listen port"),
                new("--bind", "<address>", "绑定地址，默认 0.0.0.0", "Bind address (default: 0.0.0.0)"),
                new("--send", "<text>", "客户端连接后发送的数据", "Data sent after a client connects"),
                new("--hex", null, "按十六进制解析/显示", "Interpret and display as hexadecimal"),
                new("--duration", "<seconds>", "运行时长；0=直到 Ctrl+C", "Run duration; 0 = until Ctrl+C"),
                new("--json", null, "按行输出 JSON 事件", "Emit one JSON event per line")],
            ["fancy-tool tcp server --port 9000 --send \"hello\" --duration 10"]),
        new("tcp client", "连接 TCP 服务器并收发数据", "Connect to a TCP server and exchange data",
            "tcp client --host <host> --port <n> [--send <text>] [--hex] [--duration <seconds>] [--json]",
            [new("--host", "<host>", "服务器地址", "Server address"),
                new("--port", "<n>", "服务器端口", "Server port"),
                new("--send", "<text>", "连接后发送的数据", "Data sent after connecting"),
                new("--hex", null, "按十六进制解析/显示", "Interpret and display as hexadecimal"),
                new("--duration", "<seconds>", "运行时长；0=直到 Ctrl+C", "Run duration; 0 = until Ctrl+C"),
                new("--json", null, "按行输出 JSON 事件", "Emit one JSON event per line")],
            ["fancy-tool tcp client --host 127.0.0.1 --port 9000 --send \"ping\" --duration 5"]),

        // ── UDP ──────────────────────────────────────────────────────────
        new("udp bind", "绑定 UDP 端口并接收数据", "Bind a UDP port and receive data",
            "udp bind --port <n> [--bind <address>] [--multicast <address>] [--multicast-port <n>] [--hex] [--duration <seconds>] [--json]",
            [new("--port", "<n>", "本地端口", "Local port"),
                new("--bind", "<address>", "绑定地址，默认 0.0.0.0", "Bind address (default: 0.0.0.0)"),
                new("--multicast", "<address>", "加入组播组", "Join a multicast group"),
                new("--multicast-port", "<n>", "组播端口", "Multicast port"),
                new("--hex", null, "以十六进制显示", "Display as hexadecimal"),
                new("--duration", "<seconds>", "运行时长；0=直到 Ctrl+C", "Run duration; 0 = until Ctrl+C"),
                new("--json", null, "按行输出 JSON 事件", "Emit one JSON event per line")],
            ["fancy-tool udp bind --port 9001 --duration 10"]),
        new("udp send", "向目标发送 UDP 数据", "Send UDP data to a target",
            "udp send --host <host> --port <n> --data <text> [--hex] [--bind-port <n>]",
            [new("--host", "<host>", "目标地址", "Target address"),
                new("--port", "<n>", "目标端口", "Target port"),
                new("--data", "<text>", "发送内容", "Payload"),
                new("--hex", null, "内容按十六进制解析", "Interpret the payload as hexadecimal"),
                new("--bind-port", "<n>", "本地绑定端口，默认自动", "Local bind port (default: automatic)")],
            ["fancy-tool udp send --host 255.255.255.255 --port 9001 --data \"discover\""]),

        // ── 链路测试 ─────────────────────────────────────────────────────
        new("linktest tcp-server", "TCP 带宽测试接收端", "TCP bandwidth-test receiver",
            "linktest tcp-server --port <n> [--bind <address>] [--json]",
            [new("--port", "<n>", "监听端口", "Listen port"),
                new("--bind", "<address>", "绑定地址，默认 0.0.0.0", "Bind address (default: 0.0.0.0)"),
                new("--json", null, "以 JSON 输出最终汇总", "Emit the final summary as JSON")],
            ["fancy-tool linktest tcp-server --port 9100"]),
        new("linktest tcp-client", "TCP 带宽测试发送端", "TCP bandwidth-test sender",
            "linktest tcp-client --host <host> --port <n> [--duration <seconds>] [--chunk-size <bytes>] [--rate <bytes/s>] [--json]",
            LinkClientOptions(),
            ["fancy-tool linktest tcp-client --host 192.168.1.10 --port 9100 --duration 10"]),
        new("linktest udp-server", "UDP 带宽/丢包测试接收端", "UDP bandwidth/loss-test receiver",
            "linktest udp-server --port <n> [--bind <address>] [--json]",
            [new("--port", "<n>", "监听端口", "Listen port"),
                new("--bind", "<address>", "绑定地址，默认 0.0.0.0", "Bind address (default: 0.0.0.0)"),
                new("--json", null, "以 JSON 输出最终汇总", "Emit the final summary as JSON")],
            ["fancy-tool linktest udp-server --port 9101"]),
        new("linktest udp-client", "UDP 带宽/丢包测试发送端", "UDP bandwidth/loss-test sender",
            "linktest udp-client --host <host> --port <n> [--duration <seconds>] [--chunk-size <bytes>] [--rate <bytes/s>] [--json]",
            LinkClientOptions(),
            ["fancy-tool linktest udp-client --host 192.168.1.10 --port 9101 --duration 10 --rate 10485760"]),

        // ── 模型 ─────────────────────────────────────────────────────────
        new("models list", "列出图像/视频/歌词模型及安装状态", "List image, video, and lyrics models with install state",
            "models list [--json]",
            [new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool models list"]),
        new("models install", "下载并校验模型", "Download and verify models",
            "models install [--image <all|realesrgan-x4plus|...>] [--video-sr <all|anime4k-cnn-s|...>] [--interpolation <all|rife425-lite|rife425>] [--lyrics] [--dir <dir>]",
            [new("--image", "<name|all>", "图像超分模型", "Image super-resolution model"),
                new("--video-sr", "<name|all>", "视频超分模型", "Video super-resolution model"),
                new("--interpolation", "<name|all>", "视频插帧模型", "Video interpolation model"),
                new("--lyrics", null, "歌词识别模型（普通精度）", "Lyrics recognition models (standard precision)"),
                new("--lyrics-high", null, "歌词识别模型（高精度）", "Lyrics recognition models (high precision)"),
                new("--dir", "<dir>", "模型根目录，默认应用数据目录", "Model root directory (default: app data)")],
            ["fancy-tool models install --image all --lyrics"]),
        new("models dir", "显示或切换模型目录", "Show or change the model directory",
            "models dir [--set <dir>] [--reset] [--json]",
            [new("--set", "<dir>", "切换模型目录（下次启动生效）", "Change the model directory (effective next launch)"),
                new("--reset", null, "恢复默认目录", "Reset to the default directory"),
                new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool models dir", "fancy-tool models dir --set D:\\models"]),

        // ── 歌词 ─────────────────────────────────────────────────────────
        new("lyrics extract", "从音频提取歌词（GUI 歌词页同等行为）", "Extract lyrics from audio (same as the GUI lyrics page)",
            "lyrics extract -i <audio> [-i <audio> ...] -o <out-dir> [--language <name>] [--performance <automatic|resource-saving|high-precision>] [--no-gpu] [--formats <lrc,srt,...>] [--chunk-seconds <n>] [--max-line-chars <n>] [--offset-ms <n>] [--model-dir <dir>] [--no-prefer-existing]",
            [Input, Out,
                new("--language", "<name>", "语言，默认 Auto", "Language (default: Auto)"),
                new("--performance", "<mode>", "性能模式，默认 automatic", "Performance mode (default: automatic)"),
                new("--no-gpu", null, "禁用 GPU 推理", "Disable GPU inference"),
                new("--formats", "<list>", "输出格式，默认 lrc,srt", "Output formats (default: lrc,srt)"),
                new("--chunk-seconds", "<n>", "分块秒数，默认 30", "Chunk length (default: 30)"),
                new("--max-line-chars", "<n>", "单行最大字符数，默认 42", "Maximum characters per line (default: 42)"),
                new("--offset-ms", "<n>", "整体时间偏移（毫秒）", "Global timing offset (ms)"),
                new("--model-dir", "<dir>", "歌词模型目录", "Lyrics model directory"),
                new("--no-prefer-existing", null, "忽略同名歌词文件，强制重新识别", "Ignore existing lyrics and re-recognize"),
                new("--json", null, "以 JSON 输出结果", "Emit the result as JSON")],
            ["fancy-tool lyrics extract -i song.mp3 -o .\\lyrics --formats lrc,srt"]),
        new("lyrics export", "将歌词工程导出为其他格式", "Export a lyrics project to other formats",
            "lyrics export -i <lyrics.json|.lrc|...> -o <base-path> --formats <lrc,srt,...> [--source <audio>] [--duration-ms <n>] [--offset-ms <n>]",
            [Input, Out,
                new("--formats", "<list>", "输出格式，默认 lrc,srt", "Output formats (default: lrc,srt)"),
                new("--source", "<audio>", "源音频路径（导入非 JSON 时使用）", "Source audio path (for non-JSON input)"),
                new("--duration-ms", "<n>", "源时长（毫秒）", "Source duration (ms)"),
                new("--offset-ms", "<n>", "整体时间偏移（毫秒）", "Global timing offset (ms)")],
            ["fancy-tool lyrics export -i song.json -o song --formats lrc,srt,vtt"]),

        // ── FFmpeg ───────────────────────────────────────────────────────
        new("ffmpeg info", "显示进程内 FFmpeg 版本与编码器", "Show the in-process FFmpeg version and encoders",
            "ffmpeg info [--json]",
            [new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool ffmpeg info"]),

        // ── 技能安装 ─────────────────────────────────────────────────────
        new("skill install", "将 CLI 用法写入各主流 agent 的本机技能目录", "Install CLI usage as a local skill for mainstream AI agents",
            "skill install [--agent <all|claude,cursor,gemini,codex,copilot,qoder>] [--agents-md] [--lang <zh|en>] [--force] [--json]",
            [new("--agent", "<all|列表>", "目标 agent（逗号分隔），默认全部六家", "Target agents (comma-separated); default: all six"),
                new("--agents-md", null, "附加 Qoder 用户级 AGENTS.md 受管块", "Also add the managed block to the Qoder user-level AGENTS.md"),
                new("--lang", "<zh|en>", "生成文档语言，默认跟随系统", "Documentation language (default: system)"),
                Force, new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool skill install", "fancy-tool skill install --agent claude,cursor"]),
        new("skill print", "输出技能文档内容（不写文件）", "Print the skill document without writing files",
            "skill print [--lang <zh|en>]",
            [new("--lang", "<zh|en>", "文档语言，默认跟随系统", "Documentation language (default: system)")],
            ["fancy-tool skill print --lang zh"]),
        new("skill uninstall", "移除已安装的技能与 AGENTS.md 受管片段", "Remove the installed skills and the managed AGENTS.md block",
            "skill uninstall [--agent <all|claude,cursor,gemini,codex,copilot,qoder>] [--agents-md] [--json]",
            [new("--agent", "<all|列表>", "目标 agent（逗号分隔），默认全部六家", "Target agents (comma-separated); default: all six"),
                new("--agents-md", null, "同时移除 Qoder AGENTS.md 受管块", "Also remove the managed block from the Qoder AGENTS.md"),
                new("--json", null, "以 JSON 输出", "Emit JSON")],
            ["fancy-tool skill uninstall", "fancy-tool skill uninstall --agents-md"]),
    ];

    private static CliCommandSpec[] Symmetric(string group, string label, string labelEn, string[] paddings, string sizes, string defaultSize)
    {
        string paddingText = string.Join("|", paddings);
        return
        [
            new($"{group} keygen", $"生成 {label} 密钥与 IV", $"Generate a {labelEn} key and IV",
                $"{group} keygen [--key-length <{sizes}>] [--key-type <text|base64|hex>] [--json]",
                [KeyLength with { DescriptionZh = $"密钥长度：{sizes}，默认 {defaultSize}", DescriptionEn = $"Key size: {sizes} (default: {defaultSize})" },
                    KeyType, new("--json", null, "以 JSON 输出", "Emit JSON")],
                [$"{ToolName} {group} keygen --key-length {defaultSize} --key-type hex"]),
            new($"{group} encrypt", $"使用 {label} 加密文本", $"Encrypt text with {labelEn}",
                $"{group} encrypt (-t <text> | -i <file> | --stdin) [-k <key>] [--iv <iv>] [--key-type <text|base64|hex>] [--key-length <{sizes}>] [--padding <{paddingText}>] [--mode <ECB|CBC>] [--output-format <base64|hex>] [-o <file>]",
                [Text, Input, Stdin, Key, Iv, KeyType,
                    KeyLength with { DescriptionZh = $"密钥长度：{sizes}，默认 {defaultSize}", DescriptionEn = $"Key size: {sizes} (default: {defaultSize})" },
                    Padding with { DescriptionZh = $"填充模式：{paddingText}", DescriptionEn = $"Padding mode: {paddingText}" },
                    CipherMode, OutputFormat, Out],
                [$"{ToolName} {group} encrypt -t \"hello\" -k <key> --mode CBC --output-format base64"]),
            new($"{group} decrypt", $"使用 {label} 解密文本", $"Decrypt text with {labelEn}",
                $"{group} decrypt (-t <ciphertext> | -i <file> | --stdin) [-k <key>] [--iv <iv>] [--key-type <text|base64|hex>] [--key-length <{sizes}>] [--padding <{paddingText}>] [--mode <ECB|CBC>] [--output-format <base64|hex>] [-o <file>]",
                [Text, Input, Stdin, Key, Iv, KeyType,
                    KeyLength with { DescriptionZh = $"密钥长度：{sizes}，默认 {defaultSize}", DescriptionEn = $"Key size: {sizes} (default: {defaultSize})" },
                    Padding with { DescriptionZh = $"填充模式：{paddingText}", DescriptionEn = $"Padding mode: {paddingText}" },
                    CipherMode, OutputFormat, Out],
                [$"{ToolName} {group} decrypt -t <ciphertext> -k <key> --mode CBC"]),
        ];
    }

    private static CliCommandSpec Hash(string name, string label, string labelEn)
    {
        var options = new List<CliOptionSpec> { Text, Input, Stdin, Lower };
        if (name == "md5")
            options.Add(new("--length", "<16|32>", "输出长度，默认 32", "Output length (default: 32)"));
        options.Add(Out);
        return new(
            $"hash {name}", $"计算{label} 哈希（文本或文件）", $"Compute a {labelEn} hash (text or file)",
            $"hash {name} (-t <text> | -i <file> | --stdin) [--lower]{(name == "md5" ? " [--length <16|32>]" : string.Empty)} [-o <file>]",
            [.. options],
            [$"{ToolName} hash {name} -t \"hello\"", $"{ToolName} hash {name} -i large.bin"]);
    }

    private static CliOptionSpec[] SerialOptions() =>
    [
        new("--port", "<name>", "串口名称，例如 COM3", "Port name, e.g. COM3"),
        new("--baud", "<n>", "波特率，默认 115200", "Baud rate (default: 115200)"),
        new("--encoding", "<auto|utf8|ascii|gb2312>", "文本编码，默认 utf8", "Text encoding (default: utf8)"),
        new("--parity", "<none|odd|even|mark|space>", "校验位，默认 none", "Parity (default: none)"),
        new("--data-bits", "<5|6|7|8>", "数据位，默认 8", "Data bits (default: 8)"),
        new("--stop-bits", "<one|two|onepointfive>", "停止位，默认 one", "Stop bits (default: one)"),
        new("--frame-break-ms", "<ms>", "接收分帧间隔（毫秒）", "Receive frame-break interval (ms)"),
    ];

    private static CliOptionSpec[] LinkClientOptions() =>
    [
        new("--host", "<host>", "目标地址", "Target address"),
        new("--port", "<n>", "目标端口", "Target port"),
        new("--duration", "<seconds>", "测试时长，默认 10", "Test duration (default: 10)"),
        new("--chunk-size", "<bytes>", "TCP 块大小或 UDP 包大小，默认 65536", "TCP block or UDP packet size (default: 65536)"),
        new("--rate", "<bytes/s>", "目标速率；0=不限速", "Target rate; 0 = unlimited"),
        new("--json", null, "以 JSON 输出最终汇总", "Emit the final summary as JSON"),
    ];

    private static CliOptionSpec[] VideoOptions(bool includeInput = true)
    {
        var options = new List<CliOptionSpec>();
        if (includeInput) options.Add(Input);
        options.Add(Force);
        options.AddRange(new CliOptionSpec[]
        {
        new("--container", "<mp4|mkv|mov|avi|gif>", "容器，默认按 -o 扩展名", "Container (default: inferred from -o)"),
        new("--video-codec", "<h264|h265|av1-aom|av1-svt|gif|vp8|vp9|mpeg4|copy>", "视频编码，默认 h264", "Video codec (default: h264)"),
        new("--audio-codec", "<aac|mp3|opus|vorbis|flac|ac3|none|copy>", "音频编码，默认 aac", "Audio codec (default: aac)"),
        new("--rate-control", "<crf|bitrate>", "码率控制，默认 crf", "Rate control (default: crf)"),
        new("--crf", "<0-63>", "CRF，默认 23", "CRF (default: 23)"),
        new("--video-bitrate", "<kbps>", "视频码率，默认 2500", "Video bitrate (default: 2500)"),
        new("--two-pass", null, "启用两遍编码", "Enable two-pass encoding"),
        new("--audio-bitrate", "<kbps>", "音频码率，默认 128", "Audio bitrate (default: 128)"),
        new("--preset", "<ultrafast..placebo>", "编码预设，默认 medium", "Encoder preset (default: medium)"),
        new("--hardware", "<software|nvidia|intel|amd>", "硬件编码后端，默认 software", "Hardware encoder (default: software)"),
        new("--profile", "<name>", "视频 profile", "Video profile"),
        new("--level", "<name>", "视频 level", "Video level"),
        new("--audio-gain", "<db>", "音频增益 -30..30 dB", "Audio gain (-30..30 dB)"),
        new("--scale-mode", "<none|fit|exact|width|height>", "缩放模式", "Scale mode"),
        new("--scale-width", "<px>", "缩放宽度", "Scale width"),
        new("--scale-height", "<px>", "缩放高度", "Scale height"),
        new("--no-keep-aspect", null, "允许非等比拉伸", "Allow non-uniform stretching"),
        new("--crop", "<top,bottom,left,right>", "裁剪像素", "Crop pixels"),
        new("--fps-mode", "<source|fixed|peak>", "帧率模式", "Frame-rate mode"),
        new("--fps", "<rate>", "目标帧率", "Target frame rate"),
        new("--deinterlace", "<none|yadif|bwdif>", "去隔行", "Deinterlace"),
        new("--denoise", "<none|light|medium|strong>", "降噪强度", "Denoise strength"),
        new("--gif-width", "<px>", "GIF 宽度", "GIF width"),
        new("--gif-fps", "<rate>", "GIF 帧率", "GIF frame rate"),
        new("--gif-colors", "<2-256>", "GIF 调色板颜色数", "GIF palette size"),
        new("--gif-dither", "<none|bayer|floyd|sierpinski>", "GIF 抖动", "GIF dithering"),
        new("--gif-loop", "<n>", "GIF 循环次数，0=无限", "GIF loop count (0 = infinite)"),
        new("--gif-stats", "<diff|single>", "GIF 调色板统计模式", "GIF palette statistics mode"),
        new("--trim-start", "<seconds>", "源起始裁剪", "Trim start (seconds)"),
        new("--trim-end", "<seconds>", "源结束裁剪", "Trim end (seconds)"),
        new("--hdr", "<same|hdr|sdr>", "HDR 输出策略", "HDR output policy"),
        new("--sr", null, "启用视频超分辨率", "Enable video super-resolution"),
        new("--sr-model", "<anime4k-cnn-s|anime4k-cnn-m|fsrcnnx-s|fsrcnnx-m|ravu-lite-r2|ravu-lite-r3>", "视频超分模型", "Video SR model"),
        new("--sr-scale", "<2|4>", "超分倍数，默认 2", "SR scale (default: 2)"),
        new("--sr-backend", "<auto|npu|gpu|cpu|directml|vendor>", "超分后端", "SR backend"),
        new("--sr-fp16", null, "使用 FP16 图", "Use the FP16 graph"),
        new("--interpolate", "<none|rife|motion|amd|nvidia>", "插帧模式", "Interpolation mode"),
        new("--interpolation-model", "<rife425-lite|rife425>", "RIFE 模型", "RIFE model"),
        new("--interpolation-multiplier", "<2|4|6>", "帧率倍率，默认 2", "Frame-rate multiplier (default: 2)"),
        new("--interpolation-backend", "<auto|npu|gpu|cpu>", "插帧后端", "Interpolation backend"),
        new("--interpolation-cpu-fallback", null, "允许硬件失败后回退 CPU", "Allow CPU fallback after hardware failure"),
        new("--no-scene-detection", null, "关闭场景切换检测", "Disable scene-change detection"),
        new("--processing-order", "<auto|interpolate-first|upscale-first>", "AI 处理顺序", "AI processing order"),
        });
        return [.. options];
    }

    public static string LocalizeOption(CliOptionSpec option, bool zh) => zh ? option.DescriptionZh : option.DescriptionEn;

    public static string LocalizeSummary(CliCommandSpec command, bool zh) => zh ? command.SummaryZh : command.SummaryEn;

    /// <summary>渲染单个命令的完整帮助。</summary>
    public static string RenderCommandHelp(string path, bool zh)
    {
        CliCommandSpec? command = Find(path);
        if (command is null) return zh ? $"未知命令：{path}" : $"Unknown command: {path}";
        var sb = new StringBuilder();
        sb.AppendLine($"{ToolName} {command.Path} - {LocalizeSummary(command, zh)}");
        sb.AppendLine();
        sb.AppendLine(zh ? "用法:" : "Usage:");
        sb.AppendLine($"  {ToolName} {command.Usage}");
        sb.AppendLine();
        sb.AppendLine(zh ? "选项:" : "Options:");
        foreach (CliOptionSpec option in command.Options)
        {
            string value = option.Value is null ? string.Empty : " " + option.Value;
            sb.AppendLine($"  {option.Name}{value}");
            sb.AppendLine($"      {LocalizeOption(option, zh)}");
        }
        sb.AppendLine();
        sb.AppendLine(zh ? "全局选项:" : "Global options:");
        foreach (CliOptionSpec option in GlobalOptions)
        {
            string value = option.Value is null ? string.Empty : " " + option.Value;
            sb.AppendLine($"  {option.Name}{value}");
            sb.AppendLine($"      {LocalizeOption(option, zh)}");
        }
        if (command.Examples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(zh ? "示例:" : "Examples:");
            foreach (string example in command.Examples) sb.AppendLine($"  {example}");
        }
        return sb.ToString();
    }

    /// <summary>渲染根命令或命令组列表。</summary>
    public static string RenderRootHelp(bool zh, string? group = null)
    {
        var sb = new StringBuilder();
        if (group is null)
        {
            sb.AppendLine($"{ToolName} - FancyTool " + (zh ? "命令行工具" : "command-line interface"));
            sb.AppendLine();
            sb.AppendLine(zh ? "用法:" : "Usage:");
            sb.AppendLine($"  {ToolName} <group> <command> [options]");
            sb.AppendLine($"  {ToolName} <command> -help");
            sb.AppendLine();
            sb.AppendLine(zh ? "命令组:" : "Groups:");
            foreach (string name in GroupOrder)
            {
                string summary = Groups.First(g => string.Equals(g.Key, name, StringComparison.OrdinalIgnoreCase))
                    .First().GroupSummary(zh);
                sb.AppendLine($"  {name,-12} {summary}");
            }
            sb.AppendLine();
            sb.AppendLine(zh ? "全局选项:" : "Global options:");
            foreach (CliOptionSpec option in GlobalOptions)
            {
                string value = option.Value is null ? string.Empty : " " + option.Value;
                sb.AppendLine($"  {option.Name}{value}");
                sb.AppendLine($"      {LocalizeOption(option, zh)}");
            }
            sb.AppendLine();
            sb.AppendLine(zh ? $"运行 '{ToolName} <group> -h' 查看命令组。" : $"Run '{ToolName} <group> -h' to list a group.");
        }
        else
        {
            IGrouping<string, CliCommandSpec>? commands = Groups.FirstOrDefault(g => string.Equals(g.Key, group, StringComparison.OrdinalIgnoreCase));
            if (commands is null) return zh ? $"未知命令组：{group}" : $"Unknown group: {group}";
            sb.AppendLine($"{ToolName} {commands.Key} - " + commands.First().GroupSummary(zh));
            sb.AppendLine();
            sb.AppendLine(zh ? "用法:" : "Usage:");
            sb.AppendLine($"  {ToolName} {commands.Key} <command> [options]");
            sb.AppendLine();
            sb.AppendLine(zh ? "命令:" : "Commands:");
            foreach (CliCommandSpec command in commands)
            {
                string sub = command.Path[(commands.Key.Length + 1)..];
                sb.AppendLine($"  {sub,-18} {LocalizeSummary(command, zh)}");
            }
            sb.AppendLine();
            sb.AppendLine(zh ? $"运行 '{ToolName} {commands.Key} <command> -h' 查看详细选项。" : $"Run '{ToolName} {commands.Key} <command> -h' for options.");
        }
        return sb.ToString();
    }

    private static string GroupSummary(this CliCommandSpec command, bool zh) => command.Group switch
    {
        "aes" => zh ? "AES 对称加密/解密/密钥生成" : "AES symmetric encryption/decryption/keygen",
        "des" => zh ? "DES 对称加密/解密/密钥生成" : "DES symmetric encryption/decryption/keygen",
        "sm4" => zh ? "SM4 对称加密/解密/密钥生成" : "SM4 symmetric encryption/decryption/keygen",
        "rsa" => zh ? "RSA 加解密与密钥生成" : "RSA encryption, decryption, and key generation",
        "sm2" => zh ? "SM2 加解密与密钥生成" : "SM2 encryption, decryption, and key generation",
        "hash" => zh ? "文本/文件哈希" : "Text and file hashing",
        "base64" => zh ? "Base64 编码/解码" : "Base64 encoding/decoding",
        "file" => zh ? "文件批量加密、解密与文件夹比较" : "File encryption, decryption, and folder comparison",
        "image" => zh ? "图片 Base64、格式转换与超分辨率" : "Image Base64, conversion, and super-resolution",
        "video" => zh ? "视频探测、预览、转码与拼接" : "Video probing, preview, transcode, and concat",
        "audio" => zh ? "音频探测与格式转换" : "Audio probing and conversion",
        "serial" => zh ? "串口收发" : "Serial port receive/send",
        "tcp" => zh ? "TCP 服务器/客户端调试" : "TCP server/client debugging",
        "udp" => zh ? "UDP 绑定/发送调试" : "UDP bind/send debugging",
        "linktest" => zh ? "TCP/UDP 带宽与丢包测试" : "TCP/UDP bandwidth and loss testing",
        "models" => zh ? "模型目录、下载与安装" : "Model directory, download, and installation",
        "lyrics" => zh ? "歌词识别与导出" : "Lyrics extraction and export",
        "ffmpeg" => zh ? "内置 FFmpeg 信息" : "Bundled FFmpeg information",
        "skill" => zh ? "将 CLI 用法安装到本机技能/AGENTS.md" : "Install CLI usage into a local skill/AGENTS.md",
        _ => command.Path,
    };

    /// <summary>生成面向 agent 的完整 SKILL.md 内容。</summary>
    public static string RenderSkillMarkdown(bool zh)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("name: fancy-tool");
        sb.AppendLine("description: " + (zh
            ? "使用 FancyTool 命令行完成加密解密、哈希、Base64、文件/图片/音视频转换、串口与网络调试、模型管理和歌词提取。"
            : "Use the FancyTool command line for encryption, hashing, Base64, file/image/audio/video conversion, serial and network debugging, model management, and lyrics extraction."));
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(zh ? "# FancyTool CLI" : "# FancyTool CLI");
        sb.AppendLine();
        sb.AppendLine(zh
            ? "`fancy-tool` 是本机 FancyTool GUI 的命令行版本，覆盖 GUI 的全部工具功能。每条命令都支持 `-help`。"
            : "`fancy-tool` is the command-line counterpart of the FancyTool GUI and covers every GUI tool. Every command supports `-help`.");
        sb.AppendLine();
        sb.AppendLine(zh ? "## 全局选项" : "## Global options");
        sb.AppendLine();
        foreach (CliOptionSpec option in GlobalOptions)
        {
            string value = option.Value is null ? string.Empty : " " + option.Value;
            sb.AppendLine($"- `{option.Name}{value}`: {LocalizeOption(option, zh)}");
        }
        sb.AppendLine();
        sb.AppendLine(zh ? "## 命令索引" : "## Command index");
        sb.AppendLine();
        foreach (IGrouping<string, CliCommandSpec> group in Groups.OrderBy(g => IndexOfGroup(g.Key)))
        {
            sb.AppendLine($"### {group.Key} - {group.First().GroupSummary(zh)}");
            sb.AppendLine();
            foreach (CliCommandSpec command in group)
            {
                sb.AppendLine($"#### `{ToolName} {command.Path}`");
                sb.AppendLine();
                sb.AppendLine(LocalizeSummary(command, zh));
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine($"{ToolName} {command.Usage}");
                sb.AppendLine("```");
                if (command.Options.Count > 0)
                {
                    sb.AppendLine();
                    foreach (CliOptionSpec option in command.Options)
                    {
                        string value = option.Value is null ? string.Empty : " " + option.Value;
                        sb.AppendLine($"- `{option.Name}{value}`: {LocalizeOption(option, zh)}");
                    }
                }
                if (command.Examples.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(zh ? "示例：" : "Example:");
                    sb.AppendLine();
                    sb.AppendLine("```bash");
                    foreach (string example in command.Examples) sb.AppendLine(example);
                    sb.AppendLine("```");
                }
                sb.AppendLine();
            }
        }
        sb.AppendLine(zh ? "## 提示" : "## Tips");
        sb.AppendLine();
        sb.AppendLine(zh
            ? "- 加 `--json` 获取机器可读输出；批量任务失败时返回非零退出码。\n- 加密/转码等长任务可用 Ctrl+C 取消。\n- 找不到命令时先运行 `fancy-tool -h` 或 `fancy-tool <group> -h`。"
            : "- Add `--json` for machine-readable output; batch failures return a non-zero exit code.\n- Long-running operations can be cancelled with Ctrl+C.\n- Run `fancy-tool -h` or `fancy-tool <group> -h` when unsure of a command.");
        return sb.ToString();
    }

    /// <summary>生成写入 AGENTS.md 的精简受管片段。</summary>
    public static string RenderAgentsBlock(bool zh)
    {
        var sb = new StringBuilder();
        sb.AppendLine(zh ? "## FancyTool CLI" : "## FancyTool CLI");
        sb.AppendLine();
        sb.AppendLine(zh
            ? "本机安装了 `fancy-tool` 命令行工具，覆盖 FancyTool GUI 的全部功能。使用前运行 `fancy-tool -h`，每条命令都支持 `-help`，加 `--json` 可获得机器可读输出。"
            : "The `fancy-tool` CLI is installed on this machine and covers every FancyTool GUI feature. Start with `fancy-tool -h`; every command supports `-help`, and `--json` produces machine-readable output.");
        sb.AppendLine();
        foreach (IGrouping<string, CliCommandSpec> group in Groups.OrderBy(g => IndexOfGroup(g.Key)))
        {
            string commands = string.Join(", ", group.Select(c => $"`{c.Path}`"));
            sb.AppendLine($"- {group.Key}: {commands}");
        }
        return sb.ToString();
    }

    private static int IndexOfGroup(string name)
    {
        for (int i = 0; i < GroupOrder.Count; i++)
        {
            if (string.Equals(GroupOrder[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return int.MaxValue;
    }
}