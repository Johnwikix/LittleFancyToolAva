namespace FancyToolAva.Cli.Commands;

internal static class CliHandlers
{
    public static IReadOnlyDictionary<string, Func<CliContext, Task<int>>> Map { get; } = Build();

    private static Dictionary<string, Func<CliContext, Task<int>>> Build()
    {
        var map = new Dictionary<string, Func<CliContext, Task<int>>>(StringComparer.OrdinalIgnoreCase);
        CryptoCommands.Register(map);
        AsymmetricCommands.Register(map);
        FileCommands.Register(map);
        ImageCommands.Register(map);
        VideoCommands.Register(map);
        AudioCommands.Register(map);
        NetworkCommands.Register(map);
        ModelsCommands.Register(map);
        MiscCommands.Register(map);
        return map;
    }
}