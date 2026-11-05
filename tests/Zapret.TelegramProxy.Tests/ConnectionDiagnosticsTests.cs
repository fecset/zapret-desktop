using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Tests;

public sealed class ConnectionDiagnosticsTests
{
    [Fact]
    public void NestedTypedTlsAndSocketCausesAreKeptWithoutRawRemoteMessages()
    {
        const string confidential = "secret-value-and-remote-payload";
        var tls = new WebSocketException(WebSocketError.Faulted,"remote",new HttpRequestException(confidential,new AuthenticationException(confidential)));
        var message = ConnectionDiagnostics.Describe(tls); Assert.Contains("TLS:",message); Assert.Contains("Faulted",message); Assert.DoesNotContain(confidential,message);
        Assert.Contains("ConnectionRefused",ConnectionDiagnostics.Describe(new IOException(confidential,new SocketException((int)SocketError.ConnectionRefused))));
        Assert.DoesNotContain(confidential,ConnectionDiagnostics.Describe(new InvalidDataException(confidential)));
    }
    [Fact]
    public void KnownBoundsAndHttpStatusHaveUsefulDescriptions()
    {
        Assert.Contains("лимит",ConnectionDiagnostics.Describe(new InvalidDataException("Превышен размер MTProto-пакета.")));
        Assert.Contains("HTTP 403",ConnectionDiagnostics.Describe(new HttpRequestException("untrusted",null,System.Net.HttpStatusCode.Forbidden)));
    }
}
