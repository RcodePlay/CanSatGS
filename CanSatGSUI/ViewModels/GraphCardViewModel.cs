using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanSatGSUI.ViewModels;

/// <summary>Stable keys for the ten graphs. They are saved in settings.json, so don't rename them.</summary>
public static class GraphKeys
{
    public const string Altitude = "Altitude";
    public const string Temperature = "Temperature";
    public const string Pressure = "Pressure";
    public const string Humidity = "Humidity";
    public const string Acceleration = "Acceleration";
    public const string AngularVelocity = "AngularVelocity";
    public const string MagneticField = "MagneticField";
    public const string Azimuth = "Azimuth";
    public const string Rssi = "Rssi";
    public const string Snr = "Snr";
}

/// <summary>
/// One graph (shown small on the dashboard and large on the Graphs tab).
/// Pinning happens here; <see cref="MainViewModel"/> saves the pins and decides what the Graphs tab shows.
/// </summary>
public sealed partial class GraphCardViewModel : ObservableObject
{
    private readonly Action<GraphCardViewModel> _pinChanged;

    public GraphCardViewModel(string key, string title, Action<GraphCardViewModel> pinChanged)
    {
        Key = key;
        Title = title;
        _pinChanged = pinChanged;
    }

    public string Key { get; }
    public string Title { get; }

    [ObservableProperty] public partial bool IsPinned { get; set; }

    /// <summary>True while one of the alert limits behind this graph is tripped.</summary>
    [ObservableProperty] public partial bool IsAlert { get; set; }

    /// <summary>Whether the Graphs tab currently shows this graph (All view, or Pinned view and pinned).</summary>
    [ObservableProperty] public partial bool IsShownInGraphsTab { get; set; } = true;

    public string PinToolTip => IsPinned ? $"Unpin {Title}" : $"Pin {Title} to the Graphs tab";

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    partial void OnIsPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(PinToolTip));
        _pinChanged(this);
    }
}
