using System.Text.Json;
using Campana.Core;
using Campana.Server.Infrastructure;
using Campana.Server.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Campana.Server.Endpoints;

public static partial class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        MapHealth(app);
        MapAuth(app);
        MapUsers(app);
        MapCampaigns(app);
        MapCampaignDocuments(app);
        MapReports(app);
        MapPatients(app);
        MapVisitsAndIdentity(app);
        MapEvents(app);
        MapAttachments(app);
        MapTraining(app);
        MapAdmin(app);
    }

    private static IResult Problem(int status, string code, string detail, IDictionary<string, object?>? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://httpstatuses.com/{status}",
            ["title"] = code,
            ["status"] = status,
            ["code"] = code,
            ["detail"] = detail,
        };
        if (extra is not null) foreach (var kv in extra) body[kv.Key] = kv.Value;
        return Results.Json(body, CampanaJson.Compact, "application/problem+json", status);
    }

    private static IResult NotFound(string detail) => Problem(404, "not_found", detail);

    /// <summary>Lee un multipart/form-data: primero los campos de texto y al final el archivo, que se entrega como flujo.</summary>
    private static async Task<IResult> ReadUploadAsync(HttpRequest request, Func<IReadOnlyDictionary<string, string>, string?, string?, Stream, Task<IResult>> onFile, CancellationToken ct)
    {
        if (!request.HasFormContentType || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mt) || mt.Boundary.Length == 0)
            throw DomainException.Validation("Use multipart/form-data.");
        var boundary = HeaderUtilities.RemoveQuotes(mt.Boundary).Value!;
        var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = null };
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
        {
            var cd = section.GetContentDispositionHeader();
            if (cd is null) continue;
            if (cd.IsFormDisposition())
            {
                using var sr = new StreamReader(section.Body, System.Text.Encoding.UTF8, leaveOpen: true);
                var buffer = new char[1100];
                var n = await sr.ReadBlockAsync(buffer, 0, buffer.Length);
                if (n >= buffer.Length) throw DomainException.Validation($"El campo '{cd.Name.Value}' es demasiado largo.");
                fields[HeaderUtilities.RemoveQuotes(cd.Name).Value!] = new string(buffer, 0, n);
                continue;
            }
            if (!cd.IsFileDisposition()) continue;
            var fileName = cd.FileName.HasValue ? cd.FileName.Value : cd.FileNameStar.Value;
            return await onFile(fields, fileName, section.ContentType, section.Body);
        }
        throw DomainException.Validation("No se recibió ningún archivo.");
    }

    private static string Required(IReadOnlyDictionary<string, string> f, string name)
        => f.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw DomainException.Validation($"Envíe el campo '{name}' antes del archivo.");

    // ---------------------------------------------------------------- salud
    private static void MapHealth(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { estado = "ok" }))
            .AllowAnonymous().WithTags("Salud").WithName("Health");

        app.MapGet("/health/details", async (ICampaignStore campaigns, IConsistencyService consistency, IDiskSpaceProbe disk, IFileShareSync share, CancellationToken ct) =>
        {
            var active = await campaigns.GetActiveAsync(ct);
            var last = consistency.LastReport;
            var estado = last is { Errores.Count: > 0 } ? "con_errores" : "ok";
            return Results.Ok(new { estado, espacioLibreBytes = disk.FreeBytes(), campanaActiva = active?.CampaignId, etapa = active?.Estado, fileShareConfigurado = share.IsConfigured, ultimaVerificacion = last });
        }).RequireAuthorization(Policies.CampaignAdmin).WithTags("Salud");
    }

    // ---------------------------------------------------------------- autenticación
    private static void MapAuth(WebApplication app)
    {
        app.MapPost("/auth/login", async (LoginRequest req, IUserStore users, TokenService tokens, LoginThrottle throttle, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password) || req.Username.Length > 100 || req.Password.Length > 200)
                throw DomainException.Validation("Usuario y contraseña son obligatorios.");
            if (throttle.IsLocked(req.Username))
                return Problem(429, "too_many_requests", "Demasiados intentos fallidos. Espere un minuto.");

            var user = await users.FindAsync(req.Username, ct);
            bool ok;
            if (user is null) { PasswordHasher.BurnTime(req.Password); ok = false; }
            else ok = PasswordHasher.Verify(req.Password, user.PasswordHash) && user.Activo;

            if (!ok)
            {
                throttle.Failure(req.Username);
                return Problem(401, "unauthorized", "Usuario o contraseña incorrectos.");
            }
            throttle.Success(req.Username);
            var (token, expires) = tokens.Issue(user!);
            return Results.Ok(new LoginResponse(token, expires, user!.Username, user.Role, user.ConsultorioId, user.Campanas ?? new List<string>()));
        }).AllowAnonymous().WithTags("Autenticación");

        app.MapGet("/auth/me", (HttpContext http) =>
        {
            var u = CurrentUser.From(http.User);
            return Results.Ok(new MeResponse(u.Username, u.Role, u.ConsultorioId, u.Campanas));
        }).RequireAuthorization(Policies.Authenticated).WithTags("Autenticación");
    }

    // ---------------------------------------------------------------- usuarios
    private static void MapUsers(WebApplication app)
    {
        var g = app.MapGroup("/users").WithTags("Usuarios").RequireAuthorization(Policies.GlobalAdmin);

        static UserView View(UserRecord u) => new(u.Username, u.Role, u.ConsultorioId, u.Campanas ?? new List<string>(), u.Activo);

        g.MapGet("/", async (IUserStore store, CancellationToken ct) => Results.Ok((await store.ListAsync(ct)).Select(View)));

        g.MapPost("/", async (CreateUserRequest req, IUserStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (!UserRules.IsValidUsername(req.Username)) throw DomainException.Validation("Nombre de usuario inválido (3 a 40 caracteres: minúsculas, números, punto, guion).");
            if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 10) throw DomainException.Validation("La contraseña debe tener al menos 10 caracteres.");
            if (await store.FindAsync(req.Username!, ct) is not null) throw DomainException.Conflict($"Ya existe el usuario '{req.Username}'.");
            var user = new UserRecord(req.Username!, req.Role ?? "", string.IsNullOrWhiteSpace(req.ConsultorioId) ? null : req.ConsultorioId,
                PasswordHasher.Hash(req.Password), req.Campanas?.Distinct().ToList());
            UserRules.Validate(user);
            await store.UpsertAsync(user, ct);
            await audit.RecordAsync(http.User, $"user.create rol={user.Role}", $"user:{user.Username}", null, ct: ct);
            return Results.Created($"/users/{user.Username}", View(user));
        });

        g.MapPut("/{username}", async (string username, UpdateUserRequest req, IUserStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var me = CurrentUser.From(http.User);
            var current = await store.FindAsync(username, ct);
            if (current is null) return NotFound("No existe el usuario.");
            if (me.Username == username && (req.Activo == false || (req.Role is not null && req.Role != current.Role)))
                throw DomainException.Validation("No puede desactivarse ni cambiarse el rol a usted mismo.");
            if (req.Password is not null && req.Password.Length < 10) throw DomainException.Validation("La contraseña debe tener al menos 10 caracteres.");

            var updated = current with
            {
                Role = req.Role ?? current.Role,
                ConsultorioId = req.ConsultorioId is null ? current.ConsultorioId : (req.ConsultorioId.Length == 0 ? null : req.ConsultorioId),
                Campanas = req.Campanas?.Distinct().ToList() ?? current.Campanas,
                Activo = req.Activo ?? current.Activo,
                PasswordHash = req.Password is null ? current.PasswordHash : PasswordHasher.Hash(req.Password),
            };
            UserRules.Validate(updated);
            await store.UpsertAsync(updated, ct);
            await audit.RecordAsync(http.User, "user.update", $"user:{username}", null, ct: ct);
            return Results.Ok(View(updated));
        });
    }
}
