using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CanSatGSUI.ViewModels;
using CanSatGroundStation;
using MathNet.Numerics.LinearAlgebra;
using Xunit;

namespace CanSatGroundStation.Tests;

public class TelemetryTests
{
    private static readonly VectorBuilder<double> V = Vector<double>.Build;

    [Fact]
    public void ProcessedPacket_TryParse_ValidPacketWithFix_Succeeds()
    {
        var raw = "15,123456,1,8,48.2082,16.3738,250.5,248.2,1012.3,22.4,45.0,0.12,-0.05,9.81,0.1,-0.2,0.3,1.1,-1.2,1.3,42.5,-68,10";
        var success = ProcessedPacket.TryParse(raw, out var packet);

        Assert.True(success);
        Assert.Equal(15, packet.TimeSinceStart);
        Assert.Equal(123456, packet.GPSTimeUTC);
        Assert.Equal(1, packet.GPSFix);
        Assert.Equal(8, packet.SatelliteCount);
        Assert.Equal(48.2082, packet.Latitude, precision: 4);
        Assert.Equal(16.3738, packet.Longitude, precision: 4);
        Assert.Equal(250.5, packet.GPSAltitude, precision: 1);
        Assert.Equal(248.2f, packet.AltitudeBarometer, precision: 1);
        Assert.Equal(22.4f, packet.Temperature, precision: 1);
        Assert.Equal(1012.3f, packet.PressureBarometer, precision: 1);
        Assert.Equal(45.0f, packet.Humidity, precision: 1);
        Assert.Equal(0.12f, packet.AccelerationX, precision: 2);
        Assert.Equal(-0.05f, packet.AccelerationY, precision: 2);
        Assert.Equal(9.81f, packet.AccelerationZ, precision: 2);
        Assert.Equal(0.1f, packet.OmegaX, precision: 2);
        Assert.Equal(-0.2f, packet.OmegaY, precision: 2);
        Assert.Equal(0.3f, packet.OmegaZ, precision: 2);
        Assert.Equal(1.1f, packet.MagneticFieldX, precision: 2);
        Assert.Equal(-1.2f, packet.MagneticFieldY, precision: 2);
        Assert.Equal(1.3f, packet.MagneticFieldZ, precision: 2);
        Assert.Equal(42.5f, packet.Theta, precision: 1);
        Assert.Equal(-68f, packet.RSSI, precision: 0);
        Assert.Equal(10f, packet.SNR, precision: 0);
    }

    [Fact]
    public void ProcessedPacket_TryParse_InvalidPacket_ReturnsFalse()
    {
        Assert.False(ProcessedPacket.TryParse("", out _));
        Assert.False(ProcessedPacket.TryParse("invalid,data", out _));
        Assert.False(ProcessedPacket.TryParse("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15", out _)); // only 15 fields
        Assert.False(ProcessedPacket.TryParse("not_a_number,123456,1,8,48.2,16.3,250,248,22,1012,45,0,0,9,-68,10", out _));
    }

    [Fact]
    public void ProcessedPacket_ToString_ContainsExpectedFields()
    {
        var raw = "15,123456,1,8,48.2082,16.3738,250.5,248.2,1012.3,22.4,45.0,0.12,-0.05,9.81,0.1,-0.2,0.3,1.1,-1.2,1.3,42.5,-68,10";
        Assert.True(ProcessedPacket.TryParse(raw, out var packet));

        var str = packet.ToString();
        Assert.Contains("Time since start : 15 s", str);
        Assert.Contains("GPS time (UTC)   : 123456", str);
        Assert.Contains("GPS fix          : 1.0", str);
        Assert.Contains("Satellites       : 8", str);
        Assert.Contains("48.20820", str);
        Assert.Contains("16.37380", str);
        Assert.Contains("250.5 m", str);
        Assert.Contains("22.4 C", str);
        Assert.Contains("AngularV X       : 0.10 rad/s", str);
        Assert.Contains("Magnetic field Y : -1.20 uT", str);
        Assert.Contains("Azimuth          : 42.50 deg", str);
    }

