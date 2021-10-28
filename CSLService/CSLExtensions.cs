using FlyByWireless.CSLOnDemand;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class CSLOnDemandExtensions
    {
        static public IWebHostBuilder ConfigureCSLOnDemand(this IWebHostBuilder builder, PathString pathMatch, Action<CSLServiceOptions>? configure = null)
        => builder
            .ConfigureServices(services => services.AddCSLOnDemand(configure))
            .Configure(app => app.MapCSLOnDemand(pathMatch));

        static public IServiceCollection AddCSLOnDemand(this IServiceCollection services, Action<CSLServiceOptions>? configure = null)
        {
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.AddSingleton<CSLService>().AddHostedService(s => s.GetRequiredService<CSLService>());
            return services;
        }

        static public IApplicationBuilder MapCSLOnDemand(this IApplicationBuilder app, PathString pathMatch)
        => app.Map(pathMatch, app => app.UseRouting().UseEndpoints(endpoints =>
        {
            var options = endpoints.ServiceProvider.GetRequiredService<IOptions<CSLServiceOptions>>().Value;
            var csl = endpoints.ServiceProvider.GetRequiredService<CSLService>();
            endpoints.MapGet("match", context =>
            {
                var q = context.Request.Query;
                string? Q(string k) => q.TryGetValue(k, out var vs) && vs.Last() is { Length: > 0 } and var v ? v : null;
                if (csl.Match(Q("icao"), Q("airline"), Q("livery")) is { Root: var root, Id: var id })
                {
                    context.Response.Redirect($"{pathMatch}/pack/{root}/{id}");
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
                return Task.CompletedTask;
            });
            endpoints.MapGet("pack/{root:required}/{id:required}", async context =>
            {
                var request = context.Request;
                var rv = request.RouteValues;
                var root = rv["root"]!.ToString()!;
                var id = rv["id"]!.ToString()!;
                using var content = await csl.CreateMultipartContentAsync(root, id,
                    options.RedirectTextures ? new($"{request.Scheme}://{request.Host}{request.PathBase}/") : null,
                    context.RequestAborted
                );
                content.Headers.ContentDisposition = new("attachment")
                {
                    FileName = $"{root}/{id}.csl"
                };
                var response = context.Response;
                foreach (var h in content.Headers)
                {
                    response.Headers.Add(h.Key, h.Value.ToArray());
                }
                await (await content.ReadAsStreamAsync()).CopyToAsync(response.Body, context.RequestAborted);
            });
            if (options.RedirectTextures)
            {
                endpoints.MapGet("{*path:file}", async context =>
                {
                    var n = context.Request.Path.Value!.TrimStart('/');
                    var p = Path.Join(options.Root, "CSL", n);
                    var response = context.Response;
                    if (new FileInfo(p) is { Exists: true } and var i)
                    {
                        response.Headers.ContentType = new(CSLService.GetMediaType(n).ToString());
                        response.Headers.ContentDisposition = new(new ContentDispositionHeaderValue("attachment")
                        {
                            FileName = n,
                            ModificationDate = i.LastWriteTimeUtc
                        }.ToString());
                        await response.SendFileAsync(p, context.RequestAborted);
                    }
                    else
                    {
                        response.StatusCode = 404;
                    }
                });
            }
        }));
    }
}