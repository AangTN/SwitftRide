using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwiftRide.Auth.Application;
using SwiftRide.Auth.Infrastructure;
using SwiftRide.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddExceptionHandler<SwiftRide.Auth.Api.AuthExceptionHandler>();
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment.IsDevelopment());
var otp = builder.Configuration.GetSection("Otp").Get<OtpSettings>() ?? new();
var token = builder.Configuration.GetSection("Tokens").Get<TokenSettings>() ?? new();
if (otp.UseDevelopmentOtp && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Fixed OTP cannot be enabled outside Development.");
if (builder.Configuration.GetValue("Seed:Enabled", false) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Development seed cannot be enabled outside Development.");
if (otp.HmacSecret.Length < 32 || otp.LifetimeSeconds < 1 || otp.MaxAttempts < 1 || otp.ResendSeconds < 1 ||
    otp.RequestsPerIpPhonePerHour < 1 || token.AccessMinutes < 1 || token.SessionDays < 1)
    throw new InvalidOperationException("Invalid OTP/token settings; HMAC secret must contain at least 32 characters.");
builder.Services.AddSingleton(otp);
builder.Services.AddSingleton(token);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AuthDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("AuthDb")
    ?? throw new InvalidOperationException("ConnectionStrings:AuthDb is required.")));
builder.Services.AddScoped<IAuthStore, EfAuthStore>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<IOtpSender, DevelopmentOtpSender>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddHealthChecks().AddCheck<SwiftRide.Auth.Api.AuthDatabaseHealthCheck>("auth-db");
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    if (app.Configuration.GetValue("Database:MigrateOnStartup", false)) await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment() && app.Configuration.GetValue("Seed:Enabled", false)) await AuthSeed.RunAsync(db);
}
app.UseServiceDefaults();
app.UseStaticFiles();
app.Run();

public partial class Program;
