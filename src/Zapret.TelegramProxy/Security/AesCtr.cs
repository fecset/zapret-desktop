using System.Security.Cryptography;
namespace Zapret.TelegramProxy.Security;

// Flowseal uses a big-endian 128-bit counter. Each direction owns its cipher.
internal sealed class AesCtr : IDisposable
{
    private readonly Aes aes = Aes.Create();
    private readonly ICryptoTransform encryptor;
    private readonly byte[] counter, block = new byte[16];
    private int position = 16;
    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        aes.Key = key.ToArray(); aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
        encryptor = aes.CreateEncryptor(); counter = iv.ToArray();
    }
    public void Transform(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (position == 16)
            {
                encryptor.TransformBlock(counter, 0, 16, block, 0);
                for (var j = 15; j >= 0 && ++counter[j] == 0; j--) { }
                position = 0;
            }
            data[i] ^= block[position++];
        }
    }
    public void Skip(int count) { var data = new byte[count]; Transform(data); CryptographicOperations.ZeroMemory(data); }
    public void Dispose() { encryptor.Dispose(); aes.Dispose(); CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(block); }
}
