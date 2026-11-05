using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace Zapret.TelegramProxy.Transport;

internal sealed class UpstreamConnectionException(string summary) : IOException(summary);

internal static class ConnectionDiagnostics
{
    // Only controlled text and typed codes. Never use remote response bodies,
    // raw exception messages or URLs, which may include confidential/untrusted data.
    public static string Describe(Exception error) => error switch
    {
        UpstreamConnectionException unavailable => unavailable.Message,
        WebSocketException ws => "WebSocket: " + ws.WebSocketErrorCode + (ws.InnerException is null ? "" : " (" + Describe(ws.InnerException) + ")"),
        SocketException socket => "TCP: " + socket.SocketErrorCode,
        AuthenticationException => "TLS: сертификат или рукопожатие не прошли проверку",
        HttpRequestException { StatusCode: { } status } => "HTTP " + (int)status,
        HttpRequestException { InnerException: { } inner } => Describe(inner),
        OperationCanceledException or TimeoutException => "время ожидания истекло",
        InvalidDataException invalid => invalid.Message switch
        {
            "Превышен размер MTProto-пакета." => "MTProto-пакет превышает лимит размера",
            "Недопустимая длина MTProto-пакета." => "недопустимая длина MTProto-пакета",
            "Превышен размер WebSocket-сообщения." => "WebSocket-сообщение превышает лимит размера",
            "Upstream прислал не бинарный WebSocket-пакет." => "WebSocket прислал текст вместо бинарных данных",
            _ => "недопустимый пакет или размер данных"
        },
        IOException { InnerException: { } inner } => Describe(inner),
        _ => error.GetType().Name
    };
}
