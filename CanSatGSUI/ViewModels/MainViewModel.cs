using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSatGroundStation;

namespace CanSatGSUI.ViewModels;

public record TrajectoryForecastItem(int Step, double DeltaSeconds, double Latitude, double Longitude, double Altitude)
{
    public string DisplayText =>
        $"Step +{Step} (+{DeltaSeconds:F1}s): Lat {Latitude.ToString("F5", CultureInfo.InvariantCulture)}°, Lon {Longitude.ToString("F5", CultureInfo.InvariantCulture)}°, Alt {Altitude.ToString("F1", CultureInfo.InvariantCulture)} m";
}

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private CanSatGroundStation.CanSatGroundStation? _groundStation;
    private readonly IAppLogger _logger;
    private bool _disposed;

    // Status and Metadata
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial bool HasData { get; private set; }
    [ObservableProperty] public partial int TotalPacketsReceived { get; set; }
    [ObservableProperty] public partial string ConnectionStatus { get; set; } = "Ready";
    [ObservableProperty] public partial string LastPacketTimeString { get; set; } = "--:--:--";

    // Timing
    [ObservableProperty] public partial int TimeSinceStart { get; set; }
    [ObservableProperty] public partial int GpsTimeUtc { get; private set; }
    [ObservableProperty] public partial string GpsTimeFormatted { get; set; } = "--:--:--";

    // GPS & Navigation
    [ObservableProperty] public partial int GpsFix { get; private set; }
    [ObservableProperty] public partial string GpsFixStatus { get; set; } = "No Fix";
    [ObservableProperty] public partial int SatelliteCount { get; set; }
    [ObservableProperty] public partial double Latitude { get; set; }
    [ObservableProperty] public partial double Longitude { get; set; }
    [ObservableProperty] public partial double GpsAltitude { get; set; }

    // Atmosphere & Environment
    [ObservableProperty] public partial float AltitudeBarometer { get; set; }
    [ObservableProperty] public partial float Temperature { get; set; }
    [ObservableProperty] public partial float Pressure { get; set; }
    [ObservableProperty] public partial float Humidity { get; set; }

    // Dynamics & Acceleration
    [ObservableProperty] public partial float AccelerationX { get; set; }
    [ObservableProperty] public partial float AccelerationY { get; set; }
    [ObservableProperty] public partial float AccelerationZ { get; set; }
    [ObservableProperty] public partial double AccelerationMagnitude { get; set; }
    [ObservableProperty] public partial double OmegaXDegreesPerSecond { get; set; }
    [ObservableProperty] public partial double OmegaYDegreesPerSecond { get; set; }
    [ObservableProperty] public partial double OmegaZDegreesPerSecond { get; set; }
    [ObservableProperty] public partial float MagneticFieldX { get; set; }
    [ObservableProperty] public partial float MagneticFieldY { get; set; }
    [ObservableProperty] public partial float MagneticFieldZ { get; set; }
    [ObservableProperty] public partial float Theta { get; set; }

    // RF & Signal
    [ObservableProperty] public partial float Rssi { get; set; }
    [ObservableProperty] public partial float Snr { get; set; }
    [ObservableProperty] public partial string SignalStatus { get; set; } = "No Signal";

    // Telemetry text overview & History
    [ObservableProperty] public partial string FormattedTelemetry { get; set; } = "Waiting for telemetry packets...";
    [ObservableProperty] public partial bool HasTrajectoryForecast { get; set; }

    // Settings
    [ObservableProperty] public partial string SettingLocalEthIp { get; set; } = "10.1.1.91";
    [ObservableProperty] public partial string SettingMulticastGroup { get; set; } = "239.255.1.1";
    [ObservableProperty] public partial int SettingPort { get; set; } = 5000;
    [ObservableProperty] public partial string SettingLogFile { get; set; } = "telemetry.csv";
    [ObservableProperty] public partial double SettingDeltaT { get; set; } = 1.0;
    [ObservableProperty] public partial double SettingMeasStd { get; set; } = 0.001;
    [ObservableProperty] public partial double SettingAccelStd { get; set; } = 0.001;
    [ObservableProperty] public partial int SettingStepsToPredict { get; set; } = 5;
    [ObservableProperty] public partial string SettingsStatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool SettingsSaved { get; private set; }

    public ObservableCollection<TrajectoryForecastItem> PredictedTrajectory { get; } = [];
    public ObservableCollection<string> Lines { get; } = [];
    public ObservableCollection<string> PacketHistory { get; } = [];

    /// <summary>All received measurements over time, shown in the Graphs tab.</summary>
    public TelemetryHistory History { get; } = new();

    public AppSettings Settings { get; }

    /// <summary>
    /// Design-time parameterless constructor for XAML designer preview.
    /// </summary>
    public MainViewModel()
    {
        _logger = NullAppLogger.Instance;
        Settings = new AppSettings();
        LoadSettingsIntoProperties(Settings);

        ConnectionStatus = "Preview Mode";
        TimeSinceStart = 42;
        GpsTimeUtc = 123456;
        GpsTimeFormatted = "12:34:56 UTC";
        GpsFix = 2;
        GpsFixStatus = "3D Fix (9 Sats)";
        SatelliteCount = 9;
        Latitude = 48.2082;
        Longitude = 16.3738;
        GpsAltitude = 250.5;
        AltitudeBarometer = 248.2f;
        Temperature = 22.4f;
        Pressure = 1012.30f;
        Humidity = 45.0f;
        AccelerationX = 0.12f;
        AccelerationY = -0.05f;
        AccelerationZ = 9.81f;
        AccelerationMagnitude = 9.81;
        OmegaXDegreesPerSecond = 5.73;
        OmegaYDegreesPerSecond = -11.46;
        OmegaZDegreesPerSecond = 17.19;
        MagneticFieldX = 12.0f;
        MagneticFieldY = -13.0f;
        MagneticFieldZ = 14.0f;
        Theta = 125.5f;
        Rssi = -68f;
        Snr = 10f;
        SignalStatus = "-68 dBm (SNR: 10 dB)";
        HasData = true;
        TotalPacketsReceived = 1;
        FormattedTelemetry =
            "Time since start : 42 s\nGPS time (UTC)   : 123456\nGPS fix          : 2 (3D)\nSatellites       : 9\nLatitude         : 48.20820\nLongitude        : 16.37380\nGPS altitude     : 250.5 m\nBarometer alt    : 248.2 m\nTemperature      : 22.4 C\nPressure         : 1012.30 hPa\nHumidity         : 45.0 %\nAcceleration X   : 0.12 m/s^2\nAcceleration Y   : -0.05 m/s^2\nAcceleration Z   : 9.81 m/s^2\nAngularV X       : 5.73 °/s\nAngularV Y       : -11.46 °/s\nAngularV Z       : 17.19 °/s\nMagnetic field X : 12.00 uT\nMagnetic field Y : -13.00 uT\nMagnetic field Z : 14.00 uT\nAzimuth          : 125.50 deg\nRSSI             : -68 dBm\nSNR              : 10 dB";

        PredictedTrajectory.Add(new TrajectoryForecastItem(1, 1.0, 48.2083, 16.3739, 252.0));
        PredictedTrajectory.Add(new TrajectoryForecastItem(2, 2.0, 48.2084, 16.3740, 253.5));
        PredictedTrajectory.Add(new TrajectoryForecastItem(3, 3.0, 48.2085, 16.3741, 255.0));
        HasTrajectoryForecast = true;
    }

    public MainViewModel(CanSatGroundStation.CanSatGroundStation groundStation, AppSettings? settings = null,
        IAppLogger? logger = null)
    {
        _groundStation = groundStation ?? throw new ArgumentNullException(nameof(groundStation));
        _logger = logger ?? NullAppLogger.Instance;
        Settings = settings ?? AppSettings.Load();
        LoadSettingsIntoProperties(Settings);

        _groundStation.PacketReceived += OnPacketReceived;
        _groundStation.ForecastUpdated += OnForecastUpdated;
    }

    public MainViewModel(TelemetryEngine engine, IAppLogger? logger = null)
        : this(new CanSatGroundStation.CanSatGroundStation(engine, logger: logger), null, logger)
    {
    }

    private void LoadSettingsIntoProperties(AppSettings s)
    {
        SettingLocalEthIp = s.LocalEthIp;
        SettingMulticastGroup = s.MulticastGroup;
        SettingPort = s.Port;
        SettingLogFile = s.LogFile;
        SettingDeltaT = s.DeltaT;
        SettingMeasStd = s.MeasStd;
        SettingAccelStd = s.AccelStd;
        SettingStepsToPredict = s.StepsToPredict;
    }

    [RelayCommand]
    public void SaveSettings()
    {
        try
        {
            Settings.LocalEthIp = SettingLocalEthIp.Trim();
            Settings.MulticastGroup = SettingMulticastGroup.Trim();
            Settings.Port = SettingPort;
            Settings.LogFile = SettingLogFile.Trim();
            Settings.DeltaT = SettingDeltaT;
            Settings.MeasStd = SettingMeasStd;
            Settings.AccelStd = SettingAccelStd;
            Settings.StepsToPredict = SettingStepsToPredict;

            Settings.Save();
            SettingsSaved = true;
            SettingsStatusMessage = $"Settings saved successfully ({DateTime.Now:HH:mm:ss}).";

            RestartStation();
        }
        catch (Exception ex)
        {
            SettingsSaved = false;
            SettingsStatusMessage = $"Error saving settings: {ex.Message}";
            _logger.Log(LogLevel.Error, "Failed to save settings", ex);
        }
    }

    [RelayCommand]
    public void ResetSettingsToDefault()
    {
        var def = new AppSettings();
        LoadSettingsIntoProperties(def);
        SettingsSaved = false;
        SettingsStatusMessage = "Settings reset to defaults. Click 'Save & Apply Settings' to persist.";
    }

    private void RestartStation()
    {
        try
        {
            if (_groundStation is not null)
            {
                _groundStation.PacketReceived -= OnPacketReceived;
                _groundStation.ForecastUpdated -= OnForecastUpdated;
                _groundStation.Dispose();
                _groundStation = null;
            }

            var engine = new TelemetryEngine(Settings.LocalEthIp, Settings.MulticastGroup, Settings.Port,
                Settings.LogFile, _logger);
            _groundStation = new CanSatGroundStation.CanSatGroundStation(engine, Settings.DeltaT, Settings.MeasStd,
                Settings.AccelStd, _logger, Settings.StepsToPredict);
            _groundStation.PacketReceived += OnPacketReceived;
            _groundStation.ForecastUpdated += OnForecastUpdated;

            Start();
            SettingsStatusMessage += " Station reconnected.";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Error: {ex.Message}";
            SettingsStatusMessage += $" Reconnect failed: {ex.Message}";
            _logger.Log(LogLevel.Error, "Failed to restart station with new settings", ex);
        }
    }

    public void Start()
    {
        if (_groundStation is null) return;
        try
        {
            _groundStation.Start();
            IsRunning = true;
            ConnectionStatus = $"Listening on {_groundStation.Engine.MulticastGroup}:{_groundStation.Engine.Port}";
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "Failed to start Ground Station", ex);
            ConnectionStatus = $"Error: {ex.Message}";
        }
    }

    public void UpdateWithPacket(ProcessedPacket packet)
    {
        var inv = CultureInfo.InvariantCulture;
        TimeSinceStart = packet.TimeSinceStart;
        GpsTimeUtc = packet.GPSTimeUTC;

        int hours = (packet.GPSTimeUTC / 10000) % 100;
        int minutes = (packet.GPSTimeUTC / 100) % 100;
        int seconds = packet.GPSTimeUTC % 100;
        GpsTimeFormatted = $"{hours:D2}:{minutes:D2}:{seconds:D2} UTC";

        GpsFix = packet.GPSFix;
        GpsFixStatus = packet.GPSFix switch
        {
            0 => "No Fix",
            1 => $"2D Fix ({packet.SatelliteCount} Sats)",
            2 => $"3D Fix ({packet.SatelliteCount} Sats)",
            _ => $"Unknown Fix ({packet.GPSFix})"
        };
        SatelliteCount = packet.SatelliteCount;
        Latitude = packet.Latitude;
        Longitude = packet.Longitude;
        GpsAltitude = packet.GPSAltitude;

        AltitudeBarometer = packet.AltitudeBarometer;
        Temperature = packet.Temperature;
        Pressure = packet.PressureBarometer;
        Humidity = packet.Humidity;

        AccelerationX = packet.AccelerationX;
        AccelerationY = packet.AccelerationY;
        AccelerationZ = packet.AccelerationZ;
        AccelerationMagnitude = Math.Sqrt(
            packet.AccelerationX * packet.AccelerationX +
            packet.AccelerationY * packet.AccelerationY +
            packet.AccelerationZ * packet.AccelerationZ);
        OmegaXDegreesPerSecond = packet.OmegaX * RadiansToDegrees;
        OmegaYDegreesPerSecond = packet.OmegaY * RadiansToDegrees;
        OmegaZDegreesPerSecond = packet.OmegaZ * RadiansToDegrees;
        MagneticFieldX = packet.MagneticFieldX;
        MagneticFieldY = packet.MagneticFieldY;
        MagneticFieldZ = packet.MagneticFieldZ;
        Theta = packet.Theta;

        Rssi = packet.RSSI;
        Snr = packet.SNR;
        SignalStatus = $"{packet.RSSI:F0} dBm (SNR: {packet.SNR:F0} dB)";

        FormattedTelemetry = packet.ToString();
        LastPacketTimeString = DateTime.UtcNow.ToString("HH:mm:ss", inv);
        TotalPacketsReceived++;
        HasData = true;

        var logEntry =
            $"[{LastPacketTimeString}] T+{packet.TimeSinceStart}s | Alt: {packet.AltitudeBarometer:F1}m | Temp: {packet.Temperature:F1}°C | Press: {packet.PressureBarometer:F1}hPa | RSSI: {packet.RSSI:F0}dBm";
        PacketHistory.Insert(0, logEntry);
        if (PacketHistory.Count > 500) PacketHistory.RemoveAt(PacketHistory.Count - 1);

        Lines.Insert(0, logEntry);
        if (Lines.Count > 500) Lines.RemoveAt(Lines.Count - 1);

        History.Add(packet);
    }

    [RelayCommand]
    public void ClearGraphs()
    {
        History.Clear();
    }

    public void UpdateWithForecast(TrajectoryForecast forecast)
    {
        PredictedTrajectory.Clear();
        var deltaT = _groundStation?.DeltaT ?? Settings.DeltaT;
        for (var i = 0; i < forecast.PredictedTrajectory.Count; i++)
        {
            var p = forecast.PredictedTrajectory[i];
            PredictedTrajectory.Add(new TrajectoryForecastItem(
                Step: i + 1,
                DeltaSeconds: (i + 1) * deltaT,
                Latitude: p[0],
                Longitude: p[1],
                Altitude: p[2]
            ));
        }

        HasTrajectoryForecast = PredictedTrajectory.Count > 0;
    }

    private void OnPacketReceived(ProcessedPacket packet)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateWithPacket(packet);
        }
        else
        {
            Dispatcher.UIThread.Post(() => UpdateWithPacket(packet));
        }
    }

    private void OnForecastUpdated(TrajectoryForecast forecast)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateWithForecast(forecast);
        }
        else
        {
            Dispatcher.UIThread.Post(() => UpdateWithForecast(forecast));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_groundStation is not null)
        {
            _groundStation.PacketReceived -= OnPacketReceived;
            _groundStation.ForecastUpdated -= OnForecastUpdated;
            _groundStation.Dispose();
        }

        IsRunning = false;
        ConnectionStatus = "Stopped";
    }
}