using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using _3DKalman;
using MathNet.Numerics.LinearAlgebra;

namespace CanSatGroundStation;

public enum LogLevel { Debug, Info, Ok, Warning, Error }

public interface IAppLogger
{
    void Log(LogLevel level, string message, Exception? ex = null);
}

/// <summary>Logger that discards everything. Used when the host doesn't supply one.</summary>
public sealed class NullAppLogger : IAppLogger
{
    public static readonly NullAppLogger Instance = new();
    public void Log(LogLevel level, string message, Exception? ex = null) { }
}

public readonly record struct TelemetryPacket(DateTime TimestampUtc, byte[] Data);

/// <summary>
/// One parsed telemetry line (the fields after the system timestamp in the CSV log).
/// </summary>
public readonly record struct ProcessedPacket
{
    public const int FieldCount = 16;

    public int TimeSinceStart { get; init; }
    public int GPSTimeUTC { get; init; }
    public bool GPSFix { get; init; }
    public int SatelliteCount { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double GPSAltitude { get; init; }
    public float AltitudeBarometer { get; init; }
    public float Temperature { get; init; }
    public float Pressure { get; init; }
    public float Humidity { get; init; }
    public float AccelerationX { get; init; }
    public float AccelerationY { get; init; }
    public float AccelerationZ { get; init; }
    public float RSSI { get; init; }
    public float SNR { get; init; }

    /// <summary>Parses a raw packet. Returns false (and never throws) on malformed input.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out ProcessedPacket packet)
        => TryParse(Encoding.UTF8.GetString(data), out packet);

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, out ProcessedPacket)"/>
    public static bool TryParse(string raw, out ProcessedPacket packet)
    {
        packet = default;
        var f = raw.Trim().Split(',');
        if (f.Length < FieldCount) return false;

        var inv = CultureInfo.InvariantCulture;
        const NumberStyles ints = NumberStyles.Integer;
        const NumberStyles floats = NumberStyles.Float;

        if (!int.TryParse(f[0], ints, inv, out var timeSinceStart) ||
            !int.TryParse(f[1], ints, inv, out var gpsTime) ||
            !int.TryParse(f[3], ints, inv, out var sats) ||
            !double.TryParse(f[4], floats, inv, out var lat) ||
            !double.TryParse(f[5], floats, inv, out var lon) ||
            !double.TryParse(f[6], floats, inv, out var gpsAlt) ||
            !float.TryParse(f[7], floats, inv, out var baroAlt) ||
            !float.TryParse(f[8], floats, inv, out var temp) ||
            !float.TryParse(f[9], floats, inv, out var press) ||
            !float.TryParse(f[10], floats, inv, out var hum) ||
            !float.TryParse(f[11], floats, inv, out var ax) ||
            !float.TryParse(f[12], floats, inv, out var ay) ||
            !float.TryParse(f[13], floats, inv, out var az) ||
            !float.TryParse(f[14], floats, inv, out var rssi) ||
            !float.TryParse(f[15], floats, inv, out var snr))
            return false;

        packet = new ProcessedPacket
        {
            TimeSinceStart = timeSinceStart,
            GPSTimeUTC = gpsTime,
            GPSFix = f[2].Trim() == "1",
            SatelliteCount = sats,
            Latitude = lat,
            Longitude = lon,
            GPSAltitude = gpsAlt,
            AltitudeBarometer = baroAlt,
            Temperature = temp,
            Pressure = press,
            Humidity = hum,
            AccelerationX = ax,
            AccelerationY = ay,
            AccelerationZ = az,
            RSSI = rssi,
            SNR = snr
        };
        return true;
    }

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
            $"RSSI             : {RSSI.ToString("F0", inv)} dBm",
            $"SNR              : {SNR.ToString("F0", inv)} dB",
            "----------------------------------------");
    }
}

/// <summary>Result of a Kalman update and forecast for one packet.</summary>
/// <param name="Source">The packet that triggered the update.</param>
/// <param name="Measurement">Measured [lat, lon, alt] fed to the filter.</param>
/// <param name="PredictedTrajectory">Forecast positions, <c>StepsToPredict</c> steps of <c>DeltaT</c> ahead.</param>
public sealed record TrajectoryForecast(
    ProcessedPacket Source,
    Vector<double> Measurement,
    IReadOnlyList<Vector<double>> PredictedTrajectory);

