using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

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
    private const string MulticastGroup = "239.255.1.1";
    private const int Port = 5000;
    private const string LocalEthIp = "10.1.1.91";
    
    private readonly string LogFilePath;
    private readonly Channel<byte[]> DataChannel; 

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

    // TODO: Packet processing class + methods
    private static void ProcessPacket(byte[] data)
    {
        Console.WriteLine($"Received {data.Length} bytes: {Encoding.UTF8.GetString(data)}");
    }
}