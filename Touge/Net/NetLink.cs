using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Touge.Net;

/// <summary>
///     Simulated bad network on one link (--net-sim latency[:loss[:jitter]]): every packet in either direction is dropped with
///     probability <see cref="Loss"/>, the rest delayed by <see cref="LatencyMs"/> ± <see cref="JitterMs"/> (so they can arrive
///     out of order). Applied on both send and receive: one simulated peer sees twice the latency as round trip.
/// </summary>
public sealed record NetSim(float LatencyMs, float Loss = 0, float JitterMs = 0)
{
    /// <summary>"80", "80:0.05", "80:5%:10" → latency ms, loss (fraction or percent), jitter ms.</summary>
    public static NetSim Parse(string s)
    {
        var p = s.Split(':');
        float F(int i) => i < p.Length ? float.Parse(p[i].TrimEnd('%'), CultureInfo.InvariantCulture) / (i == 1 && p[i].EndsWith('%') ? 100 : 1) : 0;
        return new NetSim(F(0), F(1), F(2));
    }
}

/// <summary>
///     One UDP socket, non-blocking, polled from the game loop (no threads): <see cref="Send"/>, <see cref="Broadcast"/>
///     (255.255.255.255 and every interface's broadcast address, for LAN discovery) and <see cref="Receive"/>. Optional
///     <see cref="NetSim"/> delays/drops packets; counters for the logs.
/// </summary>
public sealed class NetLink : IDisposable
{
    private readonly Socket _socket;
    private readonly Func<double> _clock;
    private readonly NetSim? _sim;
    private readonly Random _rng;
    private readonly List<(double Due, byte[] Data, IPEndPoint Peer)> _out = [], _in = [];
    private readonly byte[] _buffer = new byte[2048];

    public int Port { get; }
    public long Sent, Received, SimDropped, BytesSent, BytesReceived;

    /// <param name="port">Local port, 0 = any free one (clients).</param>
    public NetLink(int port, Func<double> clock, NetSim? sim = null, int seed = 1)
    {
        (_clock, _sim, _rng) = (clock, sim, new Random(seed));
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false, EnableBroadcast = true };
        if (OperatingSystem.IsWindows()) _socket.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0], null); // no ICMP "port unreachable" resets
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public void Send(byte[] packet, IPEndPoint to)
    {
        if (_sim == null) Transmit(packet, to);
        else if (_rng.NextSingle() >= _sim.Loss) _out.Add((_clock() + Delay(), packet, to));
        else SimDropped++;
    }

    /// <summary>To every IPv4 network this machine is on (limited broadcast plus each interface's directed broadcast), on <paramref name="port"/>.</summary>
    public void Broadcast(byte[] packet, int port)
    {
        foreach (var address in BroadcastAddresses()) Send(packet, new IPEndPoint(address, port));
    }

    private double Delay() => (_sim!.LatencyMs + _sim.JitterMs * (2 * _rng.NextSingle() - 1)) / 1000.0;

    private void Transmit(byte[] packet, IPEndPoint to)
    {
        try
        {
            _socket.SendTo(packet, to);
            Sent++;
            BytesSent += packet.Length;
        }
        catch (SocketException)
        {
            // unreachable network/host: UDP is best effort, the session's timeouts handle a peer that is gone
        }
    }

    /// <summary>Next packet that has arrived (after the simulated delay), false when there is none.</summary>
    public bool Receive(out byte[] packet, out IPEndPoint from)
    {
        var now = _clock();
        for (var i = _out.Count - 1; i >= 0; i--)
            if (_out[i].Due <= now)
            {
                Transmit(_out[i].Data, _out[i].Peer);
                _out.RemoveAt(i);
            }
        while (true)
        {
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                if (_socket.Available == 0) break;
                n = _socket.ReceiveFrom(_buffer, ref ep);
            }
            catch (SocketException)
            {
                break;
            }
            Received++;
            BytesReceived += n;
            var data = _buffer.AsSpan(0, n).ToArray();
            if (_sim == null) _in.Add((now, data, (IPEndPoint)ep));
            else if (_rng.NextSingle() >= _sim.Loss) _in.Add((now + Delay(), data, (IPEndPoint)ep));
            else SimDropped++;
        }
        // earliest due first (jitter may reorder)
        var best = -1;
        for (var i = 0; i < _in.Count; i++)
            if (_in[i].Due <= now && (best < 0 || _in[i].Due < _in[best].Due)) best = i;
        if (best < 0)
        {
            (packet, from) = ([], null!);
            return false;
        }
        (_, packet, from) = _in[best];
        _in.RemoveAt(best);
        return true;
    }

    /// <summary>255.255.255.255 and the directed broadcast address of every IPv4 interface that is up.</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var all = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    var ip = u.Address.GetAddressBytes();
                    var mask = u.IPv4Mask.GetAddressBytes();
                    for (var i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                    all.Add(new IPAddress(ip));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // interface list not available: the limited broadcast still goes out
        }
        return all;
    }

    /// <summary>This machine's IPv4 addresses on the LAN (lobby: "tell your friends this address").</summary>
    public static IEnumerable<IPAddress> LocalAddresses()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.")),
            ];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>"host:port" or "host" (default port); host as IPv4 address or DNS name. Null when it cannot be resolved.</summary>
    public static IPEndPoint? Resolve(string text, int defaultPort)
    {
        var s = text.Trim();
        var port = defaultPort;
        var colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out var p) && p is > 0 and < 65536) (s, port) = (s[..colon], p);
        if (IPAddress.TryParse(s, out var ip)) return new IPEndPoint(ip, port);
        try
        {
            var a = Dns.GetHostAddresses(s).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            return a == null ? null : new IPEndPoint(a, port);
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Sends what the simulated delay still holds (a leaving peer's goodbye), then closes.</summary>
    public void Dispose()
    {
        foreach (var (_, data, peer) in _out) Transmit(data, peer);
        _out.Clear();
        _socket.Dispose();
    }
}