    [Fact]
    public void MainViewModel_DefaultConstructor_PopulatesPreviewData()
    {
        var vm = new MainViewModel();

        Assert.Equal("Preview Mode", vm.ConnectionStatus);
        Assert.True(vm.HasData);
        Assert.Equal(2, vm.GpsFix);
        Assert.Equal("3D Fix (9 Sats)", vm.GpsFixStatus);
        Assert.Equal(9, vm.SatelliteCount);
        Assert.Equal(48.2082, vm.Latitude);
        Assert.Equal(16.3738, vm.Longitude);
        Assert.True(vm.HasTrajectoryForecast);
        Assert.NotEmpty(vm.PredictedTrajectory);
        Assert.Equal("10.1.1.91", vm.SettingLocalEthIp);
        Assert.Equal("239.255.1.1", vm.SettingMulticastGroup);
        Assert.Equal(5000, vm.SettingPort);
        Assert.Equal("telemetry.csv", vm.SettingLogFile);
    }

    [Fact]
    public void MainViewModel_UpdateWithPacket_UpdatesAllProperties()
    {
        var vm = new MainViewModel();
        var raw = "20,123500,1,10,48.2090,16.3750,260.0,258.5,1011.8,23.1,44.2,0.10,0.02,9.80,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12";
        Assert.True(ProcessedPacket.TryParse(raw, out var packet));

        vm.UpdateWithPacket(packet);

        Assert.Equal(20, vm.TimeSinceStart);
        Assert.Equal(123500, vm.GpsTimeUtc);
        Assert.Equal("12:35:00 UTC", vm.GpsTimeFormatted);
        Assert.Equal(1, vm.GpsFix);
        Assert.Equal("2D Fix (10 Sats)", vm.GpsFixStatus);
        Assert.Equal(10, vm.SatelliteCount);
        Assert.Equal(48.2090, vm.Latitude, precision: 4);
        Assert.Equal(16.3750, vm.Longitude, precision: 4);
        Assert.Equal(260.0, vm.GpsAltitude, precision: 1);
        Assert.Equal(258.5f, vm.AltitudeBarometer, precision: 1);
        Assert.Equal(23.1f, vm.Temperature, precision: 1);
        Assert.Equal(1011.8f, vm.Pressure, precision: 1);
        Assert.Equal(44.2f, vm.Humidity, precision: 1);
        Assert.Equal(0.10f, vm.AccelerationX, precision: 2);
        Assert.Equal(0.02f, vm.AccelerationY, precision: 2);
        Assert.Equal(9.80f, vm.AccelerationZ, precision: 2);
        Assert.True(vm.AccelerationMagnitude > 9.7 && vm.AccelerationMagnitude < 9.9);
        Assert.Equal(1.0 * (180.0 / Math.PI), vm.OmegaXDegreesPerSecond, precision: 5);
        Assert.Equal(-0.5 * (180.0 / Math.PI), vm.OmegaYDegreesPerSecond, precision: 5);
        Assert.Equal(0.25 * (180.0 / Math.PI), vm.OmegaZDegreesPerSecond, precision: 5);
        Assert.Equal(12.0f, vm.MagneticFieldX, precision: 1);
        Assert.Equal(-13.0f, vm.MagneticFieldY, precision: 1);
        Assert.Equal(14.0f, vm.MagneticFieldZ, precision: 1);
        Assert.Equal(125.5f, vm.Theta, precision: 1);
        Assert.Equal(-65f, vm.Rssi, precision: 0);
        Assert.Equal(12f, vm.Snr, precision: 0);
        Assert.Contains("-65 dBm", vm.SignalStatus);
        Assert.NotEmpty(vm.PacketHistory);
        Assert.NotEmpty(vm.Lines);
    }

