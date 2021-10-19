using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.CSLOnDemand;

public class Matching
{
    static readonly string _resources = Environment.GetEnvironmentVariable("CSL_Resources")!;

    class TestLogger : ILogger
    {
        readonly string _category;
        readonly ITestOutputHelper _output;

        public TestLogger(string category, ITestOutputHelper output)
        {
            _category = category;
            _output = output;
        }

        public IDisposable BeginScope<TState>(TState state) => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                _output.WriteLine("[{0}] {1}: {2}", _category, logLevel, formatter(state, exception));
                if (exception != null)
                {
                    _output.WriteLine(exception.ToString());
                }
            }
        }
    }

    class TestLoggerProvider : ILoggerProvider
    {
        readonly ITestOutputHelper _output;

        public TestLoggerProvider(ITestOutputHelper output) => _output = output;

        public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, _output);

        public void Dispose() { }
    }

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
                Root = _resources
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
        var aircraft = _service.Match(icao, airline, livery)!;
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
        _output.WriteLine(aircraft.Pack(package.Root).ToXsbAircraft().ToString());
    }
}