using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using _3DKalman;
using MathNet.Numerics.LinearAlgebra;

namespace CanSatGroundStation;

public readonly record struct TelemetryPacket(DateTime TimestampUtc, byte[] Data);

/// <summary>
/// Telemetry collection class.
/// </summary>
public sealed class TelemetryEngine(string localIp, string multicastGroup, int port, string logPath)
    : IDisposable
{
    private const string Header = "SystemTimestamp,TimeSinceStart,GPSTimeUTC,GPSFix,SatelliteCount,Latitude,Longitude,GPSAltitude,AltitudeBarometer,Temperature,Pressure,Humidity,AccelerationX,AccelerationY,AccelerationZ,RSSI,SNR";

    private readonly IPAddress _localIp = IPAddress.Parse(localIp);
    private readonly IPAddress _group = IPAddress.Parse(multicastGroup);

    private readonly BlockingCollection<TelemetryPacket> _logQueue =
        new(new ConcurrentQueue<TelemetryPacket>());

    private readonly Lock _subLock = new();
    private ImmutableArray<ChannelWriter<TelemetryPacket>> _subscribers = [];

    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _tsBuf = new byte[40];
    private Thread? _listener;
    private Thread? _logger;

    /// <summary>
    /// Live feed for UI / processing. Each subscriber gets its own bounded channel,
    /// so a slow consumer only loses its own old packets and can never affect logging.
    /// </summary>
    public ChannelReader<TelemetryPacket> Subscribe(int capacity = 1000)
    {
        var ch = Channel.CreateBounded<TelemetryPacket>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        lock (_subLock) _subscribers = _subscribers.Add(ch.Writer);
        return ch.Reader;
    }

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("Already started.");

        _logger = new Thread(LogLoop) { Name = "Telemetry-Logger", Priority = ThreadPriority.AboveNormal };
        _listener = new Thread(ListenLoop) { Name = "Telemetry-Listener", Priority = ThreadPriority.AboveNormal };
        _logger.Start();
        _listener.Start();
    }

    private void Stop()
    {
        _cts.Cancel();
        _listener?.Join();
        _logQueue.CompleteAdding();
        _logger?.Join();
        foreach (var s in _subscribers) s.TryComplete();
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        _logQueue.Dispose();
    }

    private Socket CreateSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.ReceiveBufferSize = 4 * 1024 * 1024; // survive short scheduling hiccups
            s.ReceiveTimeout = 500;
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(_group, _localIp));
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private void ListenLoop()
    {
        var ct = _cts.Token;
        var buffer = new byte[65535];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = CreateSocket();
                Console.WriteLine($"Listening on {_group}:{port} via {_localIp}...");

                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = socket.Receive(buffer);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue;
                    }

                    if (n == 0) continue;

                    var packet = new TelemetryPacket(DateTime.UtcNow, [.. buffer.AsSpan(0, n)]);
                    _logQueue.Add(packet);
                    foreach (var sub in _subscribers) sub.TryWrite(packet);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Listener Error] {ex.Message}");
                ct.WaitHandle.WaitOne(1000);
            }
        }
    }

    private void LogLoop()
    {
        TelemetryPacket? pending = null;
        var finished = false;

        while (!finished)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
                using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write,
                    FileShare.Read, bufferSize: 64 * 1024);

                if (fs.Length == 0)
                    fs.Write(Encoding.UTF8.GetBytes(Header + "\n"));

                while (true)
                {
                    TelemetryPacket packet;
                    if (pending is { } retry) packet = retry;
                    else if (!_logQueue.TryTake(out packet, Timeout.Infinite))
                    {
                        finished = true;
                        break;
                    }

                    pending = packet;
                    WritePacket(fs, packet);
                    pending = null;

                    if (_logQueue.Count == 0) fs.Flush(flushToDisk: true);
                }

                fs.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Logger Error] {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private void WritePacket(FileStream fs, in TelemetryPacket p)
    {
        Utf8Formatter.TryFormat(p.TimestampUtc, _tsBuf, out var tsLen, new StandardFormat('O'));

        var data = p.Data.AsSpan();
        while (data.Length > 0 && data[^1] is (byte)'\r' or (byte)'\n')
            data = data[..^1];

        fs.Write(_tsBuf.AsSpan(0, tsLen));
        fs.WriteByte((byte)',');
        fs.Write(data);
        fs.WriteByte((byte)'\n');
    }
}


internal static class CanSatGroundStation
{

    private static readonly VectorBuilder<double> V = Vector<double>.Build;

    private const string MulticastGroup = "239.255.1.1";
    private const int Port = 5000;
    private const string LocalEthIp = "10.1.1.91";
    private const string LogFile = "telemetry.csv";

    private const double MeasStd = 0.001;
    private const double AccelStd = 0.001;
    private static readonly Vector<double> InitialPos = V.Dense(3);

    private static void Main()
    {
        // TODO: Replace with new TelemetryEngine class and methods
       using var engine = new TelemetryEngine(LocalEthIp, MulticastGroup, Port, LogFile);
       var live = engine.Subscribe();
       engine.Start();

       _ = Task.Run(async () =>
       {
           await foreach (var packet in live.ReadAllAsync())
           {
               var line = Encoding.UTF8.GetString(packet.Data);
               Console.WriteLine(line);
           }
       });

       using var quit = new ManualResetEventSlim();
       Console.CancelKeyPress += (_, e) =>
       {
           e.Cancel = true;
           quit.Set();
       };

       Console.WriteLine("Press Ctrl+C to quit");
       quit.Wait();
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