namespace FancyToolAva.Services;

/// <summary>Marshals collection updates to the host UI thread; the CLI uses the immediate implementation.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task InvokeAsync(Action action);
}

public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static ImmediateUiDispatcher Instance { get; } = new();

    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
