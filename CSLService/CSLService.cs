using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("FlyByWireless.CSLOnDemand.CLSTests")]

namespace FlyByWireless.CSLOnDemand;

internal sealed record Aircraft(string Designator, char ClassType, int EngineCount, char EngineType, char WakeCategory)
{
    public ISet<string>? Related { get; set; }
}

internal sealed record Obj8(string Package, string Path, string? Texture = null, string? TextureLit = null);

internal sealed record Selector(string Icao, string? Operator = null, string? Livery = null);

internal sealed record Obj8Aircraft(string Root, string Id)
{
    public ISet<Obj8> Obj8s { get; init; } = new HashSet<Obj8>();
    public float? VertOffset { get; set; }
    public ISet<Selector> Matches { get; init; } = new HashSet<Selector>();

    public ISet<string> Dependencies => Obj8s.Select(o => o.Package).ToHashSet();

    public Package Pack() => new(Root)
    {
        ExportNames = Dependencies,
        Obj8Aircraft = { this }
    };

    public bool UnionWith(Obj8Aircraft other)
    {
        try
        {
            if (Id == other?.Id && VertOffset == other.VertOffset)
            {
                Obj8s.UnionWith(other.Obj8s);
                Matches.UnionWith(other.Matches);
                return true;
            }
        }
        catch { }
        return false;
    }
}

internal sealed record Package(string Root)
{
    public ISet<string> ExportNames { get; init; } = new HashSet<string>();
    public ISet<string> Dependencies => Obj8Aircraft.SelectMany(a => a.Dependencies).Except(ExportNames).ToHashSet();
    public ISet<Obj8Aircraft> Obj8Aircraft { get; init; } = new HashSet<Obj8Aircraft>();

    public void UnionWith(Package other)
    {
        ExportNames.UnionWith(ExportNames);
        ExportNames.UnionWith(other.ExportNames);
        foreach (var a in other.Obj8Aircraft)
        {
            UnionWith(a);
        }
    }

    public void UnionWith(Obj8Aircraft aircraft)
    {
        if (aircraft != null && !Obj8Aircraft.Any(a => a.UnionWith(aircraft)))
        {
            Obj8Aircraft.Add(aircraft);
        }
    }

    public StringBuilder ToXsbAircraft(bool dependencies = false)
    {
        StringBuilder sb = new();
        foreach (var e in ExportNames)
        {
            sb.Append("EXPORT_NAME ");
            sb.AppendLine(e);
        }
        if (dependencies)
        {
            foreach (var d in Dependencies)
            {
                sb.Append("DEPENDENCY ");
                sb.AppendLine(d);
            }
        }
        foreach (var a in Obj8Aircraft)
        {
            sb.Append("OBJ8_AIRCRAFT ");
            sb.AppendLine(a.Id);
            foreach (var o in a.Obj8s)
            {
                sb.Append("OBJ8 SOLID YES ");
                sb.Append(o.Package);
                sb.Append('/');
                sb.Append(o.Path);
                {
                    if (o.Texture is { Length: > 0 } and var t)
                    {
                        sb.Append(' ');
                        sb.Append(t);
                    }
                }
                {
                    if (o.TextureLit is { Length: > 0 } and var l)
                    {
                        sb.Append(' ');
                        sb.Append(l);
                    }
                }
                sb.AppendLine();
            }
            if (a.VertOffset is float v)
            {
                sb.Append("VERT_OFFSET ");
                sb.AppendLine(v.ToString());
            }
            foreach (var m in a.Matches)
            {
                sb.Append("MATCHES ");
                sb.Append(m.Icao);
                var hasOperator = false;
                {
                    if (m.Operator is { Length: > 0 } and var o)
                    {
                        hasOperator = true;
                        sb.Append(' ');
                        sb.Append(o);
                    }
                }
                {
                    if (m.Livery is { Length: > 0 } and var l)
                    {
                        if (hasOperator)
                        {
                            sb.Append(' ');
                        }
                        else
                        {
                            sb.Append(" - ");
                        }
                        sb.Append(l);
                    }
                }
                sb.AppendLine();
            }
        }
        return sb;
    }
}

public class CSLServiceOptions
{
    [Required]
    public string? Root { get; set; }
}

public class CSLService : IHostedService
{
    static private Random _random = new();

    private readonly ILogger<CSLService> _logger;

    internal readonly ConcurrentDictionary<string, Package> _packages = new();

    internal readonly ConcurrentDictionary<string, Aircraft> _doc8643 = new();

    internal IDictionary<string, Obj8Aircraft> _aircraft = new Dictionary<string, Obj8Aircraft>();

    readonly string _resources;

    public CSLService(ILogger<CSLService> logger, IOptions<CSLServiceOptions> options)
    {
        _logger = logger;
        _resources = options.Value.Root!;
    }

