using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net;
using System.Net.Http;
using XFrame.Device;
using XFrame.Device.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = ResolveApiBaseUrl(builder);

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(apiBaseUrl)
});

await builder.Build().RunAsync();

static string ResolveApiBaseUrl(WebAssemblyHostBuilder builder)
{
    var configured = builder.Configuration["ApiBaseUrl"];
    if (!string.IsNullOrWhiteSpace(configured))
        return EnsureTrailingSlash(configured);

    var hostUri = new Uri(builder.HostEnvironment.BaseAddress);

    if (hostUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        hostUri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(hostUri.Host, out _))
    {
        var apiUri = new UriBuilder(hostUri.Scheme, hostUri.Host, hostUri.Scheme == "https" ? 5001 : 5000);
        return EnsureTrailingSlash(apiUri.Uri.ToString());
    }

    return EnsureTrailingSlash(builder.HostEnvironment.BaseAddress);
}

static string EnsureTrailingSlash(string value)
{
    return value.EndsWith("/") ? value : value + "/";
}