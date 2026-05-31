using Asp.Versioning;
using Asp.Versioning.Builder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

// v11: API versioning shared helpers.
//
// All backend HTTP services use URL-segment versioning: /api/v{version:apiVersion}/<service>/...
// The gateway forwards versioned URLs as-is (no path-strip transform) so the version segment
// is the same on the public URL and on the backend.
//
// Adding a v2 endpoint to an existing service is two lines:
//   1) NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).HasApiVersion(new ApiVersion(2, 0))
//   2) .MapToApiVersion(2, 0) on the specific endpoint (or a v2 MapGroup)
// Deprecation:
//   versionSet.HasDeprecatedApiVersion(new ApiVersion(1, 0))
// emits the Sunset / api-deprecated-versions response headers automatically.
public static class ApiVersioningExtensions
{
    public static TBuilder AddSimpleStoreApiVersioning<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                // Emits api-supported-versions / api-deprecated-versions headers so clients
                // can discover what the server knows about without reading OpenAPI.
                options.ReportApiVersions = true;
                // URL-segment reader: /api/v{N}/...
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                // Document name pattern: "v1", "v2" (matches AddOpenApi("v1", ...) below).
                options.GroupNameFormat = "'v'VVV";
                // Substitutes the literal {version:apiVersion} token in route templates with the
                // concrete value (e.g. "1") so OpenAPI shows /api/v1/... not /api/v{version}/...
                options.SubstituteApiVersionInUrl = true;
            });

        return builder;
    }

    // Creates the canonical /api/v{version:apiVersion}/<service> route group, already wired to
    // the v1 ApiVersionSet. Endpoint files use this in place of MapGroup("/api/<service>"):
    //
    //   public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    //   {
    //       var group = app.MapApiV1Group("catalog");
    //       ...
    //   }
    //
    // Receiver is IEndpointRouteBuilder (not WebApplication) so it composes inside MapXEndpoints
    // helpers that take IEndpointRouteBuilder by convention.
    public static RouteGroupBuilder MapApiV1Group(this IEndpointRouteBuilder app, string serviceSegment)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceSegment);

        var versionSet = app.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        return app
            .MapGroup($"/api/v{{version:apiVersion}}/{serviceSegment}")
            .WithApiVersionSet(versionSet)
            .MapToApiVersion(new ApiVersion(1, 0));
    }
}
