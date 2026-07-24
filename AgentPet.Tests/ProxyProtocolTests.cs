using TokenPet.Services;
using Xunit;

namespace AgentPet.Tests;

public sealed class ProxyProtocolTests
{
    [Theory]
    [InlineData("{\"stream\":true}", true)]
    [InlineData("{ \"stream\" : true, \"model\": \"test\" }", true)]
    [InlineData("{\"stream\":false}", false)]
    [InlineData("{\"message\":\"stream=true\"}", false)]
    [InlineData("{\"stream\":\"true\"}", false)]
    [InlineData("not-json", false)]
    [InlineData("", false)]
    public void IsStreamingBody_UsesJsonBoolean(string body, bool expected)
    {
        Assert.Equal(expected, ProxyProtocol.IsStreamingBody(body));
    }

    [Fact]
    public void Dechunk_ParsesValidChunksAndTrailer()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("4\r\nWiki\r\n5\r\npedia\r\n0\r\nX-Test: ok\r\n\r\n");

        var result = ProxyProtocol.Dechunk(bytes, 1024);

        Assert.Equal("Wikipedia", System.Text.Encoding.ASCII.GetString(result));
    }

    [Theory]
    [InlineData("4\r\nWiki")]
    [InlineData("Z\r\nbad\r\n0\r\n\r\n")]
    [InlineData("4\r\nWiki\r\n")]
    public void Dechunk_RejectsMalformedBodies(string body)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(body);
        Assert.Throws<InvalidDataException>(() => ProxyProtocol.Dechunk(bytes, 1024));
    }
}
