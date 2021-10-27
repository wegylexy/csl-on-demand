using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.CSLOnDemand;

public class Matching
{

    readonly ITestOutputHelper _output;

    readonly CSLService _service;

    public Matching(ITestOutputHelper output)
    {
        _output = output;
        _service = new
        (
            LoggerFactory.Create(builder =>
            {
                builder.ClearProviders().AddProvider(new TestLoggerProvider(output));
            }).CreateLogger<CSLService>(),
            Options.Create(new CSLServiceOptions
            {
                Root = Constants.Resources
            })
        );
    }

    [Fact]
    public async Task PackagesAsync()
    {
        if (_service._packages.IsEmpty)
        {
            await _service.CachePackagesAsync();
        }
        Assert.False(_service._packages.IsEmpty);
        foreach (var p in _service._packages.Values.Distinct())
        {
            Assert.NotNull(p.ToXsbAircraft(true).ToString());
        }
    }

    [Fact]
    public async Task RelatedAsync()
    {
        if (_service._doc8643.IsEmpty)
        {
            await _service.CacheDoc8643Async();
            await _service.CacheRelatedAsync();
        }
        Assert.False(_service._doc8643.IsEmpty);
        foreach (var a in _service._doc8643)
        {
            Assert.Equal(a.Key, a.Value.Designator);
            Assert.True(a.Value.Related is null or { Count: > 1 });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(false, "C172")]
    [InlineData(false, "A320", "CRK")]
    [InlineData(false, "A21N", "SJX")]
    public async Task Match(bool exact, string? icao = null, string? airline = null, string? livery = null)
    {
        if (_service._doc8643.IsEmpty)
        {
            await _service.CacheDoc8643Async();
            await _service.CacheRelatedAsync();
        }
        if (_service._packages.IsEmpty)
        {
            await _service.CachePackagesAsync();
        }
        var aircraft = _service.MatchCore(icao, airline, livery)!;
        Assert.NotNull(aircraft);
        if (icao != null)
        {
            var related = _service._doc8643[icao].Related;
            Assert.True(related == null ?
                aircraft.Matches.Any(m => m.Icao == icao) :
                aircraft.Matches.IntersectBy(related, m => m.Icao).Any()
            );
            if (exact)
            {
                Assert.Contains(airline, aircraft.Matches.Select(m => m.Operator));
                Assert.Contains(livery, aircraft.Matches.Select(m => m.Livery));
            }
        }
        else if (exact)
        {
            throw new ArgumentException("ICAO designator not specified for exact matching.");
        }
        var package = aircraft.Dependencies.Select(d => _service._packages[d]).Single();
        _output.WriteLine($"Matched: {aircraft.Id} in {package.Root}");
        _output.WriteLine(string.Empty);
        using var content = await _service.CreateMultipartContentAsync(aircraft.Pack());
        await content.LogAsync(_output);
    }
}