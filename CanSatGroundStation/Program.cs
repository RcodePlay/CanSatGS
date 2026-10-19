using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using _3DKalman;
using MathNet.Numerics.LinearAlgebra;

namespace CanSatGroundStation;

internal abstract class CanSatGroundStation
{

    private static readonly VectorBuilder<double> V = Vector<double>.Build;

    private const string MulticastGroup = "239.255.1.1";
    private const int Port = 5000;
    private const string LocalEthIp = "10.1.1.91";

    private const double MeasStd = 0.001;
    private const double AccelStd = 0.001;
    private static readonly Vector<double> InitialPos = V.Dense(3);

    private static void Main()
    {
        using var listener = new UdpClient();
        var kalman = new Kalman3D(MeasStd, AccelStd, InitialPos);


        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var localEp = new IPEndPoint(IPAddress.Any, Port);
        listener.Client.Bind(localEp);

        listener.JoinMulticastGroup(
            IPAddress.Parse(MulticastGroup),
            IPAddress.Parse(LocalEthIp));

        Console.WriteLine($"Listening for CanSat telemetry on {MulticastGroup}:{Port} via {LocalEthIp}...");

        try
        {
            while (true)
            {
                IPEndPoint? remoteEp = null;
                var data = listener.Receive(ref remoteEp);

                ProcessPacket(data);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            listener.DropMulticastGroup(IPAddress.Parse(MulticastGroup));
        }
    }

    private struct TelemetryFormat
    {
        public int TimeSinceStart;
        public int GPSTimeUTC;
        public bool GPSFix;
        public int SatelliteCount;
        public float Latitude;
        public float Longitude;
        public float GPSAltitude;
        public float AltitudeBarometer;
        public float Temperature;
        public float Pressure;
        public float Humidity;
        public float AccelerationX;
        public float AccelerationY;
        public float AccelerationZ;
        public float RSSI;
        public float SNR;

        public override string ToString()
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join(Environment.NewLine,
                $"Time since start : {TimeSinceStart} s",
                $"GPS time (UTC)   : {GPSTimeUTC:D6}",
                $"GPS fix          : {(GPSFix ? "Yes" : "No")}",
                $"Satellites       : {SatelliteCount}",
                $"Latitude         : {Latitude.ToString("F5", inv)}",
                $"Longitude        : {Longitude.ToString("F5", inv)}",
                $"GPS altitude     : {GPSAltitude.ToString("F1", inv)} m",
                $"Barometer alt    : {AltitudeBarometer.ToString("F1", inv)} m",
                $"Temperature      : {Temperature.ToString("F1", inv)} C",
                $"Pressure         : {Pressure.ToString("F2", inv)} hPa",
                $"Humidity         : {Humidity.ToString("F1", inv)} %",
                $"Acceleration X   : {AccelerationX.ToString("F2", inv)} m/s^2",
                $"Acceleration Y   : {AccelerationY.ToString("F2", inv)} m/s^2",
                $"Acceleration Z   : {AccelerationZ.ToString("F2", inv)} m/s^2",
                $"RSSI             : {RSSI.ToString("F0", inv) + " dBm"}",
                $"SNR              : {SNR.ToString("F0", inv) + " dB"}",
                "----------------------------------------");
        }
    }

    private static void ProcessPacket(byte[] data)
    {
        var raw = Encoding.UTF8.GetString(data).Trim();
        var f = raw.Split(',');

        if (f.Length != 16)
        {
            Console.WriteLine($"Malformed packet ({f.Length} fields): {raw}");
            return;
        }

        var inv = CultureInfo.InvariantCulture;

        try
        {
            var telemetry = new TelemetryFormat
            {
                TimeSinceStart = int.Parse(f[0], inv),
                GPSTimeUTC = int.Parse(f[1], inv),
                GPSFix = f[2].Trim() == "1",
                SatelliteCount = int.Parse(f[3], inv),
                Latitude = float.Parse(f[4], inv),
                Longitude = float.Parse(f[5], inv),
                GPSAltitude = float.Parse(f[6], inv),
                AltitudeBarometer = float.Parse(f[7], inv),
                Temperature = float.Parse(f[8], inv),
                Pressure = float.Parse(f[9], inv),
                Humidity = float.Parse(f[10], inv),
                AccelerationX = float.Parse(f[11], inv),
                AccelerationY = float.Parse(f[12], inv),
                AccelerationZ = float.Parse(f[13], inv),
                RSSI = float.Parse(f[14], inv),
                SNR = float.Parse(f[15], inv)
            };

            Console.WriteLine(telemetry);
        }
        catch (FormatException)
        {
            Console.WriteLine($"Could not parse packet: {raw}");
        }
    }
}