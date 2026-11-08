using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Oplus.Core;

/// <summary>One advertised printer (DNS-SD instance).</summary>
public sealed class MdnsService
{
    public required string Instance;                 // "Kitchen printer"
    public string Type = Defaults.ServiceType;       // "_ipp._tcp"
    public string[] Subtypes = { Defaults.SubType }; // "_universal"
    public int Port;
    public List<KeyValuePair<string, string>> Txt = new();
    public string FullName => $"{Instance}.{Type}.local";
}

/// <summary>
/// A small multicast DNS responder (RFC 6762/6763) so iPhones and iPads find the shared printers.
/// No Bonjour install is needed.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    const int Port = 5353;
    const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33, TypeAny = 255, TypeAAAA = 28, TypeNsec = 47;

    readonly object _gate = new();
    readonly List<MdnsService> _services = new();
    Socket? _sock;
    Thread? _thread;
    volatile bool _running;
    public string HostName { get; }
    public event Action<string>? Log;

    public MdnsResponder()
    {
        var label = new StringBuilder();
        foreach (var c in Environment.MachineName) label.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        HostName = (label.Length == 0 ? "windows" : label.ToString()) + "-oplus.local";
    }

    public void Start()
    {
        if (_running) return;
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        s.Bind(new IPEndPoint(IPAddress.Any, Port));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        foreach (var (index, _) in Interfaces())
        {
            try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, index)); }
            catch (Exception ex) { Log?.Invoke($"mDNS: cannot join on interface {index}: {ex.Message}"); }
        }
        _sock = s;
        _running = true;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "mDNS" };
        _thread.Start();
    }

    /// <summary>Replaces the advertised printers; announces new ones and says goodbye to removed ones.</summary>
    public void SetServices(IEnumerable<MdnsService> services)
    {
        List<MdnsService> old;
        lock (_gate) { old = _services.ToList(); _services.Clear(); _services.AddRange(services); }
        if (!_running) return;
        var gone = old.Where(o => !_services.Any(n => n.FullName == o.FullName)).ToList();
        if (gone.Count > 0) Announce(gone, goodbye: true);
        var list = _services.ToList();
        Announce(list, false);
        // announce again a second later (RFC 6762 §8.3)
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(1000); if (_running) Announce(list, false); });
    }

    void Announce(List<MdnsService> list, bool goodbye)
    {
        if (list.Count == 0) return;
        foreach (var (index, addrs) in Interfaces())
        {
            var w = new DnsWriter(0, 0x8400);
            foreach (var svc in list) AddServiceRecords(w, svc, addrs, goodbye, answersOnly: true);
            if (!goodbye) foreach (var a in addrs) w.AddA(HostName, a, 120);
            Send(w.ToBytes(), index, new IPEndPoint(Group, Port));
        }
    }

    void ReceiveLoop()
    {
        var buf = new byte[9000];
        while (_running)
        {
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var flags = SocketFlags.None;
                int n = _sock!.ReceiveMessageFrom(buf, 0, buf.Length, ref flags, ref from, out var info);
                HandlePacket(buf.AsSpan(0, n).ToArray(), (IPEndPoint)from, info.Interface);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (!_running) return; }
            catch (Exception ex) { Log?.Invoke("mDNS: " + ex.Message); }
        }
    }

    void HandlePacket(byte[] pkt, IPEndPoint from, int ifIndex)
    {
        if (pkt.Length < 12) return;
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(pkt);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(2));
        if ((flags & 0x8000) != 0) return; // a response, not a query
        int qd = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(4));
        int p = 12;
        var questions = new List<(string name, ushort type, bool unicast)>();
        for (int i = 0; i < qd && p < pkt.Length; i++)
        {
            string name = DnsWriter.ReadName(pkt, ref p);
            if (p + 4 > pkt.Length) return;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(p));
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(p + 2));
            p += 4;
            questions.Add((name, type, (cls & 0x8000) != 0));
        }
        if (questions.Count == 0) return;

        var addrs = Interfaces().FirstOrDefault(x => x.index == ifIndex).addrs;
        if (addrs == null || addrs.Count == 0) addrs = Interfaces().SelectMany(x => x.addrs).ToList();
        bool legacy = from.Port != Port;   // one-shot query from a plain DNS resolver
        var (bytes, unicast) = Answer(questions, id, legacy, addrs);
        if (bytes == null) return;
        if (unicast) Send(bytes, ifIndex, from);
        if (!legacy) Send(bytes, ifIndex, new IPEndPoint(Group, Port));
    }

    /// <summary>Parses a query packet and returns the answer (for tests).</summary>
    public byte[]? AnswerPacket(byte[] pkt, List<IPAddress> addrs)
    {
        int p = 12; var qs = new List<(string, ushort, bool)>();
        int qd = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(4));
        for (int i = 0; i < qd; i++)
        {
            string n = DnsWriter.ReadName(pkt, ref p);
            ushort t = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(p)); ushort c = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(p + 2)); p += 4;
            qs.Add((n, t, (c & 0x8000) != 0));
        }
        return Answer(qs, 0, false, addrs).bytes;
    }

    (byte[]? bytes, bool unicast) Answer(List<(string name, ushort type, bool unicast)> questions, ushort id, bool legacy, List<IPAddress> addrs)
    {
        var w = new DnsWriter(legacy ? id : (ushort)0, 0x8400);
        if (legacy) foreach (var q in questions) w.AddQuestion(q.name, q.type);
        bool any = false, unicast = legacy;
        List<MdnsService> services;
        lock (_gate) services = _services.ToList();

        foreach (var q in questions)
        {
            string qn = q.name.TrimEnd('.');
            uint ttlShort = legacy ? 10u : 120u, ttlLong = legacy ? 10u : 4500u;
            if (q.unicast) unicast = true;
            if (qn.Equals("_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase) && (q.type == TypePtr || q.type == TypeAny))
            {
                foreach (var t in services.Select(s => s.Type).Distinct())
                { w.AddPtr("_services._dns-sd._udp.local", t + ".local", ttlLong); any = true; }
            }
            foreach (var svc in services)
            {
                bool typeMatch = qn.Equals(svc.Type + ".local", StringComparison.OrdinalIgnoreCase) ||
                                 svc.Subtypes.Any(st => qn.Equals($"{st}._sub.{svc.Type}.local", StringComparison.OrdinalIgnoreCase));
                if (typeMatch && (q.type == TypePtr || q.type == TypeAny))
                {
                    w.AddPtr(qn, svc.FullName, ttlLong);
                    w.Additional(() => AddServiceRecords(w, svc, addrs, false, answersOnly: false, ttlScale: legacy));
                    any = true;
                }
                if (qn.Equals(svc.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    if (q.type == TypeSrv || q.type == TypeAny) { w.AddSrv(svc.FullName, HostName, svc.Port, ttlShort); any = true; }
                    if (q.type == TypeTxt || q.type == TypeAny) { w.AddTxt(svc.FullName, svc.Txt, ttlLong); any = true; }
                    if (q.type == TypeSrv) w.Additional(() => { foreach (var a in addrs) w.AddA(HostName, a, ttlShort); });
                }
            }
            if (qn.Equals(HostName, StringComparison.OrdinalIgnoreCase))
            {
                if (q.type == TypeA || q.type == TypeAny) { foreach (var a in addrs) w.AddA(HostName, a, ttlShort); any = true; }
                else if (q.type == TypeAAAA) { w.AddNsecA(HostName, ttlShort); any = true; }
            }
        }
        return any ? (w.ToBytes(), unicast) : (null, false);
    }

    void AddServiceRecords(DnsWriter w, MdnsService svc, List<IPAddress> addrs, bool goodbye, bool answersOnly, bool ttlScale = false)
    {
        uint ttlLong = goodbye ? 0 : (ttlScale ? 10u : 4500u), ttlShort = goodbye ? 0 : (ttlScale ? 10u : 120u);
        if (answersOnly)
        {
            w.AddPtr(svc.Type + ".local", svc.FullName, ttlLong);
            foreach (var st in svc.Subtypes) w.AddPtr($"{st}._sub.{svc.Type}.local", svc.FullName, ttlLong);
            if (!goodbye) w.AddPtr("_services._dns-sd._udp.local", svc.Type + ".local", ttlLong);
        }
        w.AddSrv(svc.FullName, HostName, svc.Port, ttlShort);
        w.AddTxt(svc.FullName, svc.Txt, ttlLong);
        if (!answersOnly) foreach (var a in addrs) w.AddA(HostName, a, ttlShort);
    }

    void Send(byte[] data, int ifIndex, IPEndPoint to)
    {
        var s = _sock;
        if (s == null) return;
        try
        {
            lock (_gate)
            {
                if (ifIndex > 0)
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(ifIndex));
                s.SendTo(data, to);
            }
        }
        catch (Exception ex) { Log?.Invoke("mDNS send: " + ex.Message); }
    }

    /// <summary>Network interfaces that are up and have IPv4 addresses.</summary>
    static List<(int index, List<IPAddress> addrs)> Interfaces()
    {
        var list = new List<(int, List<IPAddress>)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!ni.SupportsMulticast) continue;
                var props = ni.GetIPProperties();
                IPv4InterfaceProperties? v4;
                try { v4 = props.GetIPv4Properties(); } catch { continue; }
                if (v4 == null) continue;
                var addrs = props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                                 .Select(u => u.Address).ToList();
                if (addrs.Count > 0) list.Add((v4.Index, addrs));
            }
        }
        catch { }
        return list;
    }

    public void Dispose()
    {
        if (!_running) return;
        List<MdnsService> list; lock (_gate) list = _services.ToList();
        try { Announce(list, goodbye: true); } catch { }
        _running = false;
        try { _sock?.Close(); } catch { }
        _sock = null;
    }
}

