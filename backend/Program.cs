using backend.Data;
using backend.Services;

var builder = WebApplication.CreateBuilder(args);

var allowedOriginsRaw = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
    ?? "http://localhost:5173,http://localhost:3000";

var allowedOrigins = allowedOriginsRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var allowAll = allowedOrigins.Length == 1 && allowedOrigins[0] == "*";

builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        if (allowAll)
        {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }
        else
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

builder.Services.AddSingleton<ShopRepository>();
builder.Services.AddSingleton<ScoringService>();

var app = builder.Build();

app.UseCors("Frontend");
app.MapControllers();

app.Run();
