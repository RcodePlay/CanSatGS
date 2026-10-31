using System;
using System.Collections.Generic;
using CanSatGroundStation;

namespace CanSatGSUI.ViewModels;

/// <summary>
/// Time series of every measurement received since the app started (or since the last <see cref="Clear"/>).
/// Feeds the Graphs tab. X values (<see cref="Time"/>) are mission time in seconds (TimeSinceStart).
/// All lists always have the same length. Use only from the UI thread.
/// </summary>
public sealed class TelemetryHistory
{
    /// <summary>Safety cap so a station left running for many hours can't grow without limit.</summary>
    private const int DefaultMaxPoints = 100_000;

    private const double RadiansToDegrees = 180.0 / Math.PI;

    private readonly int _maxPoints;
    private readonly List<double>[] _all;

    public TelemetryHistory(int maxPoints = DefaultMaxPoints)
    {
        if (maxPoints < 1) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        _maxPoints = maxPoints;
        _all =
        [
            Time, GpsAltitude, BaroAltitude, Temperature, Pressure, Humidity,
            AccelX, AccelY, AccelZ, AccelMagnitude,
            OmegaX, OmegaY, OmegaZ,
            MagX, MagY, MagZ, Theta,
            Rssi, Snr
        ];
    }

    public List<double> Time { get; } = [];

    public List<double> GpsAltitude { get; } = [];
    public List<double> BaroAltitude { get; } = [];
    public List<double> Temperature { get; } = [];
    public List<double> Pressure { get; } = [];
    public List<double> Humidity { get; } = [];

    public List<double> AccelX { get; } = [];
    public List<double> AccelY { get; } = [];
    public List<double> AccelZ { get; } = [];
    public List<double> AccelMagnitude { get; } = [];

    /// <summary>Angular velocity in °/s (converted from rad/s, same as the dashboard).</summary>
    public List<double> OmegaX { get; } = [];
    public List<double> OmegaY { get; } = [];
    public List<double> OmegaZ { get; } = [];

    public List<double> MagX { get; } = [];
    public List<double> MagY { get; } = [];
    public List<double> MagZ { get; } = [];
    public List<double> Theta { get; } = [];

    public List<double> Rssi { get; } = [];
    public List<double> Snr { get; } = [];

    /// <summary>Number of stored points (same for every list).</summary>
    public int Count => Time.Count;

    /// <summary>
    /// Increases whenever existing points are removed (Clear or trimming), so consumers that copy
    /// data incrementally know they have to start over.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>Raised after points were added or removed.</summary>
    public event Action? Changed;

    public void Add(ProcessedPacket p)
    {
        Time.Add(p.TimeSinceStart);

        GpsAltitude.Add(p.GPSAltitude);
        BaroAltitude.Add(p.AltitudeBarometer);
        Temperature.Add(p.Temperature);
        Pressure.Add(p.PressureBarometer);
        Humidity.Add(p.Humidity);

        AccelX.Add(p.AccelerationX);
        AccelY.Add(p.AccelerationY);
        AccelZ.Add(p.AccelerationZ);
        AccelMagnitude.Add(Math.Sqrt(
            (double)p.AccelerationX * p.AccelerationX +
            (double)p.AccelerationY * p.AccelerationY +
            (double)p.AccelerationZ * p.AccelerationZ));

        OmegaX.Add(p.OmegaX * RadiansToDegrees);
        OmegaY.Add(p.OmegaY * RadiansToDegrees);
        OmegaZ.Add(p.OmegaZ * RadiansToDegrees);

        MagX.Add(p.MagneticFieldX);
        MagY.Add(p.MagneticFieldY);
        MagZ.Add(p.MagneticFieldZ);
        Theta.Add(p.Theta);

        Rssi.Add(p.RSSI);
        Snr.Add(p.SNR);

        if (Count > _maxPoints)
        {
            // Drop the oldest points in one chunk (10% extra) instead of one by one on every packet.
            var remove = Count - _maxPoints + Math.Max(1, _maxPoints / 10);
            remove = Math.Min(remove, Count - 1);
            foreach (var list in _all) list.RemoveRange(0, remove);
            Generation++;
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        foreach (var list in _all) list.Clear();
        Generation++;
        Changed?.Invoke();
    }
}
