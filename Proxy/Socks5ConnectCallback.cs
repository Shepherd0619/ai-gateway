using System.Net.Sockets;
using System.Text;

namespace AiGateway.Proxy;

internal sealed class Socks5ConnectCallback
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;

    public Socks5ConnectCallback(string address)
    {
        var uri = new Uri(address);
        _proxyHost = uri.Host;
        _proxyPort = uri.Port;
    }

    public async ValueTask<Stream> Connect(
        SocketsHttpConnectionContext ctx,
        CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(_proxyHost, _proxyPort, ct);
        var stream = new NetworkStream(socket, ownsSocket: true);

        try
        {
            // 1. Send greeting: version=5, 1 auth method, no-auth
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);

            // 2. Read server choice: version, chosen method
            var buf = new byte[2];
            await stream.ReadExactlyAsync(buf, ct);
            if (buf[0] != 0x05 || buf[1] != 0x00)
                throw new IOException($"SOCKS5 handshake failed: server chose method {buf[1]}");

            // 3. Send connect request: version=5, command=connect, reserved, atyp=domain,
            //    host length, host bytes, port (2 bytes big-endian)
            var endPoint = ctx.DnsEndPoint
                ?? throw new InvalidOperationException(
                    "SOCKS5 requires a DNS endpoint, but DnsEndPoint is null. " +
                    "The target address may be an IP literal instead of a hostname.");
            var hostBytes = Encoding.UTF8.GetBytes(endPoint.Host);
            var port = endPoint.Port;
            var request = new byte[7 + hostBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x03;
            request[4] = (byte)hostBytes.Length;
            Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
            request[5 + hostBytes.Length] = (byte)(port >> 8);
            request[6 + hostBytes.Length] = (byte)(port & 0xFF);
            await stream.WriteAsync(request, ct);

            // 4. Read reply: version, reply code, reserved, atyp, then skip bound address
            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, ct);
            if (reply[1] != 0x00)
                throw new IOException($"SOCKS5 connect failed: reply code {reply[1]}");

            var toSkip = reply[3] switch
            {
                0x01 => 4 + 2,   // IPv4 + port
                0x04 => 16 + 2,  // IPv6 + port
                0x03 => 0,       // domain — need to read length first
                _ => throw new IOException($"SOCKS5: unknown address type {reply[3]}")
            };

            if (reply[3] == 0x03)
            {
                var lenBuf = new byte[1];
                await stream.ReadExactlyAsync(lenBuf, ct);
                toSkip = lenBuf[0] + 2;
            }

            var skipBuf = new byte[toSkip];
            await stream.ReadExactlyAsync(skipBuf, ct);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return stream;
    }
}
