using System.Buffers.Binary;
using System.Text;

namespace Oplus.Core;

/// <summary>IPP value and group tags (RFC 8010).</summary>
public static class IppTag
{
    public const byte OperationGroup = 0x01, JobGroup = 0x02, End = 0x03, PrinterGroup = 0x04, UnsupportedGroup = 0x05;
    public const byte Unsupported = 0x10, Unknown = 0x12, NoValue = 0x13;
    public const byte Integer = 0x21, Boolean = 0x22, Enum = 0x23;
    public const byte OctetString = 0x30, DateTime = 0x31, Resolution = 0x32, Range = 0x33,
        BeginCollection = 0x34, TextWithLanguage = 0x35, NameWithLanguage = 0x36, EndCollection = 0x37;
    public const byte Text = 0x41, Name = 0x42, Keyword = 0x44, Uri = 0x45, UriScheme = 0x46,
        Charset = 0x47, Language = 0x48, MimeType = 0x49, MemberName = 0x4A;
}

public static class IppOp
{
    public const short PrintJob = 0x0002, PrintUri = 0x0003, ValidateJob = 0x0004, CreateJob = 0x0005,
        SendDocument = 0x0006, CancelJob = 0x0008, GetJobAttributes = 0x0009, GetJobs = 0x000A,
        GetPrinterAttributes = 0x000B, IdentifyPrinter = 0x003C;
}

public static class IppStatus
{
    public const short Ok = 0x0000, OkIgnoredOrSubstituted = 0x0001,
        BadRequest = 0x0400, Forbidden = 0x0401, NotAuthenticated = 0x0402, NotPossible = 0x0404,
        NotFound = 0x0406, AttributesOrValues = 0x040B, DocumentFormatNotSupported = 0x040A,
        CharsetNotSupported = 0x040D, InternalError = 0x0500, OperationNotSupported = 0x0501,
        Busy = 0x0507, VersionNotSupported = 0x0503;
}

/// <summary>One value of an attribute: a tag plus raw bytes, or a nested collection.</summary>
public sealed class IppValue
{
    public byte Tag;
    public byte[] Data = Array.Empty<byte>();
    public List<IppAttribute>? Members; // for collections

    public string AsString => Encoding.UTF8.GetString(Data);
    public int AsInt => Data.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(Data) : (Data.Length == 1 ? Data[0] : 0);

    public static IppValue Str(byte tag, string s) => new() { Tag = tag, Data = Encoding.UTF8.GetBytes(s) };
    public static IppValue Int(byte tag, int v)
    {
        var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return new() { Tag = tag, Data = b };
    }
    public static IppValue Bool(bool v) => new() { Tag = IppTag.Boolean, Data = new[] { (byte)(v ? 1 : 0) } };
    public static IppValue Res(int x, int y)
    {
        var b = new byte[9]; BinaryPrimitives.WriteInt32BigEndian(b, x); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), y); b[8] = 3;
        return new() { Tag = IppTag.Resolution, Data = b };
    }
    public static IppValue RangeOf(int lo, int hi)
    {
        var b = new byte[8]; BinaryPrimitives.WriteInt32BigEndian(b, lo); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), hi);
        return new() { Tag = IppTag.Range, Data = b };
    }
    public static IppValue Collection(params IppAttribute[] members) => new() { Tag = IppTag.BeginCollection, Members = members.ToList() };
    public static IppValue Date(DateTime t)
    {
        var u = t.ToUniversalTime();
        var b = new byte[11];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)u.Year);
        b[2] = (byte)u.Month; b[3] = (byte)u.Day; b[4] = (byte)u.Hour; b[5] = (byte)u.Minute; b[6] = (byte)u.Second;
        b[7] = (byte)(u.Millisecond / 100); b[8] = (byte)'+'; b[9] = 0; b[10] = 0;
        return new() { Tag = IppTag.DateTime, Data = b };
    }
}

public sealed class IppAttribute
{
    public string Name;
    public List<IppValue> Values = new();
    public IppAttribute(string name) { Name = name; }
    public IppAttribute(string name, params IppValue[] values) { Name = name; Values.AddRange(values); }
    public string? FirstString => Values.Count > 0 ? Values[0].AsString : null;
    public int? FirstInt => Values.Count > 0 ? Values[0].AsInt : null;
}

public sealed class IppGroup
{
    public byte Tag;
    public List<IppAttribute> Attributes = new();
    public IppGroup(byte tag) { Tag = tag; }
    public IppAttribute? this[string name] => Attributes.FirstOrDefault(a => a.Name == name);

