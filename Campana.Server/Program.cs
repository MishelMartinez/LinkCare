using System.Security.Cryptography.X509Certificates;
using Campana.Core;
using Campana.Server;
using Campana.Server.Endpoints;
using Campana.Server.Infrastructure;
using Campana.Server.Security;
using Campana.Storage.FileSystem;
using Campana.Storage.InMemory;
using Campana.Server.Training;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// Utilidad para el administrador: dotnet run -- hash-password "mi contraseña"
if (args.Length == 2 && args[0] == "hash-password")
{
    Console.WriteLine(PasswordHasher.Hash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var section = builder.Configuration.GetSection(CampanaOptions.Section);
builder.Services.Configure<CampanaOptions>(section);
var opts = section.Get<CampanaOptions>() ?? new CampanaOptions();
var env = builder.Environment;

// ---- almacenamiento (Abstract Factory: el resto de la aplicación solo ve las interfaces)
if (string.Equals(opts.Storage, "FileSystem", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddFileSystemStorage(new FileSystemOptions
    {
        DataRoot = Path.GetFullPath(opts.DataRoot),
        MinFreeDiskBytes = opts.MinFreeDiskBytes,
        MaxAttachmentBytes = opts.MaxAttachmentBytes,
        AllowedAttachmentTypes = opts.AllowedAttachmentTypes,
        FileShareRoot = opts.FileShare.Root,
    });
    builder.Services.AddSingleton<IUserStore>(new FileUserStore(Path.Combine(Path.GetFullPath(opts.DataRoot), "_config", "users.json")));
}
else if (string.Equals(opts.Storage, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddInMemoryStorage(new InMemoryOptions { MaxAttachmentBytes = opts.MaxAttachmentBytes, AllowedAttachmentTypes = opts.AllowedAttachmentTypes });
    var users = opts.Users.Select(u => new UserRecord(u.Username, u.Role, string.IsNullOrWhiteSpace(u.ConsultorioId) ? null : u.ConsultorioId, u.PasswordHash, u.Campanas, u.Activo)).ToList();
    if (users.Count == 0 && !string.IsNullOrWhiteSpace(opts.AdminPassword))
        users.Add(new UserRecord("admin", Roles.CoordinadorGeneral, null, PasswordHasher.Hash(opts.AdminPassword)));
    FileUserStore.Validate(users);
    builder.Services.AddSingleton<IUserStore>(new StaticUserStore(users));
}
else throw new InvalidOperationException($"Campana:Storage inválido: '{opts.Storage}'. Use FileSystem o InMemory.");

builder.Services.AddHostedService<StartupInitializer>(); // primero: prepara el disco antes de aceptar solicitudes
builder.Services.AddHostedService<ConsistencyHostedService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<TrainingCatalog>();
builder.Services.AddSingleton<PresentationService>();
builder.Services.AddSingleton<PresentationCoordinator>();
builder.Services.AddSignalR();

// ---- seguridad
var key = TokenService.BuildKey(opts.Jwt.SigningKey, allowEphemeral: env.IsDevelopment() || env.IsEnvironment("Testing"));
builder.Services.AddSingleton(sp => new TokenService(opts.Jwt, key, sp.GetRequiredService<IClock>()));
builder.Services.AddSingleton(sp => new LoginThrottle(sp.GetRequiredService<IClock>()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = opts.Jwt.Issuer,
        ValidAudience = opts.Jwt.Audience,
        IssuerSigningKey = key,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub",
        RoleClaimType = "role",
    };
    o.Events = new JwtBearerEvents
    {
        // SignalR no puede mandar encabezados desde el navegador: el token viaja en la URL solo para el hub.
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
            return Task.CompletedTask;
        },
        // Un usuario desactivado, o cuyo rol, consultorio o campañas cambiaron, deja de poder usar su sesión anterior.
        OnTokenValidated = async ctx =>
        {
            var name = ctx.Principal?.FindFirst("sub")?.Value;
            var stored = name is null ? null : await ctx.HttpContext.RequestServices.GetRequiredService<IUserStore>().FindAsync(name, ctx.HttpContext.RequestAborted);
            var claimCampaigns = ctx.Principal!.FindAll("campana").Select(c => c.Value).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var storedCampaigns = (stored?.Campanas ?? new List<string>()).OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (stored is null || !stored.Activo
                || stored.Role != ctx.Principal.FindFirst("role")?.Value
                || stored.ConsultorioId != ctx.Principal.FindFirst("consultorioId")?.Value
                || !claimCampaigns.SequenceEqual(storedCampaigns))
                ctx.Fail("La sesión ya no es válida. Inicie sesión de nuevo.");
        },
    };
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.Authenticated, p => p.RequireAuthenticatedUser());
    o.AddPolicy(Policies.ClinicalRead, p => p.RequireRole(Roles.Medico, Roles.CoordinadorCampana, Roles.CoordinadorGeneral));
    o.AddPolicy(Policies.ClinicalWrite, p => p.RequireRole(Roles.Medico));
    o.AddPolicy(Policies.CampaignAdmin, p => p.RequireRole(Roles.CoordinadorCampana, Roles.CoordinadorGeneral));
    o.AddPolicy(Policies.GlobalAdmin, p => p.RequireRole(Roles.CoordinadorGeneral));
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// ---- red: HTTPS en el puerto configurado
builder.WebHost.ConfigureKestrel(k =>
{
    if (env.IsEnvironment("Testing")) return;
    k.ListenAnyIP(opts.Port, lo =>
    {
        if (!opts.Tls.Enabled) return;
        if (!string.IsNullOrWhiteSpace(opts.Tls.CertificatePath))
            lo.UseHttps(new X509Certificate2(opts.Tls.CertificatePath, opts.Tls.CertificatePassword));
        else if (env.IsDevelopment())
            lo.UseHttps(); // certificado de desarrollo (dotnet dev-certs https)
        else
            throw new InvalidOperationException("Falta Campana:Tls:CertificatePath. Para HTTP sin cifrar (solo pruebas) use Campana:Tls:Enabled=false.");
    });
});

// ---- API
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["code"] = ErrorCodes.ForStatus(ctx.ProblemDetails.Status ?? 500));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Servidor de campaña", Version = "v1", Description = "API del servidor local del campamento." });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
        Description = "Token obtenido en POST /auth/login.",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>(),
    });
});

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<BodyLimitMiddleware>();
app.UseStatusCodePages();

// El contrato OpenAPI queda público en /swagger/v1/swagger.json. Va antes de la autorización porque
// la política global (usuario autenticado) también se aplica a las rutas que no son endpoints.
app.UseSwagger();
if (env.IsDevelopment()) app.UseSwaggerUI();

// Sitio público de autocapacitación (wwwroot/capacitacion): las pacientes no inician sesión.
// Interfaz web (wwwroot/app): la CSP solo aplica aquí para no afectar al sitio público de capacitación.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/app"))
    {
        ctx.Response.Headers["Content-Security-Policy"] = AppSecurity.ContentSecurityPolicy;
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", () => Results.Redirect("/app/")).AllowAnonymous().ExcludeFromDescription();
app.MapApi();
app.MapHub<PresentationHub>("/hubs/presentacion").AllowAnonymous();

app.Run();

public partial class Program { }

/// <summary>Cabeceras de seguridad de la interfaz web. Sin recursos externos: todo sale del mismo origen.</summary>
public static class AppSecurity
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; frame-src blob:; connect-src 'self'; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
}
