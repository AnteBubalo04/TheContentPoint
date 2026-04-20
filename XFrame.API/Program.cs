using XFrame.API.Models;
using XFrame.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("SmtpSettings"));

builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<VideoComposerService>();

builder.Services.AddSingleton<IHeroRenderQueue, HeroRenderQueue>();
builder.Services.AddSingleton<IEmailDispatchQueue, EmailDispatchQueue>();

builder.Services.AddHostedService<HeroRenderWorker>();
builder.Services.AddHostedService<EmailDispatchWorker>();

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
