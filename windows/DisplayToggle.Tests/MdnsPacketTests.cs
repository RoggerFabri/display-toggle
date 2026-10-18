using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace DisplayToggle.Tests;

// Windows-only: the legacy-unicast mDNS codec.
public class MdnsPacketTests
{
    private const string Service = "_display-toggle._tcp.local";

    // A reply in the format macOS mDNSResponder sends for the Mac app to our legacy-unicast PTR query
    // (identifiers replaced with documentation values): PTR + SRV + TXT for "J. Example Mac", a
    // _device-info TXT, two AAAA and one A.
    private const string MacReplyHex =
        "0000840000010001000000060F5F646973706C61792D746F67676C65045F746370056C6F63616C00000C0001C00C000C00010000000A00110E4A2E204578616D706C65204D6163C00CC038002100010000000A001600000000CA330D4578616D706C652D4D61632D31C021C038001000010000000A00332769643D30463145324433432D344235412D343639372D383837372D36363535343433333232313103763D31066F733D6D61630E4A2E204578616D706C65204D61630C5F6465766963652D696E666FC01C001000010000000A00220D6D6F64656C3D4D616330302C300A6F7378766572733D39390869636F6C6F723D31C05B001C00010000000A0010FE800000000000000000000000000001C05B001C00010000000A0010FD000000000000000000000000000001C05B000100010000000A0004C0000223";

    [Fact]
    public void Parse_RealMacReply()
    {
        var (answers, ptrs) = MdnsPacket.Parse(Convert.FromHexString(MacReplyHex), Service);

        var (instance, ttl) = Assert.Single(ptrs);
        Assert.Equal(@"J\. Example Mac._display-toggle._tcp.local", instance);
        Assert.Equal(10u, ttl);

        var a = Assert.Single(answers);
        Assert.Equal(instance, a.Instance);
        Assert.Equal(51763, a.Port);
        Assert.Equal("0F1E2D3C-4B5A-4697-8877-665544332211", a.Txt["id"]);
        Assert.Equal("1", a.Txt["v"]);
        Assert.Equal("mac", a.Txt["os"]);
        Assert.Equal("mac", a.Txt["OS"]); // TXT keys are case-insensitive (RFC 6763 §6.4)
        Assert.False(a.Txt.ContainsKey("model")); // the _device-info TXT belongs to another name
        Assert.Equal(new[] { "192.0.2.35" }, a.Addresses);
        Assert.Equal("J. Example Mac", PeerDiscoveryLogic.InstanceLabel(a.Instance));
    }

    [Fact]
    public void Parse_Goodbye_ReportsTtlZeroPtr_AndNoAnswer()
    {
        var pkt = Response(an: new[] { Ptr(Service, "MAC-PROBE." + Service, ttl: 0) });
        var (answers, ptrs) = MdnsPacket.Parse(pkt, Service);
        Assert.Empty(answers);
        Assert.Equal(("MAC-PROBE." + Service, 0u), Assert.Single(ptrs));
    }

    [Fact]
    public void Parse_DuplicateTxtKey_FirstWins()
    {
        // RFC 6763 §6.4: if a key appears more than once, only the first occurrence counts.
        var txt = new byte[] { 4, (byte)'o', (byte)'s', (byte)'=', (byte)'a', 4, (byte)'o', (byte)'s', (byte)'=', (byte)'b' };
        var pkt = Response(an: new[] { Ptr(Service, "X." + Service, ttl: 120), Rr("X." + Service, 33, new byte[] { 0, 0, 0, 0, 0xCA, 0x33, 0 }), Rr("X." + Service, 16, txt) });
        var a = Assert.Single(MdnsPacket.Parse(pkt, Service).Answers);
        Assert.Equal("a", a.Txt["os"]);
    }

    [Fact]
    public void Parse_IgnoresOtherServices()
    {
        var pkt = Response(an: new[] { Ptr("_airplay._tcp.local", "TV._airplay._tcp.local", ttl: 120) });
        var (answers, ptrs) = MdnsPacket.Parse(pkt, Service);
        Assert.Empty(answers);
        Assert.Empty(ptrs);
    }

    [Fact]
    public void Parse_PtrWithoutSrv_GivesNoAnswer()
    {
        var pkt = Response(an: new[] { Ptr(Service, "X." + Service, ttl: 120) });
        var (answers, ptrs) = MdnsPacket.Parse(pkt, Service);
        Assert.Empty(answers);
        Assert.Single(ptrs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(40)]
    [InlineData(90)]
    public void Parse_TruncatedBeforeSrv_NoAnswer(int length)
    {
        var bytes = Convert.FromHexString(MacReplyHex);
        var (answers, _) = MdnsPacket.Parse(bytes.AsSpan(0, length), Service);
        Assert.Empty(answers);
    }

    [Fact]
    public void Parse_TruncatedAtEveryLength_NeverThrows_AndNeverInventsData()
    {
        var bytes = Convert.FromHexString(MacReplyHex);
        for (int len = 0; len <= bytes.Length; len++)
        {
            var (answers, _) = MdnsPacket.Parse(bytes.AsSpan(0, len), Service);
            foreach (var a in answers)
            {
                Assert.Equal(51763, a.Port);
                Assert.All(a.Addresses, ip => Assert.Equal("192.0.2.35", ip));
            }
        }
    }

    [Fact]
    public void Parse_CompressionLoop_DoesNotHang()
    {
        // header: 1 answer; name = pointer to itself at offset 12
        var pkt = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12 };
        var (answers, ptrs) = MdnsPacket.Parse(pkt, Service);
        Assert.Empty(answers);
        Assert.Empty(ptrs);
    }

    [Fact]
    public void BuildPtrQuery_IsOneQuestionForTheService()
    {
        var q = MdnsPacket.BuildPtrQuery(Service);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)));     // QDCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(6)));     // ANCOUNT
        Assert.Equal(Name(Service), q[12..^4]);
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 4))); // PTR
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 2)));  // IN, QM (no unicast bit)
    }

    // ---- tiny packet builder ------------------------------------------------

    private static byte[] Name(string name)
    {
        var ms = new MemoryStream();
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static byte[] Ptr(string owner, string target, uint ttl) => Rr(owner, 12, Name(target), ttl);

    private static byte[] Rr(string owner, ushort type, byte[] rdata, uint ttl = 120)
    {
        var ms = new MemoryStream();
        ms.Write(Name(owner));
        Span<byte> fixedPart = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[2..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..], ttl);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..], (ushort)rdata.Length);
        ms.Write(fixedPart);
        ms.Write(rdata);
        return ms.ToArray();
    }

    private static byte[] Response(byte[][] an)
    {
        var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr[2..], 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[6..], (ushort)an.Length);
        ms.Write(hdr);
        foreach (var rr in an) ms.Write(rr);
        return ms.ToArray();
    }
}
