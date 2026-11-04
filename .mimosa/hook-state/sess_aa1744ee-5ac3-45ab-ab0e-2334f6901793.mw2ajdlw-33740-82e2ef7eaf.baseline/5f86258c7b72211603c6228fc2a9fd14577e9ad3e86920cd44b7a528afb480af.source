using System.Net;
using System.Net.Sockets;
using System.Text;
using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// 撤销探测（RelayLicenseProbe）的行为。
///
/// 这一组测试的重点不是"能解析 JSON"，而是**失败安全**：
/// 探测失败（断网/超时/HTTP 错误/坏响应）必须返回 Known=false，
/// 调用方（被控端授权看护）据此什么都不做 —— 绝不能因为服务器抖动就把客户的机器停了。
/// 只有服务器明确回答 revoked 才允许停机。
/// </summary>
public class RelayLicenseProbeTests
{
    // ---------------------------------------------------------------- 地址换算

    [Theory]
    [InlineData("ws://127.0.0.1:8080/ws", "http://127.0.0.1:8080/api/license")]
    [InlineData("wss://relay.example.com/ws", "https://relay.example.com/api/license")]
    [InlineData("ws://202.60.232.209:8080/ws", "http://202.60.232.209:8080/api/license")]
    [InlineData("http://1.2.3.4:90", "http://1.2.3.4:90/api/license")]
    [InlineData("https://relay.example.com:8443/ws", "https://relay.example.com:8443/api/license")]
    public void 服务器地址换算成状态接口(string input, string expected)
    {
        Assert.Equal(expected, RelayLicenseProbe.StatusUrl(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1:8080/ws")]
    [InlineData("tcp://1.2.3.4:80")]
    public void 认不出的地址返回空(string input)
    {
        Assert.Null(RelayLicenseProbe.StatusUrl(input));
    }

    // ---------------------------------------------------------------- 探测结果

    [Fact]
    public async Task 服务器说已撤销_必须如实返回()
    {
        var body = """{"state":"revoked","reason":"该激活码已被厂商撤销（撤销于 2026-09-21 04:00 UTC）","deployment":"DEP-1"}""";
        using var server = FakeHttpServer.Start(body);
        var r = await RelayLicenseProbe.QueryAsync($"ws://127.0.0.1:{server.Port}/ws", TimeSpan.FromSeconds(5));
        Assert.True(r.Known);
        Assert.Equal("revoked", r.State);
        Assert.Contains("撤销", r.Reason);
    }

    [Fact]
    public async Task 服务器说正常_如实返回()
    {
        using var server = FakeHttpServer.Start("""{"state":"ok","deployment":"DEP-1"}""");
        var r = await RelayLicenseProbe.QueryAsync($"ws://127.0.0.1:{server.Port}/ws", TimeSpan.FromSeconds(5));
        Assert.True(r.Known);
        Assert.Equal("ok", r.State);
    }

    [Fact]
    public async Task 服务器返回500_当作不知道()
    {
        using var server = FakeHttpServer.Start("""{"state":"revoked"}""", status: "500 Internal Server Error");
        var r = await RelayLicenseProbe.QueryAsync($"ws://127.0.0.1:{server.Port}/ws", TimeSpan.FromSeconds(5));
        Assert.False(r.Known);
    }

    [Fact]
    public async Task 响应不是JSON_当作不知道()
    {
        using var server = FakeHttpServer.Start("<html>502 Bad Gateway</html>");
        var r = await RelayLicenseProbe.QueryAsync($"ws://127.0.0.1:{server.Port}/ws", TimeSpan.FromSeconds(5));
        Assert.False(r.Known);
    }

    [Fact]
    public async Task 服务器连不上_当作不知道()
    {
        // 127.0.0.1 上一个必然没人监听的端口
        var port = FakeHttpServer.FreePort();
        var r = await RelayLicenseProbe.QueryAsync($"ws://127.0.0.1:{port}/ws", TimeSpan.FromSeconds(3));
        Assert.False(r.Known);
    }

    [Fact]
    public async Task 地址无法解析_当作不知道()
    {
        var r = await RelayLicenseProbe.QueryAsync("127.0.0.1:8080", TimeSpan.FromSeconds(3));
        Assert.False(r.Known);
    }

    /// <summary>
    /// 极简假 HTTP 服务器：只回一个固定响应。
    /// 用 TcpListener 而不是 HttpListener —— 后者在 Windows 上要 URL 预留（非管理员会 AccessDenied）。
    /// </summary>
    private sealed class FakeHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;

        public int Port { get; }

        private FakeHttpServer(TcpListener listener, string body, string status)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            {
                                var stream = client.GetStream();
                                // 读掉请求头（读到空行为止），再回响应
                                var buf = new byte[4096];
                                var read = await stream.ReadAsync(buf);
                                _ = read;
                                var payload = Encoding.UTF8.GetBytes(body);
                                var head = Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 {status}\r\n" +
                                    $"Content-Type: {(body.StartsWith('<') ? "text/html" : "application/json")}; charset=utf-8\r\n" +
                                    $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(head);
                                await stream.WriteAsync(payload);
                                await stream.FlushAsync();
                            }
                        }
                        catch { }
                    });
                }
            });
        }

        public static FakeHttpServer Start(string body, string status = "200 OK")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeHttpServer(listener, body, status);
        }

        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