    private void LogPackageDeprecatedWarning(string packagePath, int lineNumber, string key) =>
        _packageDeprecatedWarning(_logger, packagePath, lineNumber, key, null);
    static readonly Action<ILogger, string, int, string, Exception?> _packageDeprecatedWarning =
        LoggerMessage.Define<string, int, string>(LogLevel.Warning, 1, "Deprecated command at {PackagePath}:{LineNumber} ({Key})");

    private void LogPackageSyntaxError(string packagePath, int lineNumber) =>
        _packageSyntaxError(_logger, packagePath, lineNumber, null);
    static readonly Action<ILogger, string, int, Exception?> _packageSyntaxError =
        LoggerMessage.Define<string, int>(LogLevel.Error, 2, "Syntax error at {PackagePath}:{LineNumber}");

    private void LogRelatedSingleWarning(int lineNumber, string type) =>
        _relatedSingleWarning(_logger, lineNumber, type, null);
    static readonly Action<ILogger, int, string, Exception?> _relatedSingleWarning =
        LoggerMessage.Define<int, string>(LogLevel.Warning, 3, "Unrelated type on line {LineNumber} ({Type})");

    private void LogDuplicatedError(string root, string id) =>
        _duplicatedError(_logger, root, id, null);
    static readonly Action<ILogger, string, string, Exception?> _duplicatedError =
        LoggerMessage.Define<string, string>(LogLevel.Error, 4, "Duplicated {Root}/{Id}");