/// <summary>
/// Telemetry collection class: listens on a UDP multicast group, logs raw packets to CSV,
/// and fans them out to subscribers.
/// </summary>
public sealed class TelemetryEngine(
    string localIp,
    string multicastGroup,
    int port,
    string logPath,
    IAppLogger? logger = null)
    : IDisposable
{
    private const string Header = "SystemTimestamp,TimeSinceStart,GPSTimeUTC,GPSFix,SatelliteCount,Latitude,Longitude,GPSAltitude,AltitudeBarometer,Temperature,Pressure,Humidity,AccelerationX,AccelerationY,AccelerationZ,RSSI,SNR";

    private readonly IAppLogger _log = logger ?? NullAppLogger.Instance;
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
    private int _stopped;

    public string LocalIp => localIp;
    public string MulticastGroup => multicastGroup;
    public int Port => port;
    public string LogPath => logPath;
    public bool IsRunning => _listener is not null && _stopped == 0;

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
        ObjectDisposedException.ThrowIf(_stopped != 0, this);
        if (_listener is not null) throw new InvalidOperationException("Already started.");

        _logger = new Thread(LogLoop) { Name = "Telemetry-Logger", Priority = ThreadPriority.AboveNormal };
        _listener = new Thread(ListenLoop) { Name = "Telemetry-Listener", Priority = ThreadPriority.AboveNormal };
        _logger.Start();
        _listener.Start();
    }

    /// <summary>Stops listening, flushes the log, and completes all subscriber channels. Idempotent.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

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
                _log.Log(LogLevel.Ok, $"Listening on {_group}:{port} via {_localIp}...");

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
                    // ReSharper disable once MethodSupportsCancellation
                    _logQueue.Add(packet);
                    foreach (var sub in _subscribers) sub.TryWrite(packet);
                }
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, "Listener error", ex);
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
                _log.Log(LogLevel.Error, "Logger error", ex);
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

/// <summary>
/// Ground station: consumes the live telemetry feed, parses packets, runs the Kalman filter,
/// and raises events for the host application (UI, console, etc.).
/// Owns the <see cref="TelemetryEngine"/> it is given: <see cref="Start"/> starts it and
/// <see cref="DisposeAsync"/> disposes it.
/// </summary>
public sealed class CanSatGroundStation(
    TelemetryEngine engine,
    double deltaT = 1.0,
    double measStd = 0.001,
    double accelStd = 0.001,
    IAppLogger? logger = null,
    int stepsToPredict = 5) : IAsyncDisposable, IDisposable
{
    private static readonly VectorBuilder<double> V = Vector<double>.Build;

    private readonly IAppLogger _log = logger ?? NullAppLogger.Instance;
    private Kalman3D? _kalman;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processing;
    private int _disposed;

    public TelemetryEngine Engine => engine;
    public double DeltaT => deltaT;
    public int StepsToPredict => stepsToPredict;
    public ProcessedPacket? LastPacket { get; private set; }
    public TrajectoryForecast? LastForecast { get; private set; }

    /// <summary>Raised for every successfully parsed packet (on a thread-pool thread).</summary>
    public event Action<ProcessedPacket>? PacketReceived;

    /// <summary>Raised after the Kalman filter was updated and a forecast computed (on a thread-pool thread).</summary>
    public event Action<TrajectoryForecast>? ForecastUpdated;

    /// <summary>Subscribes to the engine, starts it, and begins processing in the background.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_processing is not null) throw new InvalidOperationException("Already started.");

        var live = engine.Subscribe();
        engine.Start();
        _processing = Task.Run(() => ProcessLoopAsync(live, _cts.Token));
    }

    /// <summary>Completes when the live feed has ended (engine stopped) or the station was disposed.</summary>
    public Task Completion => _processing ?? Task.CompletedTask;

    private async Task ProcessLoopAsync(ChannelReader<TelemetryPacket> live, CancellationToken ct)
    {
        try
        {
            await foreach (var packet in live.ReadAllAsync(ct))
            {
                try
                {
                    Handle(packet);
                }
                catch (Exception ex)
                {
                    // A bad packet or a faulty event handler must not kill the processing loop.
                    _log.Log(LogLevel.Error, "Error while processing packet", ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Handle(TelemetryPacket packet)
    {
        if (!ProcessedPacket.TryParse(packet.Data, out var telemetry))
        {
            _log.Log(LogLevel.Warning,
                $"Could not parse packet: {Encoding.UTF8.GetString(packet.Data).Trim()}");
            return;
        }

        LastPacket = telemetry;
        _log.Log(LogLevel.Debug, $"Received telemetry packet:{Environment.NewLine}{telemetry}");
        PacketReceived?.Invoke(telemetry);

        // Without a fix the GPS position is meaningless; don't feed it to the filter.
        if (!telemetry.GPSFix) return;

        var measurement = V.Dense([telemetry.Latitude, telemetry.Longitude, telemetry.GPSAltitude]);
        if (_kalman is null)
        {
            _kalman = new Kalman3D(measStd, accelStd, measurement);
        }
        else
        {
            _kalman.Update(measurement);
        }
        var predicted = _kalman.Forecast(stepsToPredict, deltaT);

        var forecast = new TrajectoryForecast(telemetry, measurement, predicted);
        LastForecast = forecast;
        ForecastUpdated?.Invoke(forecast);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        engine.Dispose();      // completes the subscriber channel, so the loop ends by itself
        _cts.CancelAfter(TimeSpan.FromSeconds(2)); // safety net if the loop is stuck in a handler
        if (_processing is not null)
        {
            try { await _processing.ConfigureAwait(false); }
            catch (Exception ex) { _log.Log(LogLevel.Error, "Processing task faulted", ex); }
        }
        _cts.Dispose();
    }
}