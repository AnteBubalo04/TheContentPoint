using Microsoft.AspNetCore.Http.Features;
using XFrame.RenderWorker.Models;
using XFrame.RenderWorker.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("SmtpSettings"));
builder.Services.Configure<InternalApiSettings>(builder.Configuration.GetSection("InternalApi"));

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

builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<VideoComposerService>();

builder.Services.AddSingleton<IRenderJobQueue, RenderJobQueue>();
builder.Services.AddHostedService<RenderJobWorker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () =>
{
    return Results.Ok(new
    {
        service = "XFrame.RenderWorker",
        status = "running",
        swagger = "/swagger/index.html"
    });
});

app.UseAuthorization();

app.MapControllers();

app.Run();