using System.Security.Claims;
using System.Text.Json;
using Campana.Core;
using Campana.Server.Security;
using Campana.Storage.FileSystem;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Campana.Server.Infrastructure;

public static class ErrorCodes
{
    public static (int Status, string Code) For(ErrorCode c) => c switch
    {
        ErrorCode.ValidationError => (400, "validation_error"),
        ErrorCode.NotFound => (404, "not_found"),
        ErrorCode.Conflict => (409, "conflict"),
        ErrorCode.CampaignConflict => (409, "campaign_conflict"),
        ErrorCode.CampaignClosed => (409, "campaign_closed"),
        ErrorCode.CampaignState => (409, "campaign_state"),
        ErrorCode.IdentityConflict => (409, "identity_conflict"),
        ErrorCode.DuplicateCandidates => (409, "duplicate_candidates"),
        ErrorCode.FileTooLarge => (413, "file_too_large"),
        ErrorCode.FileTypeNotAllowed => (415, "file_type_not_allowed"),
        ErrorCode.IntegrityError => (422, "integrity_error"),
        ErrorCode.InsufficientStorage => (507, "insufficient_storage"),
        ErrorCode.Forbidden => (403, "forbidden"),
        _ => (500, "internal_error"),
    };

    public static string ForStatus(int status) => status switch
    {
        400 => "validation_error",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        405 => "method_not_allowed",
        409 => "conflict",
        413 => "file_too_large",
        415 => "file_type_not_allowed",
        422 => "integrity_error",
        429 => "too_many_requests",
        507 => "insufficient_storage",
        _ => status >= 500 ? "internal_error" : "error",
    };
}

/// <summary>Traduce las excepciones de dominio a ProblemDetails (RFC 7807) con un campo "code".</summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _log;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log) { _next = next; _log = log; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (DomainException ex) when (!ctx.Response.HasStarted)
        {
            var (status, code) = ErrorCodes.For(ex.Code);
            await WriteAsync(ctx, status, code, ex.Message, ex.Details);
        }
        catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            await WriteAsync(ctx, ex.StatusCode, ErrorCodes.ForStatus(ex.StatusCode), "Solicitud inválida.", null);
        }
        catch (JsonException) when (!ctx.Response.HasStarted)
        {
            await WriteAsync(ctx, 400, "validation_error", "El cuerpo de la solicitud no es un JSON válido.", null);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* el cliente cerró la conexión */ }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            // Nunca se devuelven detalles internos ni datos de pacientes.
            _log.LogError(ex, "Error no controlado en {Method} {Path}", ctx.Request.Method, ctx.Request.Path.Value);
            await WriteAsync(ctx, 500, "internal_error", "Error interno del servidor.", null);
        }
    }

    private static Task WriteAsync(HttpContext ctx, int status, string code, string detail, object? details)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://httpstatuses.com/{status}",
            ["title"] = code,
            ["status"] = status,
            ["code"] = code,
            ["detail"] = detail,
        };
        if (details is not null) body["errors"] = details;
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, CampanaJson.Compact));
    }
}

/// <summary>Limita el tamaño del cuerpo: 1 MB en general y el máximo configurado (más margen) en subidas de adjuntos.</summary>
public sealed class BodyLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly long _attachmentLimit;

    public BodyLimitMiddleware(RequestDelegate next, IOptions<CampanaOptions> opts)
    {
        _next = next;
        _attachmentLimit = opts.Value.MaxAttachmentBytes + 64 * 1024;
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            var isUpload = HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.Value!.Contains("/attachments", StringComparison.Ordinal);
            feature.MaxRequestBodySize = isUpload ? _attachmentLimit : 1024 * 1024;
        }
        return _next(ctx);
    }
}

public sealed class AuditService
{
    private readonly IAuditLog _log;
    private readonly IClock _clock;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAuditLog log, IClock clock, ILogger<AuditService> logger) { _log = log; _clock = clock; _logger = logger; }

    /// <summary>Registra quién hizo qué. No incluye contenido clínico. Un fallo al auditar no revierte lo ya escrito, pero se avisa.</summary>
    public async Task RecordAsync(ClaimsPrincipal principal, string accion, string recurso, string? campaignId, string? consultorioId = null, CancellationToken ct = default)
    {
        try
        {
            var user = CurrentUser.From(principal);
            await _log.WriteAsync(new AuditEntry(_clock.UtcNow, user.Username, consultorioId ?? user.ConsultorioId, accion, recurso, campaignId), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo escribir la auditoría de {Accion}", accion);
        }
    }
}

/// <summary>Prepara la raíz de datos, crea el primer administrador y ejecuta una verificación antes de aceptar solicitudes.</summary>
public sealed class StartupInitializer : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly CampanaOptions _opts;
    private readonly ILogger<StartupInitializer> _log;

    public StartupInitializer(IServiceProvider sp, IOptions<CampanaOptions> opts, ILogger<StartupInitializer> log) { _sp = sp; _opts = opts.Value; _log = log; }

    public async Task StartAsync(CancellationToken ct)
    {
        var host = _sp.GetService<DataRootHost>();
        if (host is not null)
        {
            var report = host.Initialize();
            _log.LogInformation("Raíz de datos lista. Temporales eliminados: {Removed}. Esquemas sembrados: {Seeded}.", report.TempFilesRemoved, report.SchemasSeeded);
        }
        BootstrapAdmin();
        var consistency = _sp.GetRequiredService<IConsistencyService>();
        var result = await consistency.RunAsync(ct);
        _log.LogInformation("Verificación inicial: {Checked} pacientes revisadas, {Repaired} reparadas, {Errors} errores.",
            result.PacientesRevisadas, result.PacientesReparadas, result.Errores.Count);
    }

    private void BootstrapAdmin()
    {
        if (!string.Equals(_opts.Storage, "FileSystem", StringComparison.OrdinalIgnoreCase)) return;
        var path = Path.Combine(Path.GetFullPath(_opts.DataRoot), "_config", "users.json");
        if (File.Exists(path))
        {
            FileUserStore.Validate(CampanaJson.Deserialize<List<UserRecord>>(File.ReadAllBytes(path)) ?? new());
            return;
        }
        var password = _opts.AdminPassword;
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated) password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18)).Replace('+', 'A').Replace('/', 'B');
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CampanaJson.SerializeToUtf8(new List<UserRecord> { new("admin", Roles.CoordinadorGeneral, null, PasswordHasher.Hash(password!)) }));
        if (generated) _log.LogWarning("Se creó el usuario 'admin'. Contraseña inicial (se muestra una sola vez, cámbiela): {Password}", password);
        else _log.LogInformation("Se creó el usuario 'admin' con la contraseña configurada.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Ejecuta la verificación de consistencia cada N minutos.</summary>
public sealed class ConsistencyHostedService : BackgroundService
{
    private readonly IConsistencyService _consistency;
    private readonly CampanaOptions _opts;
    private readonly ILogger<ConsistencyHostedService> _log;

    public ConsistencyHostedService(IConsistencyService consistency, IOptions<CampanaOptions> opts, ILogger<ConsistencyHostedService> log)
    { _consistency = consistency; _opts = opts.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_opts.ConsistencyIntervalMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_opts.ConsistencyIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var r = await _consistency.RunAsync(stoppingToken);
                    if (r.PacientesReparadas > 0 || r.Errores.Count > 0 || r.IndiceReconstruido)
                        _log.LogWarning("Verificación: {Repaired} reparadas, {Errors} errores, índice reconstruido: {Index}.", r.PacientesReparadas, r.Errores.Count, r.IndiceReconstruido);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Falló la verificación periódica de consistencia.");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
