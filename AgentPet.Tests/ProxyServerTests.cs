using System.Net;
using System.Net.Sockets;
using System.Text;
using TokenPet.Services;
using Xunit;

namespace AgentPet.Tests;

public sealed class ProxyServerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AgentPet.Proxy.Tests", Guid.NewGuid().ToString("N"));
    private static readonly ProxyTarget OpenAi = new("OpenAI", "oai", "api.openai.com");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private ProxyServer CreateServer() =>
        new(Path.Combine(_root, "proxy_targets.json"));

    [Fact]
    public void Start_WhenPortIsOccupied_DoesNotBecomeActive()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var server = CreateServer();

        Assert.Throws<SocketException>(() => server.Start(port, [OpenAi]));
        Assert.False(server.IsActive);
    }

    [Fact]
    public void Targets_ReturnsSnapshot()
    {
        var server = CreateServer();
        var first = server.Targets;
        var originalName = server.Targets[0].Name;

        if (first is ProxyTarget[] array)
            array[0] = new ProxyTarget("Changed", "changed", "example.com");

        Assert.Equal(originalName, server.Targets[0].Name);
    }

    [Fact]
    public void AddAndReplaceTarget_RejectDuplicatePrefixes()
    {
        var server = CreateServer();

        Assert.False(server.AddTarget(new ProxyTarget("Duplicate", "oai", "example.com")));
        Assert.True(server.AddTarget(new ProxyTarget("Other", "other", "example.com")));
        Assert.False(server.ReplaceTarget(1, new ProxyTarget("Other", "oai", "example.com")));
    }

    [Fact]
    public void DuplicateContentLength_IsRejected()
    {
        var port = GetAvailablePort();
        var server = CreateServer();
        server.Start(port, [OpenAi]);
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var request = Encoding.ASCII.GetBytes(
                $"POST /oai/v1/responses HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: 0\r\nContent-Length: 1\r\nConnection: close\r\n\r\n");
            using var stream = client.GetStream();
            stream.Write(request);

            for (var i = 0; i < 50 && client.Available == 0; i++)
            {
                server.Poll();
                Thread.Sleep(10);
            }

            var response = new byte[Math.Max(1, client.Available)];
            var read = stream.Read(response, 0, response.Length);
            Assert.StartsWith("HTTP/1.1 400", Encoding.ASCII.GetString(response, 0, read));
        }
        finally
        {
            server.Stop();
        }
    }
    [Fact]
    public void Constructor_RecoversProxyTargetsFromBackup()
    {
        var server = CreateServer();
        Assert.True(server.AddTarget(new ProxyTarget("Other", "other", "example.com")));
        Assert.True(server.ReplaceTarget(1, new ProxyTarget("Changed", "other", "example.com")));
        File.WriteAllText(Path.Combine(_root, "proxy_targets.json"), "{broken");

        var recovered = CreateServer();

        Assert.Contains(recovered.Targets, target => target.Prefix == "other");
    }
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"name\":null,\"prefix\":\"bad\",\"host\":\"example.com\"}]")]
    public void Constructor_RecoversFromNullProxyTargetValues(string invalidPrimary)
    {
        var server = CreateServer();
        Assert.True(server.AddTarget(new ProxyTarget("Other", "other", "example.com")));
        Assert.True(server.ReplaceTarget(1, new ProxyTarget("Changed", "other", "example.com")));
        File.WriteAllText(Path.Combine(_root, "proxy_targets.json"), invalidPrimary);

        var recovered = CreateServer();

        Assert.Contains(recovered.Targets, target => target.Prefix == "other");
    }
    [Fact]
    public void UnknownPrefix_IsRejectedWithoutUpstreamFallback()
    {
        var port = GetAvailablePort();
        var server = CreateServer();
        server.Start(port, [OpenAi]);
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var request = Encoding.ASCII.GetBytes(
                $"GET /unknown/v1/models HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
            using var stream = client.GetStream();
            stream.Write(request);

            for (var i = 0; i < 50 && client.Available == 0; i++)
            {
                server.Poll();
                Thread.Sleep(10);
            }

            var response = new byte[Math.Max(1, client.Available)];
            var read = stream.Read(response, 0, response.Length);
            Assert.StartsWith("HTTP/1.1 421", Encoding.ASCII.GetString(response, 0, read));
        }
        finally
        {
            server.Stop();
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