    public IppGroup Add(string name, params IppValue[] values) { Attributes.Add(new IppAttribute(name, values)); return this; }
    public IppGroup Keyword(string name, params string[] v) => Add(name, v.Select(s => IppValue.Str(IppTag.Keyword, s)).ToArray());
    public IppGroup Text(string name, string v) => Add(name, IppValue.Str(IppTag.Text, v));
    public IppGroup NameAttr(string name, string v) => Add(name, IppValue.Str(IppTag.Name, v));
    public IppGroup Uri(string name, params string[] v) => Add(name, v.Select(s => IppValue.Str(IppTag.Uri, s)).ToArray());
    public IppGroup Mime(string name, params string[] v) => Add(name, v.Select(s => IppValue.Str(IppTag.MimeType, s)).ToArray());
    public IppGroup Integer(string name, params int[] v) => Add(name, v.Select(i => IppValue.Int(IppTag.Integer, i)).ToArray());
    public IppGroup Enum(string name, params int[] v) => Add(name, v.Select(i => IppValue.Int(IppTag.Enum, i)).ToArray());
    public IppGroup Bool(string name, bool v) => Add(name, IppValue.Bool(v));
}

/// <summary>An IPP request or response.</summary>
public sealed class IppMessage
{
    public byte Major = 1, Minor = 1;
    public short Code;       // operation-id or status-code
    public int RequestId;
    public List<IppGroup> Groups = new();

    public IppGroup? Group(byte tag) => Groups.FirstOrDefault(g => g.Tag == tag);
    public IppGroup NewGroup(byte tag) { var g = new IppGroup(tag); Groups.Add(g); return g; }
    public IppAttribute? Find(string name) => Groups.SelectMany(g => g.Attributes).FirstOrDefault(a => a.Name == name);

    /// <summary>Parses an IPP message; returns the offset where document data starts.</summary>
    public static IppMessage Parse(byte[] buf, out int dataOffset)
    {
        var m = new IppMessage();
        if (buf.Length < 9) throw new FormatException("IPP message too short");
        m.Major = buf[0]; m.Minor = buf[1];
        m.Code = BinaryPrimitives.ReadInt16BigEndian(buf.AsSpan(2));
        m.RequestId = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(4));
        int p = 8;
        IppGroup? group = null;
        IppAttribute? last = null;
        while (p < buf.Length)
        {
            byte tag = buf[p++];
            if (tag == IppTag.End) { dataOffset = p; return m; }
            if (tag < 0x10) { group = m.NewGroup(tag); last = null; continue; }
            if (group == null) throw new FormatException("value before group");
            var (name, data) = ReadItem(buf, ref p);
            var val = new IppValue { Tag = tag, Data = data };
            if (tag == IppTag.BeginCollection) val.Members = ReadCollection(buf, ref p);
            if (name.Length > 0) { last = new IppAttribute(name); group.Attributes.Add(last); }
            last?.Values.Add(val);
        }
        dataOffset = buf.Length;
        return m;
    }

    static (string name, byte[] data) ReadItem(byte[] buf, ref int p)
    {
        int nl = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(p)); p += 2;
        string name = Encoding.UTF8.GetString(buf, p, nl); p += nl;
        int vl = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(p)); p += 2;
        var data = buf.AsSpan(p, vl).ToArray(); p += vl;
        return (name, data);
    }

    /// <summary>Reads collection members after a begCollection value, up to and including endCollection.</summary>
    static List<IppAttribute> ReadCollection(byte[] buf, ref int p)
    {
        var members = new List<IppAttribute>();
        IppAttribute? cur = null;
        while (p < buf.Length)
        {
            byte tag = buf[p++];
            var (_, data) = ReadItem(buf, ref p);
            if (tag == IppTag.EndCollection) break;
            if (tag == IppTag.MemberName) { cur = new IppAttribute(Encoding.UTF8.GetString(data)); members.Add(cur); continue; }
            var v = new IppValue { Tag = tag, Data = data };
            if (tag == IppTag.BeginCollection) v.Members = ReadCollection(buf, ref p);
            cur?.Values.Add(v);
        }
        return members;
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        ms.WriteByte(Major); ms.WriteByte(Minor);
        WriteU16(ms, (ushort)Code); WriteI32(ms, RequestId);
        foreach (var g in Groups)
        {
            ms.WriteByte(g.Tag);
            foreach (var a in g.Attributes) WriteAttr(ms, a.Name, a.Values);
        }
        ms.WriteByte(IppTag.End);
        return ms.ToArray();
    }

    static void WriteAttr(Stream s, string name, List<IppValue> values)
    {
        if (values.Count == 0) { WriteRaw(s, IppTag.NoValue, name, Array.Empty<byte>()); return; }
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            string n = i == 0 ? name : "";
            if (v.Tag == IppTag.BeginCollection)
            {
                WriteRaw(s, IppTag.BeginCollection, n, Array.Empty<byte>());
                foreach (var m in v.Members ?? new())
                {
                    WriteRaw(s, IppTag.MemberName, "", Encoding.UTF8.GetBytes(m.Name));
                    WriteAttr(s, "", m.Values);
                }
                WriteRaw(s, IppTag.EndCollection, "", Array.Empty<byte>());
            }
            else WriteRaw(s, v.Tag, n, v.Data);
        }
    }

    static void WriteRaw(Stream s, byte tag, string name, byte[] data)
    {
        s.WriteByte(tag);
        var nb = Encoding.UTF8.GetBytes(name);
        WriteU16(s, (ushort)nb.Length); s.Write(nb);
        WriteU16(s, (ushort)data.Length); s.Write(data);
    }
    static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void WriteI32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
}
