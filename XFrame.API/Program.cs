using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using XFrame.API.Models;
using XFrame.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.Configure<RenderWorkerSettings>(builder.Configuration.GetSection("RenderWorker"));

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 20 * 1024 * 1024; // 20 MB
    options.MultipartHeadersLengthLimit = 64 * 1024;
    options.ValueLengthLimit = 64 * 1024;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 20 * 1024 * 1024; // 20 MB
});

builder.Services.AddHttpClient<RenderDispatchService>((serviceProvider, client) =>
{
    var settings = serviceProvider.GetRequiredService<IOptions<RenderWorkerSettings>>().Value;

    if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
    {
        client.BaseAddress = new Uri(settings.BaseUrl);
    }

    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () =>
{
    return Results.Ok(new
    {
        service = "XFrame.API",
        status = "running",
        swagger = "/swagger/index.html"
    });
});

app.UseAuthorization();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

app.MapControllers();

app.Run();