/// <summary>Builds a DNS message (answers first, then additional records, no name compression).</summary>
sealed class DnsWriter
{
    readonly ushort _id, _flags;
    readonly List<byte[]> _questions = new(), _answers = new(), _additional = new();
    readonly HashSet<string> _seen = new();
    bool _inAdditional;

    public DnsWriter(ushort id, ushort flags) { _id = id; _flags = flags; }

    public void Additional(Action add) { _inAdditional = true; try { add(); } finally { _inAdditional = false; } }

    public void AddQuestion(string name, ushort type)
    {
        var ms = new MemoryStream(); WriteName(ms, name); U16(ms, type); U16(ms, 1); _questions.Add(ms.ToArray());
    }

    void Record(string name, ushort type, bool unique, uint ttl, byte[] rdata)
    {
        var ms = new MemoryStream();
        WriteName(ms, name); U16(ms, type); U16(ms, (ushort)(unique ? 0x8001 : 0x0001));
        U32(ms, ttl); U16(ms, (ushort)rdata.Length); ms.Write(rdata);
        var rec = ms.ToArray();
        string key = Convert.ToHexString(rec);
        if (!_seen.Add(key)) return;
        (_inAdditional ? _additional : _answers).Add(rec);
    }

    public void AddPtr(string name, string target, uint ttl)
    { var ms = new MemoryStream(); WriteName(ms, target); Record(name, 12, false, ttl, ms.ToArray()); }

