using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace FlyByWireless.CSLOnDemand;

static class Constants
{
    static public readonly string Resources = Environment.GetEnvironmentVariable("CSL_Resources")!;
}

static class Extensions
{
    static public string GetBoundary(this MediaTypeHeaderValue type)
    => type.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');

    static public async Task LogAsync(this MultipartContent content, ITestOutputHelper output)
    {
        foreach (var h in content.Headers)
        {
            foreach (var v in h.Value)
            {
                output.WriteLine($"{h.Key}: {v}");
            }
        }
        var boundary = content.Headers.ContentType!.GetBoundary();
        foreach (var c in content)
        {
            output.WriteLine("--" + boundary);
            foreach (var h in c.Headers)
            {
                foreach (var v in h.Value)
                {
                    output.WriteLine($"{h.Key}: {v}");
                }
            }
            output.WriteLine(string.Empty);
            if (c.Headers.ContentType?.MediaType?.StartsWith("text/") is true)
            {
                var t = await c.ReadAsStringAsync();
                output.WriteLine(t.Length > 256 ? $"({t.Length:#,##0} characters)" : t);
            }
            else
            {
                output.WriteLine($"({(await c.ReadAsStreamAsync()).Length:#,##0} bytes)");
            }
        }
        output.WriteLine("--" + boundary + "--");
    }

    static public async Task LogAsync(this MultipartReader reader, ITestOutputHelper output)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(4096);
        for (; ; )
        {
            var section = await reader.ReadNextSectionAsync();
            if (section == null)
            {
                break;
            }
            output.WriteLine(string.Empty);
            var d = section.GetContentDispositionHeader()!;
            output.WriteLine($"Path: {d.FileName}");
            if (section.Headers!.TryGetValue("Last-Modified", out var m))
            {
                output.WriteLine($"Modified: {DateTime.Parse(m.Single()):yyyy-MM-dd'T'HH:mm:ss'Z'}");
            }
            if (new MediaType(section.ContentType!).Type == "text")
            {
                var t = await section.ReadAsStringAsync();
                output.WriteLine(t.Length > 256 ? $"({t.Length:#,##0} characters)" : t);
            }
            else
            {
                int s = 0;
                if
                (
                    section.Headers!.TryGetValue("Content-Length", out var ls) &&
                    int.TryParse(ls.Single(), out s)
                )
                {
                    for (; ; )
                    {
                        var r = await section.Body.ReadAsync(buffer);
                        s += r;
                        if (r == 0)
                        {
                            break;
                        }
                    }
                }
                output.WriteLine($"({s:#,##0} bytes)");
            }
        }
    }
}

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