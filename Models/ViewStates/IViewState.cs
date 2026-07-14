namespace LittleFancyToolAva.Models.ViewStates;

public interface IViewState
{
    string ViewName { get; }
    object CaptureState();
    void RestoreState(object state);
}
