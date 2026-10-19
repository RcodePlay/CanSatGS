using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using _3DKalman;
using MathNet.Numerics.LinearAlgebra;

namespace CanSatGroundStation;

/// <summary>
/// Telemetry collection class.
/// </summary>
public class TelemetryEngine {
    private readonly IPAddress LocalEthIp;
    private readonly IPAddress MulticastGroup;
    private readonly int Port;
    private readonly string LogFilePath;
    private readonly Channel<byte[]> DataChannel;

    /// <summary>
    /// Collects telemetry from multicast group in form of UDP packets.
    /// </summary>
    /// <param name="localIp">Interface IP Address the listener is bound to.</param>
    /// <param name="multicastGroup">Multicast group IP Address to listen to.</param>
    /// <param name="port">Interface port to listen through.</param>
    /// <param name="logPath">Path to the log file.</param>
    public TelemetryEngine(string localIp, string multicastGroup, int port, string logPath)
    {
        LocalEthIp = localIp;
        MulticastGroup = multicastGroup;
        Port = port;
        LogFilePath = logPath;

        DataChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true
        });
    }

    /// <summary>
    /// Starts the worker thread.
    /// </summary>
    public void Start() {
        // 1. Dedicated network thread (LongRunning ensures a fresh thread off the ThreadPool)
        Task.Factory.StartNew(
            () => RunUdpListenerLoop(cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // 2. Background file logging task
        Task.Run(() => RunLoggerLoopAsync(_logFilePath, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs the listener loop.
    /// </summary>
    /// <param name="cancellationToken">Triggers an operation cancellation.</param>
    public void RunUdpListenerLoop(CancellationToken cancellationToken) {
        using var listener = new UdpClient();

        try
        {        
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var localEp = new IPEndPoint(IPAddress.Any, Port);
            listener.Client.Bind(localEp);

            listener.JoinMulticastGroup(
                IPAddress.Parse(MulticastGroup),
                IPAddress.Parse(LocalEthIp));

            Console.WriteLine($"Listening for CanSat telemetry on {MulticastGroup}:{Port} via {LocalEthIp}...");

            while(!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    IPEndPoint? remoteEp = null;
                    var data = listener.Receive(ref remoteEp);

                    if (data != null && data.Length > 0)
                        {
                            _dataChannel.Writer.TryWrite(data);
                        }
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"[UDP Socket Error]: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UDP Loop Error]: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fatal UDP Initialization Error]: {ex.Message}");
        }
        finally
        {
            try { listener.DropMulticastGroup(MulticastGroup); } catch { }
            DataChannel.Writer.Complete();
        }
    }

    /// <summary>
    /// Runs the logger loop asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Triggers an operation cancellation.</param>
    /// <returns>Logger async task.</returns>
    private async Task RunLoggerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using FileStream fs = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using StreamWriter writer = new StreamWriter(fs, Encoding.UTF8);

            if (fs.Length == 0)
            {
                await writer.WriteLineAsync("SystemTimestamp,RawHexData");
                await writer.FlushAsync(cancellationToken);
            }

            while (await _dataChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_dataChannel.Reader.TryRead(out byte[]? payload))
                {
                    try
                    {
                        string timeStr = DateTime.UtcNow.ToString("o");
                        string hexData = Convert.ToHexString(payload);

                        await writer.WriteLineAsync($"{timeStr},{hexData}");
                        await writer.FlushAsync(cancellationToken);

                        // NOTE: Trigger your 3DKalman filter & UI updates here or via an event/callback
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Logger Exception] Failed to record packet: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown requested
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fatal Logger Error] {ex.Message}");
        }
    }
}





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
        // TODO: Replace with new TelemetryEngine class and methods
        // using var listener = new UdpClient();

        // listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        // var localEp = new IPEndPoint(IPAddress.Any, Port);
        // listener.Client.Bind(localEp);

        // listener.JoinMulticastGroup(
        //     IPAddress.Parse(MulticastGroup),
        //     IPAddress.Parse(LocalEthIp));

        // Console.WriteLine($"Listening for CanSat telemetry on {MulticastGroup}:{Port} via {LocalEthIp}...");

        // try
        // {
        //     while (true)
        //     {
        //         IPEndPoint? remoteEp = null;
        //         var data = listener.Receive(ref remoteEp);

        //         ProcessPacket(data);
        //     }
        // }
        // catch (Exception ex)
        // {
        //     Console.WriteLine($"Error: {ex.Message}");
        // }
        // finally
        // {
        //     listener.DropMulticastGroup(IPAddress.Parse(MulticastGroup));
        // }
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
// TODO: Packet processing class + methods
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