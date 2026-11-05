// Adapted from Flowseal/tg-ws-proxy, MIT; see licenses/TG-WS-Proxy-LICENSE.txt.
using System.Buffers.Binary;
using System.Security.Cryptography;
using Zapret.TelegramProxy.Security;
namespace Zapret.TelegramProxy.MtProto;

internal sealed record ClientHello(int Dc, bool Media, bool Test, uint Protocol, byte[] KeyMaterial);
internal static class ObfuscatedTransport
{
    public const uint Abridged = 0xefefefef, Intermediate = 0xeeeeeeee, Padded = 0xdddddddd;
    public static ClientHello Parse(byte[] handshake, byte[] secret)
    {
        if (handshake.Length != 64 || secret.Length != 16) throw new InvalidDataException("Неверная длина MTProto init.");
        var material = handshake[8..56];
        using var cipher = new AesCtr(Hash(material.AsSpan(0, 32), secret), material.AsSpan(32, 16));
        var plain = handshake.ToArray(); cipher.Transform(plain);
        var protocol = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(56));
        if (protocol is not (Abridged or Intermediate or Padded)) throw new InvalidDataException("Неверный ключ или транспорт MTProto.");
        var id = (int)BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(60));
        var dc = Math.Abs(id); var test = dc >= 10000; if (test) dc -= 10000;
        if (test ? dc is not (1 or 2 or 3) : dc is not (1 or 2 or 3 or 4 or 5 or 203))
            throw new InvalidDataException("Неизвестный Telegram DC.");
        return new(dc, id < 0, test, protocol, material);
    }
    public static byte[] RelayInit(ClientHello hello)
    {
        byte[] data;
        do { data = RandomNumberGenerator.GetBytes(64); } while (Reserved(data));
        using var cipher = new AesCtr(data.AsSpan(8, 32), data.AsSpan(40, 16));
        var encrypted = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(encrypted.AsSpan(56), hello.Protocol);
        BinaryPrimitives.WriteInt16LittleEndian(encrypted.AsSpan(60), (short)(hello.Media ? -hello.Dc : hello.Dc));
        cipher.Transform(encrypted); encrypted.AsSpan(56, 8).CopyTo(data.AsSpan(56)); return data;
    }
    private static bool Reserved(byte[] b) => b[0] == 0xef || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) == 0 ||
        new uint[] { 0x44414548, 0x54534f50, 0x20544547, Intermediate, Padded, 0x02010316, 0x4954504f }.Contains(BinaryPrimitives.ReadUInt32LittleEndian(b));
    internal static byte[] Hash(ReadOnlySpan<byte> prekey, byte[] secret)
    { Span<byte> input = stackalloc byte[48]; prekey.CopyTo(input); secret.CopyTo(input[32..]); return SHA256.HashData(input); }
}
internal sealed class CryptoContext : IDisposable
{
    public AesCtr ClientRead { get; }
    public AesCtr ClientWrite { get; }
    public AesCtr RelayWrite { get; }
    public AesCtr RelayRead { get; }
    public CryptoContext(ClientHello hello, byte[] secret, byte[] relay)
    {
        var material = hello.KeyMaterial;
        ClientRead = new(ObfuscatedTransport.Hash(material.AsSpan(0, 32), secret), material.AsSpan(32));
        ClientRead.Skip(64);
        var reverse = material.Reverse().ToArray();
        ClientWrite = new(ObfuscatedTransport.Hash(reverse.AsSpan(0, 32), secret), reverse.AsSpan(32));
        RelayWrite = new(relay.AsSpan(8, 32), relay.AsSpan(40, 16)); RelayWrite.Skip(64);
        reverse = relay[8..56].Reverse().ToArray(); RelayRead = new(reverse.AsSpan(0, 32), reverse.AsSpan(32));
    }
    public void Dispose() { ClientRead.Dispose(); ClientWrite.Dispose(); RelayRead.Dispose(); RelayWrite.Dispose(); }
}
