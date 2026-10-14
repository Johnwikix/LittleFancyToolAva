using CommunityToolkit.Mvvm.ComponentModel;
using FancyToolAva.Services;

namespace FancyToolAva.Models;

/// <summary>A stable dropdown item whose text can change without clearing selection.</summary>
public sealed class LocalizedOption(string key) : ObservableObject
{
    public string Label { get; private set => SetProperty(ref field, value); } = LocalizationRegistry.Get(key);
    public void Refresh() => Label = LocalizationRegistry.Get(key);
    public override string ToString() => Label;
}
