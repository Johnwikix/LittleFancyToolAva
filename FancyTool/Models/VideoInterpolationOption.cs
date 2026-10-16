using CommunityToolkit.Mvvm.ComponentModel;
using FancyToolAva.Services;

namespace FancyToolAva.Models;

/// <summary>Represents a localized interpolation choice with runtime hardware availability.</summary>
/// <param name="key">The localization key for the choice label.</param>
/// <param name="enabled">Whether the choice can initially be selected.</param>
/// <param name="reasonKey">The localization key explaining its availability.</param>
public sealed class VideoInterpolationOption(string key, bool enabled = true, string? reasonKey = null) : ObservableObject
{
    private string? _reasonKey = reasonKey;
    private string _detail = string.Empty;

    /// <summary>Gets the localized option label.</summary>
    public string Label => LocalizationRegistry.Get(key);
    /// <summary>Gets whether the option can be selected on this device.</summary>
    public bool IsEnabled { get; private set => SetProperty(ref field, value); } = enabled;
    /// <summary>Gets the localized availability explanation and any driver diagnostic.</summary>
    public string Description => (_reasonKey == null ? string.Empty : LocalizationRegistry.Get(_reasonKey))
        + (_detail.Length == 0 ? string.Empty : " · " + _detail);

    internal void SetAvailability(bool available, string? reason = null, string detail = "")
    {
        IsEnabled = available;
        _reasonKey = reason;
        _detail = detail;
        OnPropertyChanged(nameof(Description));
    }

    /// <summary>Refreshes labels and availability explanations after a language change.</summary>
    public void Refresh() { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Description)); }

    /// <inheritdoc />
    public override string ToString() => Label;
}
