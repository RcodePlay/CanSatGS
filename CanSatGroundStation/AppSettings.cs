using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CanSatGroundStation;

public class AppSettings
{
    public string LocalEthIp { get; set; } = "10.1.1.91";
    public string MulticastGroup { get; set; } = "239.255.1.1";
    public int Port { get; set; } = 5000;
    public string LogFile { get; set; } = "telemetry.csv";
    public double DeltaT { get; set; } = 1.0;
    public double MeasStd { get; set; } = 0.001;
    public double AccelStd { get; set; } = 0.001;
    public int StepsToPredict { get; set; } = 5;

    /// <summary>Keys of the graphs pinned to the Graphs tab (see GraphKeys in the UI project).</summary>
    public List<string> PinnedGraphs { get; set; } = [];

    /// <summary>Fixed alert limits per measurement key. A missing entry or a null bound means "no limit".</summary>
    public Dictionary<string, AlertLimit> AlertLimits { get; set; } = new();

    /// <summary>Data health: the link counts as lost after this many seconds without a packet.</summary>
    public double LinkLostSeconds { get; set; } = 2.0;

    /// <summary>Data health: a sensor counts as frozen after this many identical readings in a row.</summary>
    public int FrozenSensorReadings { get; set; } = 20;

    /// <summary>Data health: warn when the GPS had a fix and lost it.</summary>
    public bool WarnOnGpsFixLost { get; set; } = true;

    /// <summary>Data health: warn when a packet contains a missing (NaN) value.</summary>
    public bool WarnOnMissingValue { get; set; } = true;

    public static string DefaultFilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        var filePath = path ?? DefaultFilePath;
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return settings;
            }
        }
        catch
        {
            // fallback to default values on error
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        var filePath = path ?? DefaultFilePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(filePath, json);
    }
}

/// <summary>Min / max alert bounds for one measurement. Null means no bound on that side.</summary>
public class AlertLimit
{
    public double? Min { get; set; }
    public double? Max { get; set; }
}
