using FHSMS.Application;
using FHSMS.Application.Common.Exceptions;
using FHSMS.Infrastructure;
using FHSMS.Infrastructure.Persistence;
using FHSMS.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// --- Hosting-agnostic startup configuration -------------------------------
// Works the same on any host (Alet Cloud VPS, any Linux/Windows VPS, Docker,
// Render, Railway, ...). Everything is driven by environment variables, so
// the same build runs anywhere without code changes.
//
// PORT: many container/PaaS hosts inject a PORT env var and expect the app to
// bind to it. Only overrides when PORT is actually set - otherwise Kestrel
// uses ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS (the official .NET 8 Docker
// image already defaults to 8080) or launchSettings.json for `dotnet run`.
var hostPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(hostPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{hostPort}");
}

// DATABASE_URL: many hosts and managed-Postgres providers expose the
// connection as a single postgres://user:pass@host:port/db URI. Npgsql needs
// the ADO.NET keyword=value form instead, so translate it here when present.
// If you'd rather set ConnectionStrings__DefaultConnection directly (as the
// bundled docker-compose.prod.yml does), just leave DATABASE_URL unset - the
// direct value is used untouched.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrWhiteSpace(databaseUrl))
{
    builder.Configuration["ConnectionStrings:DefaultConnection"] = ConvertPostgresUrlToNpgsqlConnectionString(databaseUrl);
}

// --- Services ------------------------------------------------------------
// Every enum in this API (UserRole, TaxType, PaymentMethod, OrderSourceType,
// TaxProfileType, AgentType, ...) is sent/received as its string name from the
// frontend (e.g. "HotelAgent", not 2). System.Text.Json defaults to NUMERIC
// enum serialization unless told otherwise, which silently fails model
// binding (400 Bad Request) for every request carrying a string enum value.
// This converter must be registered globally, not per-DTO, so no future
// enum anywhere in the API can hit this trap again.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "FHSMS API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a valid JWT token."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    // A comma-separated ALLOWED_ORIGINS env var (or Cors:AllowedOrigins in
    // appsettings) locks CORS down to your real frontend domain(s) in
    // production. Left unset, falls back to AllowAnyOrigin so local dev and
    // first deploys aren't blocked before you know the final frontend URL.
    // (Not needed at all when the frontend and API share one domain, e.g. the
    // bundled Docker setup where the web container proxies /api.)
    var allowedOrigins = (Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
            ?? builder.Configuration["Cors:AllowedOrigins"])
        ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    options.AddPolicy("Default", policy =>
    {
        if (allowedOrigins is { Length: > 0 })
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
        else
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

// Clean Architecture composition root: Application + Infrastructure register
// everything they own, the API project just wires HTTP concerns on top.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// --- Global exception handling --------------------------------------------
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = feature?.Error;

        var (statusCode, payload) = exception switch
        {
            ValidationException validationEx => (StatusCodes.Status400BadRequest, (object)validationEx.Errors),
            NotFoundException notFoundEx => (StatusCodes.Status404NotFound, (object)new { message = notFoundEx.Message }),
            UnauthorizedAccessException authEx => (StatusCodes.Status401Unauthorized, (object)new { message = authEx.Message }),
            FHSMS.Domain.Exceptions.DomainException domainEx => (StatusCodes.Status400BadRequest, (object)new { message = domainEx.Message }),
            _ => (StatusCodes.Status500InternalServerError, (object)new { message = "An unexpected error occurred." })
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(payload);
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Default");
app.UseAuthentication();
app.UseAuthorization();

// Presence tracking: on every authenticated request, stamp LastSeenAt with a
// single UPDATE (ExecuteUpdateAsync - no entity load/tracking overhead, safe
// to run on every request). This is what "online" actually means throughout
// the app - recent activity, not just "logged in at some point today".
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            using var scope = context.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastSeenAt, DateTime.UtcNow));
        }
    }
    await next();
});

app.MapControllers();

// Load balancers, Docker healthchecks and uptime monitors poll this to know the
// app is ready for traffic - unauthenticated and deliberately trivial, no DB
// round-trip, so it can't itself become a point of failure.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// --- Create/upgrade the database schema + seed on startup ------------------
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await ApplicationDbContextSeeder.SeedAsync(context);
}

app.Run();

// Converts a postgres://user:pass@host:port/db?sslmode=require URI into the
// semicolon key=value form Npgsql expects.
// SSL: managed Postgres providers usually require SSL but present a CA Npgsql
// doesn't trust by default, so with no sslmode (or sslmode=require) this sets
// SSL Mode=Require;Trust Server Certificate=true. Pass ?sslmode=disable for a
// database on the same machine/private network that has SSL switched off, or
// ?sslmode=prefer to let Npgsql negotiate.
static string ConvertPostgresUrlToNpgsqlConnectionString(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var database = uri.AbsolutePath.TrimStart('/');
    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
    var sslMode = query.TryGetValue("sslmode", out var sslModeValues) ? sslModeValues.ToString() : null;

    var npgsqlBuilder = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        Database = database
    };

    if (string.IsNullOrEmpty(sslMode) || sslMode.Equals("require", StringComparison.OrdinalIgnoreCase))
    {
        npgsqlBuilder.SslMode = Npgsql.SslMode.Require;
        npgsqlBuilder.TrustServerCertificate = true;
    }
    else if (sslMode.Equals("disable", StringComparison.OrdinalIgnoreCase))
    {
        npgsqlBuilder.SslMode = Npgsql.SslMode.Disable;
    }
    else if (sslMode.Equals("prefer", StringComparison.OrdinalIgnoreCase))
    {
        npgsqlBuilder.SslMode = Npgsql.SslMode.Prefer;
        npgsqlBuilder.TrustServerCertificate = true;
    }

    return npgsqlBuilder.ConnectionString;
}
