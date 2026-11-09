using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSatGroundStation;

namespace CanSatGSUI.ViewModels;

public record TrajectoryForecastItem(int Step, double DeltaSeconds, double Latitude, double Longitude, double Altitude)
{
    public string StepText => $"+{Step}";
    public string DeltaText => $"+{DeltaSeconds.ToString("F1", CultureInfo.InvariantCulture)} s";
    public string LatitudeText => $"{Latitude.ToString("F5", CultureInfo.InvariantCulture)}°";
    public string LongitudeText => $"{Longitude.ToString("F5", CultureInfo.InvariantCulture)}°";
    public string AltitudeText => $"{Altitude.ToString("F1", CultureInfo.InvariantCulture)} m";

    public string DisplayText =>
        $"Step +{Step} (+{DeltaSeconds:F1}s): Lat {Latitude.ToString("F5", CultureInfo.InvariantCulture)}°, Lon {Longitude.ToString("F5", CultureInfo.InvariantCulture)}°, Alt {Altitude.ToString("F1", CultureInfo.InvariantCulture)} m";
}

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private CanSatGroundStation.CanSatGroundStation? _groundStation;
    private readonly IAppLogger _logger;
    private bool _disposed;

    // Pins are saved to settings.json only in the real app, never by the preview / test constructor.
    private readonly bool _persistPins;
    private bool _loadingPins;

    // Data health bookkeeping
    private readonly DispatcherTimer? _healthTimer;
    private DateTime? _lastPacketUtc;
    private bool _hadGpsFix;
    private readonly double[] _frozenLast = new double[FrozenSensorNames.Length];
    private readonly int[] _frozenRepeats = new int[FrozenSensorNames.Length];

    /// <summary>Sensors watched by the "frozen sensor" check (GPS values legitimately repeat on the ground).</summary>
    private static readonly string[] FrozenSensorNames =
    [
        "Barometric Altitude", "Pressure", "Temperature", "Humidity",
        "Acceleration X", "Acceleration Y", "Acceleration Z",
        "Angular velocity X", "Angular velocity Y", "Angular velocity Z",
        "Magnetic field X", "Magnetic field Y", "Magnetic field Z"
    ];

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

    // Data health settings (Settings tab, "Alert Limits & Data Health")
    [ObservableProperty] public partial double SettingLinkLostSeconds { get; set; } = 2.0;
    [ObservableProperty] public partial int SettingFrozenSensorReadings { get; set; } = 20;
    [ObservableProperty] public partial bool SettingWarnOnGpsFixLost { get; set; } = true;
    [ObservableProperty] public partial bool SettingWarnOnMissingValue { get; set; } = true;

    // Navigation: 0 = Dashboard, 1 = Graphs, 2 = Settings
    [ObservableProperty] public partial int SelectedTab { get; set; }
    public bool IsDashboardTab => SelectedTab == 0;
    public bool IsGraphsTab => SelectedTab == 1;
    public bool IsSettingsTab => SelectedTab == 2;

    /// <summary>Dashboard shows small live graphs instead of plain values.</summary>
    [ObservableProperty] public partial bool IsDashboardGraphsView { get; set; }
    public bool IsDashboardValuesView => !IsDashboardGraphsView;

    /// <summary>Graphs tab shows all graphs instead of only the pinned ones.</summary>
    [ObservableProperty] public partial bool IsAllGraphsView { get; set; } = true;
    public bool IsPinnedGraphsView => !IsAllGraphsView;
    public int PinnedCount => Graphs.Count(g => g.IsPinned);
    public string PinnedButtonText => $"Pinned ({PinnedCount})";
    public string AllButtonText => $"All ({Graphs.Count})";
    public bool ShowNoPinsHint => IsPinnedGraphsView && PinnedCount == 0;

    /// <summary>Mission time as mm:ss for the header clock.</summary>
    public string MissionClock => TimeSinceStart < 0
        ? "--:--"
        : $"{TimeSinceStart / 60:D2}:{TimeSinceStart % 60:D2}";

    /// <summary>True while the GPS reports a 2D or 3D fix (green badge in the header).</summary>
    public bool HasGpsFix => GpsFix > 0;

    /// <summary>Hint next to the Values | Graphs switch.</summary>
    public string DashboardHint => IsDashboardGraphsView
        ? "LAST 60 S · ★ PINS A GRAPH TO THE GRAPHS TAB"
        : "LIVE · UPDATES EVERY PACKET";

    /// <summary>Every active alert, one per line (tooltip on the header alert badge).</summary>
    public string AlertDetails => string.Join(Environment.NewLine, ActiveAlerts);

    /// <summary>GPS time without the " UTC" suffix (the header already says UTC).</summary>
    public string GpsClock => GpsTimeFormatted.Replace(" UTC", string.Empty);

    // Alerts & data health
    [ObservableProperty] public partial bool HasAlerts { get; private set; }
    [ObservableProperty] public partial string AlertSummary { get; private set; } = string.Empty;
    public ObservableCollection<string> ActiveAlerts { get; } = [];

    [ObservableProperty] public partial bool IsLinkLost { get; private set; }
    [ObservableProperty] public partial bool IsGpsFixLost { get; private set; }
    [ObservableProperty] public partial bool HasMissingValues { get; private set; }
    [ObservableProperty] public partial bool HasFrozenSensors { get; private set; }
    [ObservableProperty] public partial string LinkHealthText { get; private set; } = "Waiting for first packet";
    [ObservableProperty] public partial string LastPacketAgeText { get; private set; } = "—";
    [ObservableProperty] public partial string FrozenSensorsText { get; private set; } = "none";
    [ObservableProperty] public partial string MissingValuesText { get; private set; } = "none";

    // Panel-level alert flags (red outline on the dashboard panels)
    [ObservableProperty] public partial bool IsPositionAlert { get; private set; }
    [ObservableProperty] public partial bool IsAtmosphereAlert { get; private set; }
    [ObservableProperty] public partial bool IsMotionAlert { get; private set; }
    [ObservableProperty] public partial bool IsRotationAlert { get; private set; }
    [ObservableProperty] public partial bool IsRadioAlert { get; private set; }

    // The ten graphs (dashboard mini cards + Graphs tab)
    public GraphCardViewModel AltitudeGraph { get; private set; } = null!;
    public GraphCardViewModel TemperatureGraph { get; private set; } = null!;
    public GraphCardViewModel PressureGraph { get; private set; } = null!;
    public GraphCardViewModel HumidityGraph { get; private set; } = null!;
    public GraphCardViewModel AccelerationGraph { get; private set; } = null!;
    public GraphCardViewModel AngularVelocityGraph { get; private set; } = null!;
    public GraphCardViewModel MagneticFieldGraph { get; private set; } = null!;
    public GraphCardViewModel AzimuthGraph { get; private set; } = null!;
    public GraphCardViewModel RssiGraph { get; private set; } = null!;
    public GraphCardViewModel SnrGraph { get; private set; } = null!;
    public IReadOnlyList<GraphCardViewModel> Graphs { get; private set; } = [];

    // The alert limits (Settings tab table + red values)
    public AlertLimitViewModel BaroAltitudeLimit { get; } = new(LimitKeys.BaroAltitude, "Barometric Altitude", "m");
    public AlertLimitViewModel GpsAltitudeLimit { get; } = new(LimitKeys.GpsAltitude, "GPS Altitude", "m");
    public AlertLimitViewModel TemperatureLimit { get; } = new(LimitKeys.Temperature, "Temperature", "°C");
    public AlertLimitViewModel PressureLimit { get; } = new(LimitKeys.Pressure, "Pressure", "hPa");
    public AlertLimitViewModel HumidityLimit { get; } = new(LimitKeys.Humidity, "Humidity", "%");
    public AlertLimitViewModel AccelMagnitudeLimit { get; } = new(LimitKeys.AccelMagnitude, "Acceleration, Total Magnitude", "m/s²");
    public AlertLimitViewModel AngularVelocityLimit { get; } = new(LimitKeys.AngularVelocity, "Angular Velocity, any axis", "°/s");
    public AlertLimitViewModel MagneticFieldLimit { get; } = new(LimitKeys.MagneticField, "Magnetic Field, any axis", "µT");
    public AlertLimitViewModel RssiLimit { get; } = new(LimitKeys.Rssi, "RSSI", "dBm");
    public AlertLimitViewModel SnrLimit { get; } = new(LimitKeys.Snr, "Signal-to-Noise Ratio", "dB");
    public IReadOnlyList<AlertLimitViewModel> AlertLimits { get; private set; } = [];

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
        CreateGraphs();
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
        CreateGraphs();
        LoadSettingsIntoProperties(Settings);
        _persistPins = true;

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _healthTimer.Tick += (_, _) => UpdateLinkHealth(DateTime.UtcNow);
        _healthTimer.Start();

        _groundStation.PacketReceived += OnPacketReceived;
        _groundStation.ForecastUpdated += OnForecastUpdated;
    }

    public MainViewModel(TelemetryEngine engine, IAppLogger? logger = null)
        : this(new CanSatGroundStation.CanSatGroundStation(engine, logger: logger), null, logger)
    {
    }

    /// <param name="s">Settings to show.</param>
    /// <param name="formOnly">
    /// True for Reset to Defaults: only the form changes; the alert limits in force stay
    /// until Save &amp; Apply Settings, like every other setting.
    /// </param>
    private void LoadSettingsIntoProperties(AppSettings s, bool formOnly = false)
    {
        SettingLocalEthIp = s.LocalEthIp;
        SettingMulticastGroup = s.MulticastGroup;
        SettingPort = s.Port;
        SettingLogFile = s.LogFile;
        SettingDeltaT = s.DeltaT;
        SettingMeasStd = s.MeasStd;
        SettingAccelStd = s.AccelStd;
        SettingStepsToPredict = s.StepsToPredict;

        SettingLinkLostSeconds = s.LinkLostSeconds;
        SettingFrozenSensorReadings = s.FrozenSensorReadings;
        SettingWarnOnGpsFixLost = s.WarnOnGpsFixLost;
        SettingWarnOnMissingValue = s.WarnOnMissingValue;

        foreach (var limit in AlertLimits)
        {
            if (formOnly) limit.ClearText();
            else limit.Load(s.AlertLimits.TryGetValue(limit.Key, out var l) ? l : null);
        }

        _loadingPins = true;
        foreach (var graph in Graphs) graph.IsPinned = s.PinnedGraphs.Contains(graph.Key);
        _loadingPins = false;
        OnPinsChanged();
    }

    /// <summary>Creates the ten graph cards and the list of alert limits (called once by each constructor).</summary>
    private void CreateGraphs()
    {
        GraphCardViewModel G(string key, string title) => new(key, title, OnGraphPinChanged);
        AltitudeGraph = G(GraphKeys.Altitude, "Altitude");
        TemperatureGraph = G(GraphKeys.Temperature, "Temperature");
        PressureGraph = G(GraphKeys.Pressure, "Pressure");
        HumidityGraph = G(GraphKeys.Humidity, "Humidity");
        AccelerationGraph = G(GraphKeys.Acceleration, "Acceleration");
        AngularVelocityGraph = G(GraphKeys.AngularVelocity, "Angular Velocity");
        MagneticFieldGraph = G(GraphKeys.MagneticField, "Magnetic Field");
        AzimuthGraph = G(GraphKeys.Azimuth, "Azimuth (Theta)");
        RssiGraph = G(GraphKeys.Rssi, "RSSI");
        SnrGraph = G(GraphKeys.Snr, "Signal-to-Noise Ratio");
        Graphs =
        [
            AltitudeGraph, TemperatureGraph, PressureGraph, HumidityGraph, AccelerationGraph,
            AngularVelocityGraph, MagneticFieldGraph, AzimuthGraph, RssiGraph, SnrGraph
        ];
        AlertLimits =
        [
            BaroAltitudeLimit, GpsAltitudeLimit, TemperatureLimit, PressureLimit, HumidityLimit,
            AccelMagnitudeLimit, AngularVelocityLimit, MagneticFieldLimit, RssiLimit, SnrLimit
        ];
    }

    // ---------------- Navigation ----------------

    [RelayCommand]
    private void ShowTab(string? index)
    {
        if (int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i is >= 0 and <= 2)
            SelectedTab = i;
    }

    partial void OnSelectedTabChanged(int oldValue, int newValue)
    {
        OnPropertyChanged(nameof(IsDashboardTab));
        OnPropertyChanged(nameof(IsGraphsTab));
        OnPropertyChanged(nameof(IsSettingsTab));

        // Entering the Graphs tab: show your pinned graphs if you have any, otherwise all of them.
        if (newValue == 1 && oldValue != 1) IsAllGraphsView = PinnedCount == 0;
    }

    [RelayCommand]
    private void ShowDashboardValues() => IsDashboardGraphsView = false;

    [RelayCommand]
    private void ShowDashboardGraphs() => IsDashboardGraphsView = true;

    partial void OnIsDashboardGraphsViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDashboardValuesView));
        OnPropertyChanged(nameof(DashboardHint));
    }

    partial void OnGpsFixChanged(int value) => OnPropertyChanged(nameof(HasGpsFix));

    [RelayCommand]
    private void ShowPinnedGraphs() => IsAllGraphsView = false;

    [RelayCommand]
    private void ShowAllGraphs() => IsAllGraphsView = true;

    partial void OnIsAllGraphsViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPinnedGraphsView));
        OnPinsChanged();
    }

    partial void OnTimeSinceStartChanged(int value) => OnPropertyChanged(nameof(MissionClock));

    partial void OnGpsTimeFormattedChanged(string value) => OnPropertyChanged(nameof(GpsClock));

    // ---------------- Pins ----------------

    private void OnGraphPinChanged(GraphCardViewModel graph)
    {
        if (_loadingPins) return;
        OnPinsChanged();
        SavePins();
    }

    private void OnPinsChanged()
    {
        foreach (var g in Graphs) g.IsShownInGraphsTab = IsAllGraphsView || g.IsPinned;
        OnPropertyChanged(nameof(PinnedCount));
        OnPropertyChanged(nameof(PinnedButtonText));
        OnPropertyChanged(nameof(ShowNoPinsHint));
    }

    private void SavePins()
    {
        Settings.PinnedGraphs = Graphs.Where(g => g.IsPinned).Select(g => g.Key).ToList();
        if (!_persistPins) return;
        try
        {
            Settings.Save();
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "Failed to save pinned graphs", ex);
        }
    }

    [RelayCommand]
    public void SaveSettings()
    {
        // Check every alert limit first, so a typo doesn't half-apply the settings.
        var parsedLimits = new List<(AlertLimitViewModel Row, double? Min, double? Max)>();
        foreach (var limit in AlertLimits)
        {
            if (!limit.TryParse(out var min, out var max, out var error))
            {
                SettingsSaved = false;
                SettingsStatusMessage = $"Not saved. {error}";
                return;
            }

            parsedLimits.Add((limit, min, max));
        }

        try
        {
            var limits = new Dictionary<string, AlertLimit>();
            foreach (var (row, min, max) in parsedLimits)
            {
                if (row.Apply(min, max) is { } applied) limits[row.Key] = applied;
            }

            Settings.AlertLimits = limits;
            Settings.LinkLostSeconds = SettingLinkLostSeconds;
            Settings.FrozenSensorReadings = SettingFrozenSensorReadings;
            Settings.WarnOnGpsFixLost = SettingWarnOnGpsFixLost;
            Settings.WarnOnMissingValue = SettingWarnOnMissingValue;
            Settings.PinnedGraphs = Graphs.Where(g => g.IsPinned).Select(g => g.Key).ToList();

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
        // Keep the pins: resetting the network / filter settings shouldn't unpin your graphs.
        def.PinnedGraphs = Graphs.Where(g => g.IsPinned).Select(g => g.Key).ToList();
        LoadSettingsIntoProperties(def, formOnly: true);
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

        _lastPacketUtc = DateTime.UtcNow;
        UpdateDataHealth(packet);
        EvaluateAlerts(packet);
        UpdateLinkHealth(_lastPacketUtc.Value);
    }

    // ---------------- Alerts & data health ----------------

    /// <summary>Checks the fixed limits against a packet and refreshes every alert flag.</summary>
    private void EvaluateAlerts(ProcessedPacket p)
    {
        double ox = p.OmegaX * RadiansToDegrees, oy = p.OmegaY * RadiansToDegrees, oz = p.OmegaZ * RadiansToDegrees;
        var accelMag = Math.Sqrt((double)p.AccelerationX * p.AccelerationX +
                                 (double)p.AccelerationY * p.AccelerationY +
                                 (double)p.AccelerationZ * p.AccelerationZ);

        BaroAltitudeLimit.Check([p.AltitudeBarometer]);
        GpsAltitudeLimit.Check([p.GPSAltitude]);
        TemperatureLimit.Check([p.Temperature]);
        PressureLimit.Check([p.PressureBarometer]);
        HumidityLimit.Check([p.Humidity]);
        AccelMagnitudeLimit.Check([accelMag]);
        AngularVelocityLimit.Check([ox, oy, oz]);
        MagneticFieldLimit.Check([p.MagneticFieldX, p.MagneticFieldY, p.MagneticFieldZ]);
        RssiLimit.Check([p.RSSI]);
        SnrLimit.Check([p.SNR]);

        RefreshAlertSummary();
    }

    /// <summary>Recomputes panel flags, graph flags and the header summary from the current alert state.</summary>
    private void RefreshAlertSummary()
    {
        AltitudeGraph.IsAlert = BaroAltitudeLimit.IsTripped || GpsAltitudeLimit.IsTripped;
        TemperatureGraph.IsAlert = TemperatureLimit.IsTripped;
        PressureGraph.IsAlert = PressureLimit.IsTripped;
        HumidityGraph.IsAlert = HumidityLimit.IsTripped;
        AccelerationGraph.IsAlert = AccelMagnitudeLimit.IsTripped;
        AngularVelocityGraph.IsAlert = AngularVelocityLimit.IsTripped;
        MagneticFieldGraph.IsAlert = MagneticFieldLimit.IsTripped;
        RssiGraph.IsAlert = RssiLimit.IsTripped;
        SnrGraph.IsAlert = SnrLimit.IsTripped;

        IsPositionAlert = GpsAltitudeLimit.IsTripped || IsGpsFixLost;
        IsAtmosphereAlert = BaroAltitudeLimit.IsTripped || TemperatureLimit.IsTripped ||
                            PressureLimit.IsTripped || HumidityLimit.IsTripped;
        IsMotionAlert = AccelMagnitudeLimit.IsTripped;
        IsRotationAlert = AngularVelocityLimit.IsTripped || MagneticFieldLimit.IsTripped;
        IsRadioAlert = RssiLimit.IsTripped || SnrLimit.IsTripped || IsLinkLost;

        var alerts = new List<string>();
        foreach (var limit in AlertLimits)
        {
            if (limit.IsTripped) alerts.Add(ShortName(limit.Key, limit.Name));
        }

        if (IsLinkLost) alerts.Add("Link lost");
        if (IsGpsFixLost) alerts.Add("GPS fix lost");
        if (HasFrozenSensors) alerts.Add("Frozen sensor");
        if (HasMissingValues) alerts.Add("Missing value");

        if (!alerts.SequenceEqual(ActiveAlerts))
        {
            ActiveAlerts.Clear();
            foreach (var a in alerts) ActiveAlerts.Add(a);
        }

        OnPropertyChanged(nameof(AlertDetails));
        HasAlerts = alerts.Count > 0;
        AlertSummary = alerts.Count switch
        {
            0 => string.Empty,
            1 => $"1 Alert · {alerts[0]}",
            _ => $"{alerts.Count} Alerts · {alerts[0]} +{alerts.Count - 1}"
        };
    }

    private static string ShortName(string key, string name) => key switch
    {
        LimitKeys.Snr => "SNR",
        LimitKeys.AccelMagnitude => "Acceleration",
        LimitKeys.AngularVelocity => "Angular Velocity",
        LimitKeys.MagneticField => "Magnetic Field",
        _ => name
    };

    /// <summary>Frozen-sensor, missing-value and GPS-fix checks for one packet.</summary>
    private void UpdateDataHealth(ProcessedPacket p)
    {
        double[] sensors =
        [
            p.AltitudeBarometer, p.PressureBarometer, p.Temperature, p.Humidity,
            p.AccelerationX, p.AccelerationY, p.AccelerationZ,
            p.OmegaX, p.OmegaY, p.OmegaZ,
            p.MagneticFieldX, p.MagneticFieldY, p.MagneticFieldZ
        ];

        // Frozen: the same reading many times in a row.
        var frozen = new List<string>();
        var threshold = Math.Max(2, Settings.FrozenSensorReadings);
        for (var i = 0; i < sensors.Length; i++)
        {
            // The first packet starts a fresh count.
            _frozenRepeats[i] = TotalPacketsReceived > 1 && sensors[i].Equals(_frozenLast[i]) ? _frozenRepeats[i] + 1 : 1;
            _frozenLast[i] = sensors[i];
            if (_frozenRepeats[i] >= threshold) frozen.Add(FrozenSensorNames[i]);
        }

        HasFrozenSensors = frozen.Count > 0;
        FrozenSensorsText = frozen.Count == 0 ? "none" : string.Join(", ", frozen);

        // Missing: the parser turns "nan" into NaN.
        double[] all =
        [
            p.Latitude, p.Longitude, p.GPSAltitude, p.AltitudeBarometer, p.PressureBarometer, p.Temperature,
            p.Humidity, p.AccelerationX, p.AccelerationY, p.AccelerationZ, p.OmegaX, p.OmegaY, p.OmegaZ,
            p.MagneticFieldX, p.MagneticFieldY, p.MagneticFieldZ, p.Theta, p.RSSI, p.SNR
        ];
        var missing = all.Count(double.IsNaN);
        HasMissingValues = Settings.WarnOnMissingValue && missing > 0;
        MissingValuesText = missing == 0 ? "none" : $"{missing} in last packet";

        // Fix lost: we had a fix, now we don't.
        if (p.GPSFix > 0) _hadGpsFix = true;
        IsGpsFixLost = Settings.WarnOnGpsFixLost && _hadGpsFix && p.GPSFix == 0;
    }

    /// <summary>Called on every packet and by a 250 ms timer: link age and "link lost".</summary>
    private void UpdateLinkHealth(DateTime nowUtc)
    {
        if (_lastPacketUtc is not { } last)
        {
            LinkHealthText = IsRunning ? "Waiting for first packet" : "Not listening";
            LastPacketAgeText = "—";
            return;
        }

        var age = Math.Max(0, (nowUtc - last).TotalSeconds);
        var ageText = age.ToString("0.0", CultureInfo.InvariantCulture);
        LastPacketAgeText = ageText;

        var lost = age > Math.Max(0.1, Settings.LinkLostSeconds);
        LinkHealthText = lost ? $"Link lost · no packet for {ageText} s" : $"Link OK · last packet {ageText} s ago";
        if (lost != IsLinkLost)
        {
            IsLinkLost = lost;
            RefreshAlertSummary();
        }
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
        _healthTimer?.Stop();

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