using System.Buffers.Binary;
using System.Security.Cryptography;
using Zapret.Core;
using Zapret.TelegramProxy.MtProto;
using Zapret.TelegramProxy.Security;
namespace Zapret.TelegramProxy.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Aes256CtrMatchesNistSp80038aAcrossPartialBlocks()
    {
        using var ctr = new AesCtr(Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4"), Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"));
        var bytes = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
        ctr.Transform(bytes.AsSpan(0, 3)); ctr.Transform(bytes.AsSpan(3, 17)); ctr.Transform(bytes.AsSpan(20));
        Assert.Equal("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5", Convert.ToHexStringLower(bytes));
    }
    [Theory]
    [InlineData(2, 0xddddddddU, 2, false, false)]
    [InlineData(-203, 0xeeeeeeeeU, 203, true, false)]
    [InlineData(-10003, 0xefefefefU, 3, true, true)]
    public void ParsesIndependentClientInit(int id, uint tag, int dc, bool media, bool test)
    {
        var secret = new byte[16]; var init = ClientInit((short)id, tag, secret);
        var result = ObfuscatedTransport.Parse(init, secret);
        Assert.Equal(dc, result.Dc); Assert.Equal(media, result.Media); Assert.Equal(test, result.Test); Assert.Equal(tag, result.Protocol);
        var relay = ObfuscatedTransport.RelayInit(result);
        var decoded = Transform(relay, relay[8..40], relay[40..56]);
        Assert.Equal(tag, BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(56)));
        Assert.Equal(media ? -dc : dc, BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(60)));
    }
    [Theory]
    [InlineData(0)] [InlineData(6)] [InlineData(-32768)] [InlineData(10004)]
    public void UnknownDcRejected(short dc) => Assert.Throws<InvalidDataException>(() => ObfuscatedTransport.Parse(ClientInit(dc, ObfuscatedTransport.Padded, new byte[16]), new byte[16]));
    [Fact]
    public void BadInitAndWrongSecretRejected()
    {
        Assert.Throws<InvalidDataException>(() => ObfuscatedTransport.Parse(new byte[63], new byte[16]));
        Assert.Throws<InvalidDataException>(() => ObfuscatedTransport.Parse(ClientInit(2, 123, new byte[16]), new byte[16]));
        Assert.Throws<InvalidDataException>(() => ObfuscatedTransport.Parse(ClientInit(2, ObfuscatedTransport.Padded, new byte[16]), RandomNumberGenerator.GetBytes(16)));
    }
    [Theory]
    [InlineData(0xefefefefU)] [InlineData(0xeeeeeeeeU)] [InlineData(0xddddddddU)]
    public async Task ArbitrarilyFragmentedAndCoalescedPacketsArePreserved(uint protocol)
    {
        foreach (var length in new[] { 16, 512, 65536 })
        {
            var payload = Enumerable.Repeat((byte)0x37, length).ToArray();
            var first = PacketFraming.HttpReply(payload, protocol); first[protocol == ObfuscatedTransport.Abridged ? 0 : 3] |= 0x80;
            var second = PacketFraming.HttpReply(new byte[8], protocol); var clear = first.Concat(second).ToArray();
            var encrypted = Transform(clear, new byte[32], new byte[16]);
            await using var fragmented = new FragmentedStream(encrypted, 3); using var cipher = new AesCtr(new byte[32], new byte[16]);
            Assert.Equal(first, await PacketFraming.ReadAsync(fragmented, cipher, protocol, 100000, TestContext.Current.CancellationToken));
            Assert.Equal(second, await PacketFraming.ReadAsync(fragmented, cipher, protocol, 100000, TestContext.Current.CancellationToken));
            Assert.Null(await PacketFraming.ReadAsync(fragmented, cipher, protocol, 100000, TestContext.Current.CancellationToken));
        }
    }
    [Fact]
    public async Task OversizedAndTruncatedFramesRejected()
    {
        using var cipher = new AesCtr(new byte[32], new byte[16]);
        await using var stream = new MemoryStream(Transform(new byte[] { 255, 255, 255, 127 }, new byte[32], new byte[16]));
        await Assert.ThrowsAsync<InvalidDataException>(() => PacketFraming.ReadAsync(stream, cipher, ObfuscatedTransport.Padded, 4096, TestContext.Current.CancellationToken));
        using var other = new AesCtr(new byte[32], new byte[16]); await using var tail = new MemoryStream(Transform(new byte[] { 16, 0, 0, 0, 1 }, new byte[32], new byte[16]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => PacketFraming.ReadAsync(tail, other, ObfuscatedTransport.Intermediate, 4096, TestContext.Current.CancellationToken));
    }
    internal static byte[] ClientInit(short dc, uint protocol, byte[] secret)
    {
        var init = Enumerable.Range(1, 64).Select(x => (byte)x).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(init.AsSpan(56), protocol); BinaryPrimitives.WriteInt16LittleEndian(init.AsSpan(60), dc);
        var encrypted = Transform(init, SHA256.HashData(init[8..40].Concat(secret).ToArray()), init[40..56]); encrypted.AsSpan(56).CopyTo(init.AsSpan(56)); return init;
    }
    // Independent test oracle: ECB on explicitly numbered counter blocks.
    internal static byte[] Transform(byte[] input, byte[] key, byte[] iv, int skip = 0)
    {
        using var aes = Aes.Create(); aes.Key = key; var result = new byte[input.Length]; var counter = iv.ToArray();
        var whole = new byte[input.Length + skip];
        for (var offset = 0; offset < whole.Length; offset += 16)
        {
            var block = aes.EncryptEcb(counter, PaddingMode.None);
            for (var i = 0; i < Math.Min(16, whole.Length - offset); i++) whole[offset+i] = block[i];
            for (var j = 15; j >= 0 && ++counter[j] == 0; j--) { }
        }
        for (var i = 0; i < input.Length; i++) result[i] = (byte)(input[i] ^ whole[i+skip]); return result;
    }
    private sealed class FragmentedStream(byte[] data, int chunk) : MemoryStream(data)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], ct); }
}