    public async Task CachePackagesAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var p in EnumeratePackagesAsync(Path.Join(_resources, "CSL"), cancellationToken: cancellationToken))
        {
            foreach (var e in p.ExportNames)
            {
                _packages.AddOrUpdate(e, p, (_, package) =>
                {
                    package.UnionWith(p);
                    return package;
                });
            }
        }
        Dictionary<string, Obj8Aircraft> d = new();
        foreach (var a in _packages.Values.Distinct().SelectMany(p => p.Obj8Aircraft))
        {
            if (!d.TryAdd($"{a.Root}/{a.Id}", a))
            {
                LogDuplicatedError(a.Root, a.Id);
            }
        }
        _aircraft = d;
    }

    public async Task CacheRelatedAsync(CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(Path.Join(_resources, "related.txt"));
        await foreach (var s in ParseRelatedAsync(stream, cancellationToken))
        {
            foreach (var t in s)
            {
                if (_doc8643.TryGetValue(t, out var a))
                {
                    a.Related = s;
                }
            }
        }
    }

    public async Task CacheDoc8643Async(CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(Path.Join(_resources, "Doc8643.txt"));
        await foreach (var a in ParseDoc8643Async(stream, cancellationToken))
        {
            _doc8643[a.Designator] = a;
        }
    }

    public void ClearCache()
    {
        _packages.Clear();
        _doc8643.Clear();
        _aircraft.Clear();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // TODO: load from zip/rar
        await CacheDoc8643Async(cancellationToken);
        await CacheRelatedAsync(cancellationToken);
        await CachePackagesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ClearCache();
        return Task.CompletedTask;
    }

    internal async IAsyncEnumerable<Package> EnumeratePackagesAsync(string path, int maxDepth = 5, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = await Task.Run
        (
            () => Directory.EnumerateFiles(path, "xsb_aircraft.txt", new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                MaxRecursionDepth = maxDepth,
                RecurseSubdirectories = true
            }).GetEnumerator()
        , cancellationToken).WaitAsync(cancellationToken);
        while (await Task.Run(() => enumerator.MoveNext(), cancellationToken).WaitAsync(cancellationToken))
        {
            var packagePath = enumerator.Current;
            using var stream = File.OpenRead(packagePath);
            yield return await ParsePackageAsync(Path.GetRelativePath(path, packagePath), stream, cancellationToken);
        }
    }

    static private readonly Regex _packagePattern = new(@"(?<=^\s*)(?<Key>[^#\s]+)(?:\s+(?<Values>[^#\s]+))+", RegexOptions.Compiled);
    internal async Task<Package> ParsePackageAsync(string packagePath, Stream packageStream, CancellationToken cancellationToken = default)
    {
        Package package = new(Path.GetDirectoryName(packagePath)!);
        Obj8Aircraft? aircraft = null;
        void Add()
        {
            if (aircraft is { Obj8s.Count: > 0, Matches.Count: > 0 })
            {
                package.UnionWith(aircraft);
            }
            aircraft = null;
        }
        {
            int lineNumber = 0;
            using var reader = new StreamReader(packageStream, Encoding.ASCII);
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                ++lineNumber;
                if (line is { Length: > 0 } && line[0] != '#' && _packagePattern.Match(line) is { Success: true, Groups: var groups })
                {
                    if (groups["Values"] is { Success: true, Captures.Count: > 0, Captures: var values })
                    {
                        var key = groups["Key"].Value;
                        switch (key)
                        {
                            case "MATCHES":
                            case "LIVERY":
                            case "AIRLINE":
                            case "ICAO":
                                if (aircraft != null && values.Count <= 3)
                                {
                                    aircraft.Matches.Add(new(
                                        values[0].Value,
                                        values.Count > 1 && values[1].ValueSpan != "-" ? values[1].Value : null,
                                        values.Count > 2 ? values[2].Value : null
                                    ));
                                    continue;
                                }
                                break;
                            case "OBJ8":
                                static Obj8? P(CaptureCollection values) =>
                                    values.Count is >= 3 and <= 5 &&
                                    values[2].ValueSpan is var s && s.IndexOfAny('/', '\\', ':') is > 0 and var i ?
                                    new(
                                        s[..i].ToString(),
                                        s[(i + 1)..].ToString().Replace('\\', '/').Replace(':', '/'),
                                        values.Count >= 4 ? values[3].Value : null,
                                        values.Count >= 5 ? values[4].Value : null
                                    ) :
                                    null;
                                if (aircraft != null && P(values) is not null and var o)
                                {
                                    aircraft.Obj8s.Add(o);
                                    continue;
                                }
                                break;
                            case "OBJ8_AIRCRAFT":
                                Add();
                                if (values.Count == 1)
                                {
                                    aircraft = new(package.Root, values[0].Value);
                                    continue;
                                }
                                break;
                            case "VERT_OFFSET":
                                {
                                    if (values.Count == 1 && aircraft != null && float.TryParse(values[0].Value, out var f))
                                    {
                                        aircraft.VertOffset = f;
                                        continue;
                                    }
                                }
                                break;
                            case "OFFSET":
                                {
                                    if (values.Count == 3 && aircraft != null && float.TryParse(values[2].Value, out var f))
                                    {
                                        aircraft.VertOffset = f;
                                        continue;
                                    }
                                }
                                break;
                            case "EXPORT_NAME":
                                if (values.Count == 1)
                                {
                                    package.ExportNames.Add(values[0].Value);
                                    continue;
                                }
                                break;
                            case "OBJECT" or "AIRCRAFT":
                                Add();
                                goto default;
                            default:
                                LogPackageDeprecatedWarning(packagePath, lineNumber, key);
                                continue;
                        }
                    }
                    LogPackageSyntaxError(packagePath, lineNumber);
                }
            }
        }
        Add();
        return package;
    }

    static private readonly Regex _relatedPattern = new(@"^(?!;)(?:\s*\b(?<Type>\S+)\b\s*)+$", RegexOptions.Compiled);
    internal async IAsyncEnumerable<ISet<string>> ParseRelatedAsync(Stream relatedStream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int lineNumber = 0;
        using var reader = new StreamReader(relatedStream, Encoding.ASCII);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            ++lineNumber;
            if (line is { Length: > 0 } && _relatedPattern.Match(line) is { Success: true, Groups: var groups })
            {
                var s = groups["Type"].Captures.Select(c => c.Value).ToHashSet();
                if (s.Count > 1)
                {
                    yield return s;
                }
                else
                {
                    LogRelatedSingleWarning(lineNumber, line.Trim());
                }
            }
        }
    }

    static private readonly Regex _doc8643Pattern = new(@"^.*\t(?<Designator>[\dA-Z]+)\t(?<ClassType>[A-Z])(?<EngineCount>\d)(?<EngineType>[A-Z])\t(?<Wake>[A-Z])$", RegexOptions.Compiled);
    static internal async IAsyncEnumerable<Aircraft> ParseDoc8643Async(Stream doc8643Stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(doc8643Stream, Encoding.ASCII);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (line is { Length: > 0 } && _doc8643Pattern.Match(line) is { Success: true, Groups: var groups })
            {
                yield return new
                (
                    groups["Designator"].Value,
                    groups["ClassType"].ValueSpan[0],
                    groups["EngineCount"].ValueSpan[0] - '0',
                    groups["EngineType"].ValueSpan[0],
                    groups["Wake"].ValueSpan[0]
                );
            }
        }
    }

    internal Obj8Aircraft? MatchCore(string? icao = null, string? airline = null, string? livery = null)
    {
        Aircraft? aircraft = null;
        if (icao != null)
        {
            _doc8643.TryGetValue(icao, out aircraft);
        }
        int bestQuality = 0b1111111111;
        List<Obj8Aircraft> best = new();
        foreach (var a in _aircraft.Values)
        {
            foreach (var m in a.Matches)
            {
                var q = 0;
                if (string.IsNullOrEmpty(livery) || m.Livery != livery)
                {
                    q |= 0b0000000001;
                }
                if (string.IsNullOrEmpty(airline) || m.Operator != airline)
                {
                    q |= 0b0000001010;
                }
                if (string.IsNullOrEmpty(icao) || m.Icao != icao)
                {
                    q |= 0b0000000100;
                }
                if (aircraft == null)
                {
                    q |= 0b1111111000;
                }
                else if (_doc8643.TryGetValue(m.Icao, out var other))
                {
                    if (aircraft.Related?.Contains(m.Icao) != true)
                    {
                        q |= 0b0000011000;
                    }
                    if (aircraft.ClassType != other.ClassType)
                    {
                        q |= 0b0000100000;
                    }
                    if (aircraft.EngineCount != other.EngineCount)
                    {
                        q |= 0b0001000000;
                    }
                    if (aircraft.WakeCategory != other.WakeCategory)
                    {
                        q |= 0b00100000000;
                    }
                    if (aircraft.ClassType != other.ClassType)
                    {
                        q |= 0b0100000000;
                    }
                    if ((aircraft.ClassType == 'H') != (other.ClassType == 'H'))
                    {
                        q |= 0b1000000000;
                    }
                }
                else
                {
                    q |= 0b1111111000;
                }
                if (q < bestQuality)
                {
                    best.Clear();
                    best.Add(a);
                    bestQuality = q;
                }
                else if (q == bestQuality)
                {
                    best.Add(a);
                }
            }
        }
        return best.Count is > 0 and var bc ? best[_random.Next(bc)] :
            _aircraft is { Count: > 0 and var ac, Values: var v } ? v.ElementAt(_random.Next(ac)) :
            null;
    }

    internal async Task<MultipartContent> CreateMultipartContentAsync(Package package)
    {
        MultipartContent mc = new();
        {
            HashSet<string> s = new();
            void T(string path)
            {
                if (s.Add(path))
                {
                    var p = Path.Join(_resources, "CSL", path);
                    FileInfo i = new(p);
                    mc.Add(new StreamContent(File.OpenRead(p))
                    {
                        Headers =
                        {
                            ContentType = new(Path.GetExtension(path).ToLowerInvariant() switch
                            {
                                ".dds" => "image/vnd.ms-dds",
                                ".png" => "image/png",
                                _ => "application/octet-stream"
                            }),
                            ContentDisposition = new("attachment")
                            {
                                FileName = path.Replace('\\', '/')
                            },
                            ContentLength = i.Length,
                            LastModified = i.LastWriteTimeUtc
                        }
                    });
                }
            }
            foreach (var a in package.Obj8Aircraft)
            {
                foreach (var o in a.Obj8s)
                {
                    var p = Path.Join(_packages[o.Package].Root, o.Path);
                    if (s.Add(p))
                    {
                        var d = Path.GetDirectoryName(p);
                        StringBuilder sb = new();
                        using var file = File.OpenRead(Path.Join(_resources, "CSL", p));
                        using StreamReader reader = new(file, Encoding.ASCII);
                        while (!reader.EndOfStream)
                        {
                            var line = await reader.ReadLineAsync();
                            if (line is { Length: > 8 } && line.AsSpan(0, 7).SequenceEqual("TEXTURE"))
                            {
                                if (line is { Length: > 12 } && line.AsSpan(7, 4).SequenceEqual("_LIT"))
                                {
                                    var t = o.Texture ?? line.AsSpan(12).Trim().ToString();
                                    T(Path.Join(d, t));
                                    sb.Append("TEXTURE_LIT ");
                                    sb.AppendLine(t);
                                }
                                else
                                {
                                    var t = o.TextureLit ?? line.AsSpan(8).Trim().ToString();
                                    T(Path.Join(d, t));
                                    sb.Append("TEXTURE ");
                                    sb.AppendLine(t);
                                }
                            }
                            else
                            {
                                sb.AppendLine(line);
                            }
                        }
                        mc.Add(new StringContent(sb.ToString(), Encoding.ASCII, "text/plain")
                        {
                            Headers =
                            {
                                ContentDisposition = new("attachment")
                                {
                                    FileName = p.Replace('\\', '/')
                                }
                            }
                        });
                    }
                }
            }
        }
        mc.Add(new StringContent(package.ToXsbAircraft().ToString(), Encoding.ASCII, "text/plain")
        {
            Headers =
            {
                ContentDisposition = new("attachment")
                {
                    FileName = $"{package.Root}/xsb_aircraft.txt"
                }
            }
        });
        return mc;
    }

    public Task<MultipartContent> CreateMultipartContentAsync(string root, string id) =>
        CreateMultipartContentAsync(_aircraft[$"{root}/{id}"].Pack());

    public (string Root, string Id)? Match(string? icao = null, string? airline = null, string? livery = null) =>
        MatchCore(icao, airline, livery) is not null and var a ?
            (a.Root, a.Id) :
            null;
}