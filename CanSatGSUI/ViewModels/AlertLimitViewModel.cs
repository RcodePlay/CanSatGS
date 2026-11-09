using System.Collections.Generic;
using System.Globalization;
using CanSatGroundStation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanSatGSUI.ViewModels;

/// <summary>Stable keys for the alert limits. They are saved in settings.json, so don't rename them.</summary>
public static class LimitKeys
{
    public const string BaroAltitude = "BaroAltitude";
    public const string GpsAltitude = "GpsAltitude";
    public const string Temperature = "Temperature";
    public const string Pressure = "Pressure";
    public const string Humidity = "Humidity";
    public const string AccelMagnitude = "AccelMagnitude";
    public const string AngularVelocity = "AngularVelocity";
    public const string MagneticField = "MagneticField";
    public const string Rssi = "Rssi";
    public const string Snr = "Snr";
}

/// <summary>
/// One row of the "Alert Limits" table: the text the user typed, the limits currently in force
/// (applied with Save &amp; Apply Settings) and whether the latest packet broke them.
/// </summary>
public sealed partial class AlertLimitViewModel : ObservableObject
{
    public AlertLimitViewModel(string key, string name, string unit)
    {
        Key = key;
        Name = name;
        Unit = unit;
    }

    public string Key { get; }

    /// <summary>Label in the settings table, e.g. "Signal-to-Noise Ratio".</summary>
    public string Name { get; }

    public string Unit { get; }

    public string NameWithUnit => $"{Name} ({Unit})";

    /// <summary>What the user typed in the Min box. Empty means no limit.</summary>
    [ObservableProperty] public partial string MinText { get; set; } = string.Empty;

    /// <summary>What the user typed in the Max box. Empty means no limit.</summary>
    [ObservableProperty] public partial string MaxText { get; set; } = string.Empty;

    /// <summary>Lower limit in force. Null = no limit.</summary>
    public double? AppliedMin { get; private set; }

    /// <summary>Upper limit in force. Null = no limit.</summary>
    public double? AppliedMax { get; private set; }

    [ObservableProperty] public partial bool IsTripped { get; private set; }

    /// <summary>Short reason shown next to the value, e.g. "below 5 dB".</summary>
    [ObservableProperty] public partial string Reason { get; private set; } = string.Empty;

    /// <summary>Fills the text boxes and the limits in force from saved settings.</summary>
    public void Load(AlertLimit? limit)
    {
        AppliedMin = limit?.Min;
        AppliedMax = limit?.Max;
        MinText = Format(AppliedMin);
        MaxText = Format(AppliedMax);
        Check([]);
    }

    /// <summary>
    /// Empties the text boxes only. The limits in force stay until Save &amp; Apply Settings.
    /// </summary>
    public void ClearText()
    {
        MinText = string.Empty;
        MaxText = string.Empty;
    }

    /// <summary>
    /// Parses the text boxes. Returns false (and changes nothing) when a box holds something that isn't a number.
    /// </summary>
    public bool TryParse(out double? min, out double? max, out string error)
    {
        error = string.Empty;
        max = null;
        if (!TryParseBound(MinText, out min))
        {
            error = $"{Name}: minimum \"{MinText}\" is not a number.";
            return false;
        }

        if (!TryParseBound(MaxText, out max))
        {
            error = $"{Name}: maximum \"{MaxText}\" is not a number.";
            return false;
        }

        if (min is { } lo && max is { } hi && lo > hi)
        {
            error = $"{Name}: minimum is larger than maximum.";
            return false;
        }

        return true;
    }

    /// <summary>Puts parsed limits in force and returns them in the form settings.json stores.</summary>
    public AlertLimit? Apply(double? min, double? max)
    {
        AppliedMin = min;
        AppliedMax = max;
        MinText = Format(min);
        MaxText = Format(max);
        return min is null && max is null ? null : new AlertLimit { Min = min, Max = max };
    }

    /// <summary>
    /// Checks the latest reading(s) against the limits in force. Several values (X, Y, Z) trip the
    /// limit when any one of them is outside it. NaN values are ignored here (data health reports them).
    /// </summary>
    public bool Check(IEnumerable<double> values)
    {
        string reason = string.Empty;
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            if (AppliedMin is { } lo && v < lo)
            {
                reason = $"below {Format(lo)} {Unit}";
                break;
            }

            if (AppliedMax is { } hi && v > hi)
            {
                reason = $"above {Format(hi)} {Unit}";
                break;
            }
        }

        Reason = reason;
        IsTripped = reason.Length > 0;
        return IsTripped;
    }

    private static bool TryParseBound(string? text, out double? value)
    {
        value = null;
        var t = (text ?? string.Empty).Trim().Replace('−', '-').Replace(',', '.');
        if (t.Length == 0) return true;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v))
            return false;
        value = v;
        return true;
    }

    private static string Format(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
}
