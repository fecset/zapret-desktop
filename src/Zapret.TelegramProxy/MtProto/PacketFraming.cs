using System.Buffers.Binary;
using Zapret.TelegramProxy.Security;
namespace Zapret.TelegramProxy.MtProto;

// Reads precisely one transport packet, even when headers/payload cross TCP reads.
internal static class PacketFraming
{
    public static async Task<byte[]?> ReadAsync(Stream stream, AesCtr cipher, uint protocol, int maximum, CancellationToken ct)
    {
        var prefix = new byte[4];
        if (await stream.ReadAsync(prefix.AsMemory(0, 1), ct).ConfigureAwait(false) == 0) return null;
        cipher.Transform(prefix.AsSpan(0, 1));
        int header, length;
        if (protocol == ObfuscatedTransport.Abridged)
        {
            header = (prefix[0] & 0x7f) == 0x7f ? 4 : 1;
            if (header == 4)
            {
                await stream.ReadExactlyAsync(prefix.AsMemory(1, 3), ct).ConfigureAwait(false); cipher.Transform(prefix.AsSpan(1, 3));
                length = (prefix[1] | prefix[2] << 8 | prefix[3] << 16) * 4;
            }
            else length = (prefix[0] & 0x7f) * 4;
        }
        else
        {
            header = 4; await stream.ReadExactlyAsync(prefix.AsMemory(1, 3), ct).ConfigureAwait(false); cipher.Transform(prefix.AsSpan(1, 3));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(prefix) & 0x7fffffff;
            if (size > maximum) throw new InvalidDataException("Превышен размер MTProto-пакета.");
            length = (int)size;
        }
        if (length <= 0 || length > maximum) throw new InvalidDataException("Недопустимая длина MTProto-пакета.");
        var packet = new byte[header + length]; prefix.AsSpan(0, header).CopyTo(packet);
        await stream.ReadExactlyAsync(packet.AsMemory(header), ct).ConfigureAwait(false); cipher.Transform(packet.AsSpan(header));
        return packet;
    }
    public static byte[] HttpBody(byte[] frame, uint protocol, int maximum)
    {
        var header = protocol == ObfuscatedTransport.Abridged ? (frame[0] & 0x7f) == 0x7f ? 4 : 1 : 4;
        var body = frame[header..]; var length = body.Length;
        if (protocol == ObfuscatedTransport.Padded)
        {
            if (length < 24) throw new InvalidDataException("Короткий padded MTProto-пакет.");
            var unencrypted = body.AsSpan(0, 8).SequenceEqual(new byte[8]);
            var actual = unencrypted ? 20L + BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)) : 24 + (length - 24) / 16 * 16;
            if (actual < 24 || actual > length || length - actual > (unencrypted ? 256 : 15)) throw new InvalidDataException("Неверный MTProto padding.");
            body = body[..(int)actual];
        }
        if (body.Length < 24 || body.Length > maximum || body.Length % 4 != 0) throw new InvalidDataException("Неверный MTProto HTTP-пакет.");
        return body;
    }
    public static byte[] HttpReply(byte[] body, uint protocol)
    {
        if (body.Length == 0 || body.Length % 4 != 0) throw new InvalidDataException("Неверный ответ MTProto HTTP.");
        var words = body.Length / 4;
        var header = protocol == ObfuscatedTransport.Abridged ? words < 127 ? 1 : 4 : 4;
        var result = new byte[header + body.Length];
        if (protocol == ObfuscatedTransport.Abridged)
        { result[0] = words < 127 ? (byte)words : (byte)127; if (header == 4) { result[1] = (byte)words; result[2] = (byte)(words >> 8); result[3] = (byte)(words >> 16); } }
        else BinaryPrimitives.WriteInt32LittleEndian(result, body.Length);
        body.CopyTo(result, header); return result;
    }
}