    public void AddSrv(string name, string host, int port, uint ttl)
    {
        var ms = new MemoryStream(); U16(ms, 0); U16(ms, 0); U16(ms, (ushort)port); WriteName(ms, host);
        Record(name, 33, true, ttl, ms.ToArray());
    }

    public void AddTxt(string name, List<KeyValuePair<string, string>> txt, uint ttl)
    {
        var ms = new MemoryStream();
        foreach (var kv in txt)
        {
            var b = Encoding.UTF8.GetBytes(kv.Key + "=" + kv.Value);
            if (b.Length > 255) b = b[..255];
            ms.WriteByte((byte)b.Length); ms.Write(b);
        }
        if (ms.Length == 0) ms.WriteByte(0);
        Record(name, 16, true, ttl, ms.ToArray());
    }

    public void AddA(string name, IPAddress a, uint ttl) => Record(name, 1, true, ttl, a.GetAddressBytes());

    /// <summary>NSEC saying the host has only A records (so clients stop waiting for IPv6).</summary>
    public void AddNsecA(string name, uint ttl)
    {
        var ms = new MemoryStream(); WriteName(ms, name); ms.WriteByte(0); ms.WriteByte(1); ms.WriteByte(0x40);
        Record(name, 47, true, ttl, ms.ToArray());
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        U16(ms, _id); U16(ms, _flags);
        U16(ms, (ushort)_questions.Count); U16(ms, (ushort)_answers.Count); U16(ms, 0); U16(ms, (ushort)_additional.Count);
        foreach (var r in _questions) ms.Write(r);
        foreach (var r in _answers) ms.Write(r);
        foreach (var r in _additional) ms.Write(r);
        return ms.ToArray();
    }

    static void WriteName(Stream s, string name)
    {
        // The instance part may contain dots; split only the trailing service labels.
        foreach (var label in SplitName(name.TrimEnd('.')))
        {
            var b = Encoding.UTF8.GetBytes(Fit(label, 63));
            s.WriteByte((byte)b.Length); s.Write(b);
        }
        s.WriteByte(0);
    }

    /// <summary>Shortens text to at most maxBytes of UTF-8 without cutting a character in half.</summary>
    public static string Fit(string s, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        var sb = new StringBuilder(); int n = 0;
        while (e.MoveNext())
        {
            var el = e.GetTextElement(); int c = Encoding.UTF8.GetByteCount(el);
            if (n + c > maxBytes) break;
            sb.Append(el); n += c;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>"My.Printer._ipp._tcp.local" →["My.Printer","_ipp","_tcp","local"].</summary>
    static IEnumerable<string> SplitName(string name)
    {
        var parts = name.Split('.');
        int firstService = Array.FindIndex(parts, p => p.StartsWith('_'));
        if (firstService > 1) // instance label with dots
        {
            yield return string.Join(".", parts[..firstService]);
            foreach (var p in parts[firstService..]) yield return p;
        }
        else foreach (var p in parts) yield return p;
    }

    public static string ReadName(byte[] pkt, ref int p)
    {
        var labels = new List<string>();
        int pos = p; bool jumped = false; int hops = 0;
        while (pos < pkt.Length)
        {
            int len = pkt[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= pkt.Length || ++hops > 32) break;
                int ptr = ((len & 0x3F) << 8) | pkt[pos + 1];
                if (!jumped) p = pos + 2;
                jumped = true; pos = ptr; continue;
            }
            if (pos + 1 + len > pkt.Length) break;
            labels.Add(Encoding.UTF8.GetString(pkt, pos + 1, len));
            pos += 1 + len;
        }
        if (!jumped) p = pos;
        return string.Join(".", labels);
    }

    static void U16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void U32(Stream s, uint v) { U16(s, (ushort)(v >> 16)); U16(s, (ushort)v); }
}
