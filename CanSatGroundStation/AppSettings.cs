using System;
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
