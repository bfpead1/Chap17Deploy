using backend.Data;
using backend.Services;

var builder = WebApplication.CreateBuilder(args);

var allowedOrigins = (
    Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
    ?? "http://localhost:5173,http://localhost:3000"
)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddSingleton<ShopRepository>();
builder.Services.AddSingleton<ScoringService>();

var app = builder.Build();

app.UseCors("Frontend");
app.MapControllers();

app.Run();
