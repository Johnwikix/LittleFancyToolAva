namespace LittleFancyToolAva.Models.ViewStates;

public interface IViewLifecycle
{
    void OnNavigatedTo();
    void OnNavigatedFrom();
}
