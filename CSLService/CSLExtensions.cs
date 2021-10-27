using FlyByWireless.CSLOnDemand;

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
                var rv = context.Request.RouteValues;
                using var content = await csl.CreateMultipartContentAsync(rv["root"]!.ToString()!, rv["id"]!.ToString()!);
                var response = context.Response;
                foreach (var h in content.Headers)
                {
                    response.Headers.Add(h.Key, h.Value.ToArray());
                }
                await (await content.ReadAsStreamAsync()).CopyToAsync(response.Body);
            });
        }));
    }
}