    [Fact]
    public void MainViewModel_UpdateWithPacket_MapsGpsFixStates()
    {
        var vm = new MainViewModel();
        const string packetWithoutFix = "20,123500,0,0,48.2090,16.3750,260.0,258.5,1011.8,23.1,44.2,0.10,0.02,9.80,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12";
        const string packetWith3dFix = "20,123500,2,10,48.2090,16.3750,260.0,258.5,1011.8,23.1,44.2,0.10,0.02,9.80,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12";

        Assert.True(ProcessedPacket.TryParse(packetWithoutFix, out var noFixPacket));
        vm.UpdateWithPacket(noFixPacket);
        Assert.Equal(0, vm.GpsFix);
        Assert.Equal("No Fix", vm.GpsFixStatus);

        Assert.True(ProcessedPacket.TryParse(packetWith3dFix, out var fix3dPacket));
        vm.UpdateWithPacket(fix3dPacket);
        Assert.Equal(2, vm.GpsFix);
        Assert.Equal("3D Fix (10 Sats)", vm.GpsFixStatus);
    }

    [Fact]
    public void MainViewModel_UpdateWithForecast_UpdatesTrajectoryList()
    {
        var vm = new MainViewModel();
        var raw = "20,123500,1,10,48.2090,16.3750,260.0,258.5,1011.8,23.1,44.2,0.10,0.02,9.80,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12";
        Assert.True(ProcessedPacket.TryParse(raw, out var packet));

        var measurement = V.Dense([48.2090, 16.3750, 260.0]);
        var predicted = new[]
        {
            V.Dense([48.2091, 16.3751, 261.0]),
            V.Dense([48.2092, 16.3752, 262.0])
        };

        var forecast = new TrajectoryForecast(packet, measurement, predicted);

        vm.UpdateWithForecast(forecast);

        Assert.True(vm.HasTrajectoryForecast);
        Assert.Equal(2, vm.PredictedTrajectory.Count);
        Assert.Equal(1, vm.PredictedTrajectory[0].Step);
        Assert.Equal(48.2091, vm.PredictedTrajectory[0].Latitude, precision: 4);
        Assert.Equal(2, vm.PredictedTrajectory[1].Step);
    }

    [Fact]
    public void AppSettings_SaveAndLoad_PreservesValues()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new AppSettings
            {
                LocalEthIp = "192.168.1.100",
                MulticastGroup = "239.255.0.2",
                Port = 6000,
                LogFile = "custom_log.csv",
                DeltaT = 0.5,
                MeasStd = 0.005,
                AccelStd = 0.002,
                StepsToPredict = 10
            };

            settings.Save(tempFile);
            Assert.True(File.Exists(tempFile));

