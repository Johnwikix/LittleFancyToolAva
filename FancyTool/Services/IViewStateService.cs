using FancyToolAva.Models.ViewStates;

namespace FancyToolAva.Services;

public interface IViewStateService
{
    void Register(IViewState view);
    void Unregister(IViewState view);
    void SaveAll();
    void LoadAll();
}
