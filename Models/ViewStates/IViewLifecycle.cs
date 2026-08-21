namespace FancyToolAva.Models.ViewStates;

public interface IViewLifecycle
{
    void OnNavigatedTo();
    void OnNavigatedFrom();
}
