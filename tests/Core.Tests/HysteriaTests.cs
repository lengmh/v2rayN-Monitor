using System.Text;
using V2rayNMonitor.Core;
using Xunit;

namespace Core.Tests;

// Synthetic shape from fqfqgo/v2rayN 95f68d9, never from a user's configuration.
public sealed class HysteriaTests
{
    private const string Configuration = """
        {"stats":{},"policy":{"system":{"statsOutboundUplink":true,"statsOutboundDownlink":true}},
         "metrics":{"listen":"127.0.0.1:23456"},"outbounds":[
         {"tag":"proxy","protocol":"hysteria","settings":{"address":"hy.synthetic.invalid","port":8443,"version":2},
          "streamSettings":{"network":"hysteria","security":"tls","hysteriaSettings":{"version":2,"auth":"Synthetic-Auth"}}},
         {"tag":"direct","protocol":"freedom"},{"tag":"block","protocol":"blackhole"}]}
        """;
    private static bool Parse(string json, out XrayEffectiveConfiguration? result)
        => XrayParser.TryReadConfiguration(Encoding.UTF8.GetBytes(json), out result, out _);

    [Fact]
    public void Hysteria2UsesTransportAuthAndStableProfileIdentity()
    {
        Assert.Equal("hysteria", XrayParser.ProtocolForConfigType(7));
        Assert.True(Parse(Configuration, out var config));
        Assert.Equal(23456, config!.MetricsEndpoint.Port); // Discovered, not the user's observed port.
        Assert.True(XrayParser.TryProfileIdentity(7, 2, "HY.SYNTHETIC.INVALID", 8443, "Synthetic-Auth", "ignored", out var identity));
        Assert.Equal(identity, config.ServerIdentity);
        Assert.True(XrayParser.TryProfileIdentity(7, null, "hy.synthetic.invalid", 8443, "Synthetic-Auth", "", out var defaultCoreIdentity));
        Assert.Equal(identity, defaultCoreIdentity); // Runtime Xray binding is still required by discovery.
        Assert.DoesNotContain("Synthetic-Auth", config.ToString());
        Assert.DoesNotContain("hy.synthetic.invalid", config.ToString());
    }

    [Theory]
    [InlineData("\"version\":2", "\"version\":1")]
    [InlineData("\"network\":\"hysteria\"", "\"network\":\"tcp\"")]
    [InlineData("\"hysteriaSettings\"", "\"unrecognizedSettings\"")]
    [InlineData("\"auth\":\"Synthetic-Auth\"", "\"password\":\"Synthetic-Auth\"")]
    [InlineData("\"auth\":\"Synthetic-Auth\"", "\"auth\":\"\"")]
    [InlineData("\"auth\":\"Synthetic-Auth\"", "\"auth\":null")]
    [InlineData("\"auth\":\"Synthetic-Auth\"", "\"auth\":\"Synthetic-Auth\",\"auth\":\"other\"")]
    [InlineData("\"port\":8443", "\"port\":0")]
    [InlineData("\"protocol\":\"hysteria\"", "\"protocol\":\"hysteria2\"")]
    public void UnprovedHysteriaShapesAreRejected(string before, string after)
        => Assert.False(Parse(Configuration.Replace(before, after), out _));

    [Fact]
    public void BothVersionFieldsAreRequiredIndependently()
    {
        Assert.False(Parse(Configuration.Replace("\"port\":8443,\"version\":2", "\"port\":8443,\"version\":1"), out _));
        Assert.False(Parse(Configuration.Replace("\"hysteriaSettings\":{\"version\":2", "\"hysteriaSettings\":{\"version\":1"), out _));
        Assert.False(Parse(Configuration.Replace("\"port\":8443,\"version\":2", "\"port\":8443"), out _));
    }

    [Theory]
    [InlineData(7, 2, "other.synthetic.invalid", 8443, "Synthetic-Auth")]
    [InlineData(7, 2, "hy.synthetic.invalid", 443, "Synthetic-Auth")]
    [InlineData(7, 2, "hy.synthetic.invalid", 8443, "synthetic-auth")]
    [InlineData(7, 24, "hy.synthetic.invalid", 8443, "Synthetic-Auth")]
    [InlineData(11, 2, "hy.synthetic.invalid", 8443, "Synthetic-Auth")]
    [InlineData(1, 2, "hy.synthetic.invalid", 8443, "Synthetic-Auth")]
    [InlineData(5, 2, "hy.synthetic.invalid", 8443, "Synthetic-Auth")]
    public void MismatchedProfileCannotOwnHysteriaCounters(int type, int core, string address, int port, string password)
    {
        Assert.True(Parse(Configuration, out var config));
        var matches = XrayParser.TryProfileIdentity(type, core, address, port, password, "", out var identity)
            && identity == config!.ServerIdentity;
        Assert.False(matches);
    }

    [Fact]
    public void AnyTlsRemainsExplicitlyOutsideXrayAdapter()
    {
        // This fork forces AnyTLS through sing-box before consulting the profile core preference.
        Assert.Null(XrayParser.ProtocolForConfigType(11));
        Assert.False(Parse(Configuration.Replace("\"protocol\":\"hysteria\"", "\"protocol\":\"anytls\""), out _));
    }

    [Fact]
    public void HysteriaStillRejectsChainingAndMultipleOutbounds()
    {
        Assert.False(Parse(Configuration.Replace("\"security\":\"tls\"", "\"security\":\"tls\",\"sockopt\":{\"dialerProxy\":\"other\"}"), out _));
        Assert.False(Parse(Configuration.Replace("\"tag\":\"direct\"", "\"tag\":\"proxy-2\""), out _));
    }

    [Theory]
    [InlineData(1, "vmess")]
    [InlineData(5, "vless")]
    public void ExistingUuidProtocolsKeepProfileNormalization(int type, string protocol)
    {
        Assert.True(XrayParser.TryProfileIdentity(type, 2, "NODE.INVALID", 443,
            "AAAAAAAA-0000-0000-0000-000000000001", "unused", out var identity));
        Assert.Equal(XrayParser.ServerIdentity(protocol, "node.invalid", 443, "", "aaaaaaaa-0000-0000-0000-000000000001"), identity);
    }
}
