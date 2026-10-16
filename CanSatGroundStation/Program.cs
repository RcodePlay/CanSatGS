using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CanSatGroundStation;

internal abstract class CanSatGroundStation
{
    private const string MulticastGroup = "239.255.1.1";
    private const int Port = 5000;
    private const string LocalEthIp = "10.1.1.91";

    private static void Main()
    {
        using var listener = new UdpClient();

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

    private static void ProcessPacket(byte[] data)
    {
        Console.WriteLine($"Received {data.Length} bytes: {Encoding.UTF8.GetString(data)}");
    }
}