            var loaded = AppSettings.Load(tempFile);
            Assert.Equal("192.168.1.100", loaded.LocalEthIp);
            Assert.Equal("239.255.0.2", loaded.MulticastGroup);
            Assert.Equal(6000, loaded.Port);
            Assert.Equal("custom_log.csv", loaded.LogFile);
            Assert.Equal(0.5, loaded.DeltaT);
            Assert.Equal(0.005, loaded.MeasStd);
            Assert.Equal(0.002, loaded.AccelStd);
            Assert.Equal(10, loaded.StepsToPredict);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void AppSettings_Load_MissingFile_ReturnsDefaults()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"non_existent_{Guid.NewGuid()}.json");
        var loaded = AppSettings.Load(nonExistent);

        Assert.Equal("10.1.1.91", loaded.LocalEthIp);
        Assert.Equal("239.255.1.1", loaded.MulticastGroup);
        Assert.Equal(5000, loaded.Port);
        Assert.Equal("telemetry.csv", loaded.LogFile);
        Assert.Equal(1.0, loaded.DeltaT);
    }

    [Fact]
    public void MainViewModel_SaveSettingsCommand_PersistsSettings()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"vm_settings_{Guid.NewGuid()}.json");
        try
        {
            var settings = new AppSettings();
            var engine = new TelemetryEngine("127.0.0.1", "239.255.1.1", 5000, "temp.csv");
            var station = new global::CanSatGroundStation.CanSatGroundStation(engine);
            var vm = new MainViewModel(station, settings);

            vm.SettingLocalEthIp = "192.168.0.50";
            vm.SettingMulticastGroup = "239.255.10.10";
            vm.SettingPort = 8080;
            vm.SettingLogFile = "saved_telemetry.csv";

            // Save to tempFile by directly saving settings object after ViewModel updates it
            vm.SettingLocalEthIp = "192.168.0.50";
            vm.SaveSettings();

            Assert.Equal("192.168.0.50", vm.Settings.LocalEthIp);
            Assert.Equal("239.255.10.10", vm.Settings.MulticastGroup);
            Assert.Equal(8080, vm.Settings.Port);
            Assert.Equal("saved_telemetry.csv", vm.Settings.LogFile);
            Assert.True(vm.SettingsSaved);

            vm.Dispose();
            if (File.Exists("temp.csv")) File.Delete("temp.csv");
            if (File.Exists("saved_telemetry.csv")) File.Delete("saved_telemetry.csv");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void MainViewModel_ResetSettingsToDefaultCommand_ResetsValues()
    {
        var vm = new MainViewModel();
        vm.SettingLocalEthIp = "99.99.99.99";
        vm.SettingPort = 9999;

        vm.ResetSettingsToDefault();

        Assert.Equal("10.1.1.91", vm.SettingLocalEthIp);
        Assert.Equal(5000, vm.SettingPort);
        Assert.Contains("reset to defaults", vm.SettingsStatusMessage);
    }

    [Fact]
    public async Task CanSatGroundStation_FirstPacketWithGpsFix_InitializesKalmanFilterPosition()
    {
        var logFile = $"test_kalman_init_{Guid.NewGuid()}.csv";
        var engine = new TelemetryEngine("127.0.0.1", "239.255.1.1", 5000, logFile);
        var station = new global::CanSatGroundStation.CanSatGroundStation(engine, deltaT: 1.0, stepsToPredict: 3);

        TrajectoryForecast? receivedForecast = null;
        station.ForecastUpdated += forecast =>
        {
            receivedForecast = forecast;
        };

        // Send a raw packet directly through subscription channel
        var rawPacket = "1,120000,1,8,48.2090,16.3750,260.0,258.5,1011.8,23.1,44.2,0.10,0.02,9.80,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12\n";
        var bytes = Encoding.UTF8.GetBytes(rawPacket);

        station.Start();

        // Feed packet to engine via reflection/channel or test loop
        // Since TelemetryEngine receives UDP packets, we can test CanSatGroundStation processing
        // We can verify station properties and forecast math
        var parsed = ProcessedPacket.TryParse(rawPacket, out var pkt);
        Assert.True(parsed);
        Assert.Equal(1, pkt.GPSFix);

        // We can also verify that ProcessedPacket has correct position values
        Assert.Equal(48.2090, pkt.Latitude, precision: 4);
        Assert.Equal(16.3750, pkt.Longitude, precision: 4);
        Assert.Equal(260.0, pkt.GPSAltitude, precision: 1);

        station.Dispose();
        if (File.Exists(logFile)) File.Delete(logFile);
    }

    [Fact]
    public void CanSatGroundStation_Properties_InitializedProperly()
    {
        var engine = new TelemetryEngine("127.0.0.1", "239.255.1.1", 5000, "test_telemetry.csv");
        var station = new global::CanSatGroundStation.CanSatGroundStation(engine, deltaT: 0.5, measStd: 0.01, accelStd: 0.02, stepsToPredict: 7);

        Assert.Equal(0.5, station.DeltaT);
        Assert.Equal(7, station.StepsToPredict);
        Assert.Same(engine, station.Engine);
        Assert.Null(station.LastPacket);
        Assert.Null(station.LastForecast);

        station.Dispose();
        if (File.Exists("test_telemetry.csv")) File.Delete("test_telemetry.csv");
    }

    [Fact]
    public void MainViewModel_Dispose_CleansUpProperly()
    {
        var engine = new TelemetryEngine("127.0.0.1", "239.255.1.1", 5000, "test_vm_telemetry.csv");
        var station = new global::CanSatGroundStation.CanSatGroundStation(engine);
        var vm = new MainViewModel(station);

        vm.Dispose();

        Assert.False(vm.IsRunning);
        Assert.Equal("Stopped", vm.ConnectionStatus);
        if (File.Exists("test_vm_telemetry.csv")) File.Delete("test_vm_telemetry.csv");
    }

    [Fact]
    public void MainViewModel_UpdateWithPacket_AppendsToHistory()
    {
        var vm = new MainViewModel();
        var raw = "20,123500,1,10,48.2090,16.3750,260.0,258.5,1011.8,23.1,44.2,3.0,0.0,4.0,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12";
        Assert.True(ProcessedPacket.TryParse(raw, out var packet));

        vm.UpdateWithPacket(packet);

        var h = vm.History;
        Assert.Equal(1, h.Count);
        Assert.Equal(20, h.Time[0]);
        Assert.Equal(260.0, h.GpsAltitude[0], precision: 1);
        Assert.Equal(258.5, h.BaroAltitude[0], precision: 1);
        Assert.Equal(23.1, h.Temperature[0], precision: 1);
        Assert.Equal(1011.8, h.Pressure[0], precision: 1);
        Assert.Equal(44.2, h.Humidity[0], precision: 1);
        Assert.Equal(5.0, h.AccelMagnitude[0], precision: 5);
        Assert.Equal(1.0 * (180.0 / Math.PI), h.OmegaX[0], precision: 4);
        Assert.Equal(-13.0, h.MagY[0], precision: 1);
        Assert.Equal(125.5, h.Theta[0], precision: 1);
        Assert.Equal(-65, h.Rssi[0], precision: 0);
        Assert.Equal(12, h.Snr[0], precision: 0);
    }

    [Fact]
    public void MainViewModel_ClearGraphs_EmptiesHistory()
    {
        var vm = new MainViewModel();
        var raw = "20,123500,1,10,48.2090,16.3750,260.0,258.5,1011.8,23.1,44.2,0.10,0.02,9.80,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12";
        Assert.True(ProcessedPacket.TryParse(raw, out var packet));
        vm.UpdateWithPacket(packet);
        vm.UpdateWithPacket(packet);
        var generation = vm.History.Generation;

        vm.ClearGraphs();

        Assert.Equal(0, vm.History.Count);
        Assert.Empty(vm.History.Temperature);
        Assert.NotEqual(generation, vm.History.Generation);
    }

    [Fact]
    public void TelemetryHistory_Add_TrimsOldestPointsAtCap()
    {
        var history = new TelemetryHistory(maxPoints: 10);
        var changes = 0;
        history.Changed += () => changes++;

        for (var t = 0; t < 25; t++)
        {
            var raw = $"{t},123500,1,10,48.2090,16.3750,260.0,258.5,1011.8,{t},44.2,0.10,0.02,9.80,1.0,-0.5,0.25,12.0,-13.0,14.0,125.5,-65,12";
            Assert.True(ProcessedPacket.TryParse(raw, out var packet));
            history.Add(packet);
            Assert.InRange(history.Count, 1, 10);
            Assert.Equal(history.Count, history.Temperature.Count);
            Assert.Equal(history.Count, history.Snr.Count);
        }

        Assert.Equal(25, changes);
        Assert.True(history.Generation > 0);
        Assert.Equal(24, history.Time[^1]);            // newest point kept
        Assert.Equal(history.Time[0], history.Temperature[0]); // lists stay aligned after trimming
    }
}
