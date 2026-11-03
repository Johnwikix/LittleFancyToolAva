using System.Reflection;
using System.Text;
using FancyToolAva.Cli;
using FancyToolAva.Cli.Commands;
using FancyToolAva.Services;
using Serilog;

namespace FancyToolAva.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        CliContext.ResolveLanguage(args, out bool zh);
        LocalizationRegistry.Provider = new JsonLocalizationProvider(
            new System.Globalization.CultureInfo(zh ? "zh-CN" : "en-US"));
        InitializeLogging();
        try
        {
            return await RunAsync(args, zh).ConfigureAwait(false);
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> RunAsync(string[] args, bool zh)
    {
        if (args.Length == 0)
        {
            Console.Out.Write(CommandCatalog.RenderRootHelp(zh));
            return 0;
        }

        if (args.Any(a => a is "--version" or "-v" or "-version"))
        {
            string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                             ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.1.0";
            Console.Out.WriteLine($"{CommandCatalog.ToolName} {version}");
            return 0;
        }

        (string? path, bool helpRequested, bool groupOnly) = LocateCommand(args, zh);
        bool explicitHelp = args.Any(a => a is "-h" or "--help" or "-help");

        if (path is null && !groupOnly)
        {
            Console.Out.Write(CommandCatalog.RenderRootHelp(zh));
            return 0;
        }

        if (helpRequested || explicitHelp)
        {
            Console.Out.Write(groupOnly
                ? CommandCatalog.RenderRootHelp(zh, path)
                : CommandCatalog.RenderCommandHelp(path!, zh));
            return 0;
        }

        CliCommandSpec? spec = CommandCatalog.Find(path!);
        if (spec is null)
        {
            Console.Error.WriteLine(zh ? $"未知命令：{path}" : $"Unknown command: {path}");
            Console.Error.Write(CommandCatalog.RenderRootHelp(zh));
            return 2;
        }

        IReadOnlyList<string> commandTokens = args.Where(a => a is not ("-h" or "--help" or "-help")).ToList();
        CliArgs parsed = CliArgs.Parse(RemoveCommandPath(commandTokens, path!), [.. spec.Options, .. CommandCatalog.GlobalOptions]);
        if (parsed.UnknownOptions.Count > 0)
        {
            Console.Error.WriteLine((zh ? "未知选项: " : "Unknown option(s): ") + string.Join(", ", parsed.UnknownOptions));
            Console.Error.Write(CommandCatalog.RenderCommandHelp(path!, zh));
            return 2;
        }

        bool json = parsed.Has("--json");
        bool quiet = parsed.Has("--quiet");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
                Console.Error.WriteLine(zh ? "正在取消…" : "Cancelling…");
            }
            else
            {
                e.Cancel = false;
            }
        };

        if (path is not "skill install" and not "skill print" and not "skill uninstall")
        {
            if (!await CliLicenseGate.EnsureAsync(zh).ConfigureAwait(false)) return 3;
        }

        using var context = new CliContext
        {
            Args = parsed,
            Json = json,
            Quiet = quiet,
            Zh = zh,
            CancellationToken = cts.Token,
        };

        try
        {
            if (!CliHandlers.Map.TryGetValue(path!, out Func<CliContext, Task<int>>? handler))
            {
                context.Error(zh ? $"命令尚未实现：{path}" : $"Command is not implemented: {path}");
                return 1;
            }
            return await handler(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            context.Info(zh ? "已取消。" : "Cancelled.");
            return 130;
        }
        catch (CliUsageException ex)
        {
            if (json) context.WriteJson(new { error = ex.Message });
            else context.Error(ex.Message);
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            string message = (zh ? "找不到文件: " : "File not found: ") + ex.FileName;
            if (json) context.WriteJson(new { error = message });
            else context.Error(message);
            return 1;
        }
        catch (Exception ex)
        {
            if (json) context.WriteJson(new { error = ex.Message });
            else context.Error(ex.Message);
            Log.Error(ex, "CLI command failed: {Command}", path);
            return 1;
        }
    }

    /// <summary>找到命令路径；支持全局选项出现在命令前，帮助伪命令 help [group] [command]。</summary>
    private static (string? Path, bool HelpRequested, bool GroupOnly) LocateCommand(string[] args, bool zh)
    {
        int index = 0;
        while (index < args.Length)
        {
            string token = args[index];
            if (token is "-lang" or "--lang")
            {
                index += 2;
                continue;
            }
            if (token.StartsWith('-') && token != "-") { index++; continue; }
            break;
        }

        if (index >= args.Length) return (null, false, false);
        string first = args[index];

        if (string.Equals(first, "help", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>();
            for (int i = index + 1; i < args.Length && parts.Count < 2; i++)
            {
                if (args[i].StartsWith('-')) continue;
                parts.Add(args[i]);
            }
            if (parts.Count == 0) return (null, true, false);
            string joined = string.Join(' ', parts);
            if (CommandCatalog.Find(joined) is not null) return (joined, true, false);
            if (CommandCatalog.IsGroup(parts[0])) return (parts[0], true, parts.Count == 1);
            return (joined, true, false);
        }

        if (!CommandCatalog.IsGroup(first))
            return (first, false, false);

        if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
            return (first + " " + args[index + 1], false, false);
        return (first, false, true);
    }

    private static string[] RemoveCommandPath(IReadOnlyList<string> tokens, string path)
    {
        string[] parts = path.Split(' ');
        var result = new List<string>(tokens.Count);
        int matched = 0;
        foreach (string token in tokens)
        {
            if (matched < parts.Length && string.Equals(token, parts[matched], StringComparison.OrdinalIgnoreCase))
            {
                matched++;
                continue;
            }
            result.Add(token);
        }
        return [.. result];
    }

    private static void InitializeLogging()
    {
        try
        {
            string directory = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.File(
                    Path.Combine(directory, "cli-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
        }
    }
}