using LittleFancyToolAva.Models.ViewStates;

namespace LittleFancyToolAva.Services;

public interface IViewStateService
{
    void Register(IViewState view);
    void Unregister(IViewState view);
    void SaveAll();
    void LoadAll();
}
