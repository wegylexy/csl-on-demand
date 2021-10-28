using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.CSLOnDemand;

public class WebAPI : IDisposable
{
    readonly ITestOutputHelper _output;

    readonly IHost _host;

    public WebAPI(ITestOutputHelper output)
    {
        _output = output;
        _host = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer().ConfigureLogging(builder =>
            {
                builder.ClearProviders().AddProvider(new TestLoggerProvider(output));
            }).ConfigureServices(services =>
            {
                services.AddRouting().AddResponseCompression(options =>
                {
                    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
                    {
                        "multipart/mixed"
                    });
                });
            }).Configure(app =>
            {
                app.UseResponseCompression();
            }).ConfigureCSLOnDemand("/csl", options =>
            {
                options.Root = Constants.Resources;
            });
        }).Build();
        _host.Start();
    }

    public void Dispose()
    {
        using (_host) { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("C172", null, null)]
    public async Task MatchAsync(string? icao, string? airline, string? livery)
    {
        QueryString q = new();
        if (icao != null)
        {
            q = q.Add(nameof(icao), icao);
        }
        if (airline != null)
        {
            q = q.Add(nameof(airline), airline);
        }
        if (livery != null)
        {
            q = q.Add(nameof(livery), livery);
        }
        var p = "/csl/match" + q;
        _output.WriteLine(p);
        using var hc = _host.GetTestClient();
        using var r = await hc.GetAsync(p);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var l = r.Headers.Location!.ToString();
        _output.WriteLine("Location: " + l);
        Assert.StartsWith("/csl/pack/", l);
    }

    [Theory]
    [InlineData("C172", "C172_0HA")]
    public async Task PackAsync(string root, string id)
    {
        using var hc = _host.GetTestClient();
        hc.DefaultRequestHeaders.AcceptEncoding.Add(new("br", 1));
        using var r = await hc.GetAsync($"/csl/pack/{root}/{id}");
        r.EnsureSuccessStatusCode();
        var t = r.Content.Headers.ContentType!;
        Assert.Equal("multipart/mixed", t.MediaType);
        using var s = await r.Content.ReadAsStreamAsync();
        var reader = new MultipartReader(t.GetBoundary(), s);
        await reader.LogAsync(_output);
    }

    [Theory]
    [InlineData("C172/C172prop.png")]
    public async Task IndividualAsync(string path)
    {
        using var hc = _host.GetTestClient();
        hc.DefaultRequestHeaders.AcceptEncoding.Add(new("br", 1));
        using var r = await hc.GetAsync($"/csl/{path}");
        r.EnsureSuccessStatusCode();
        var t = r.Content.Headers.ContentType!;
        Assert.StartsWith("image/", t.MediaType);
    }
}