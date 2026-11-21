using Campana.Core;
using Campana.Server.Infrastructure;
using Campana.Server.Reports;
using Campana.Server.Security;

namespace Campana.Server.Endpoints;

public static partial class ApiEndpoints
{
    private static void MapCampaigns(WebApplication app)
    {
        var g = app.MapGroup("/campaigns").WithTags("Campañas");

        // El coordinador general registra la campaña cuando el gobierno asigna el espacio (etapa "asignada").
        g.MapPost("/", async (CreateCampaignRequest req, ICampaignStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Lugar) || string.IsNullOrWhiteSpace(req.ZonaHoraria) || req.Consultorios is null)
                throw DomainException.Validation("lugar, zonaHoraria y consultorios son obligatorios.");
            var user = CurrentUser.From(http.User);
            var campaign = await store.CreateAsync(new NewCampaign(req.Lugar, req.FechaInicio, req.FechaFin, req.ZonaHoraria, req.Consultorios, req.Formularios, req.UnidadGeografica), user.Username, ct);
            await audit.RecordAsync(http.User, "campaign.create", $"campaign:{campaign.CampaignId}", campaign.CampaignId, ct: ct);
            return Results.Created($"/campaigns/{campaign.CampaignId}", campaign);
        }).RequireAuthorization(Policies.GlobalAdmin);

        // General: todas. Los demás perfiles: solo las asignadas.
        g.MapGet("/", async (ICampaignStore store, HttpContext http, string? estado, string? claveGeografica, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            var list = (await store.ListAsync(ct)).Where(c => user.CanAccessCampaign(c.CampaignId));
            if (!string.IsNullOrWhiteSpace(estado)) list = list.Where(c => c.Estado == estado);
            if (!string.IsNullOrWhiteSpace(claveGeografica)) list = list.Where(c => c.UnidadGeografica.Clave == Normalizer.Slug(claveGeografica));
            return Results.Ok(list.ToList());
        }).RequireAuthorization(Policies.Authenticated);

        // Las campañas con las cifras de su tarjeta (lista y kanban). Solo coordinación; cada quien ve las suyas.
        g.MapGet("/summary", async (ICampaignStore store, IStatsService stats, IUserStore users, ICampaignDocumentStore docs, IFileShareSync sync, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            var campaigns = (await store.ListAsync(ct)).Where(c => user.CanAccessCampaign(c.CampaignId)).ToList();
            var allUsers = await users.ListAsync(ct);
            var result = new List<CampaignSummary>();
            foreach (var c in campaigns)
            {
                var s = await stats.GetCampaignStatsAsync(c.CampaignId, ct);
                var documents = (await docs.ListAsync(c.CampaignId, ct)).Count;
                var report = await sync.LastReportAsync(c.CampaignId, ct);
                var medicos = allUsers.Count(u => u.Role == Roles.Medico && u.Activo && (u.Campanas?.Contains(c.CampaignId) ?? false));
                var signals = new CampaignSignals(
                    s.CasosSospechosos,
                    !c.IsConcluded && documents == 0 ? 1 : 0,
                    report?.Conflictos.Count ?? 0,
                    sync.IsConfigured && c.IsConcluded && (report is null || !report.Completo) ? 1 : 0);
                result.Add(new CampaignSummary(c, medicos, s.PacientesRegistradas, s.PacientesAtendidas, s.PacientesNuevas, s.PacientesRecurrentes,
                    s.Visitas, s.Estudios, s.CasosSospechosos, documents, sync.IsConfigured,
                    report is null ? null : new SyncSummary(report.Hora, report.Completo, report.Subidos, report.Conflictos.Count, report.Errores.Count), signals));
            }
            return Results.Ok(result);
        }).RequireAuthorization(Policies.CampaignAdmin);

        // Los médicos que participan en la campaña y lo que han hecho en ella.
        g.MapGet("/{campaignId}/medicos", async (string campaignId, ICampaignStore store, IStatsService stats, IUserStore users, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            var campaign = await store.GetAsync(campaignId, ct);
            if (campaign is null) return NotFound("No existe la campaña.");
            var activity = (await stats.GetActivityByUserAsync(campaignId, ct)).ToDictionary(a => a.Usuario, StringComparer.Ordinal);
            var all = await users.ListAsync(ct);
            string? RoomName(string? id) => id is null ? null : campaign.Consultorios.FirstOrDefault(c => c.ConsultorioId == id)?.Nombre;

            var assigned = all.Where(u => u.Role == Roles.Medico && (u.Campanas?.Contains(campaignId) ?? false)).ToList();
            var views = assigned.Select(u =>
            {
                activity.TryGetValue(u.Username, out var a);
                return new MedicoView(u.Username, u.ConsultorioId, RoomName(u.ConsultorioId), u.Activo, true,
                    a?.Visitas ?? 0, a?.Estudios ?? 0, a?.Adjuntos ?? 0, a?.CambiosDeDatos ?? 0, a?.Pacientes ?? 0, a?.UltimaActividad);
            }).ToList();
            // Quien capturó pero ya no está asignado (o se desactivó) sigue apareciendo: su trabajo cuenta.
            foreach (var a in activity.Values.Where(a => assigned.All(u => u.Username != a.Usuario)))
            {
                var u = all.FirstOrDefault(x => x.Username == a.Usuario);
                views.Add(new MedicoView(a.Usuario, u?.ConsultorioId, RoomName(u?.ConsultorioId), u?.Activo ?? false, false,
                    a.Visitas, a.Estudios, a.Adjuntos, a.CambiosDeDatos, a.Pacientes, a.UltimaActividad));
            }
            return Results.Ok(views.OrderBy(v => v.ConsultorioId, StringComparer.Ordinal).ThenBy(v => v.Username, StringComparer.Ordinal).ToList());
        }).RequireAuthorization(Policies.CampaignAdmin);

        // La campaña que está en campo ahora (concientización o consulta), si el usuario tiene acceso.
        g.MapGet("/active", async (ICampaignStore store, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            var c = await store.GetActiveAsync(ct);
            return c is not null && user.CanAccessCampaign(c.CampaignId) ? Results.Ok(c) : Problem(404, "not_found", "No hay una campaña en campo.");
        }).RequireAuthorization(Policies.Authenticated);

        g.MapGet("/{campaignId}", async (string campaignId, ICampaignStore store, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            return await store.GetAsync(campaignId, ct) is { } c ? Results.Ok(c) : NotFound("No existe la campaña.");
        }).RequireAuthorization(Policies.Authenticated);

        // Avanza una etapa: asignada → concientizacion → consulta → concluida.
        g.MapPost("/{campaignId}/transitions", async (string campaignId, TransitionRequest req, ICampaignStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            if (string.IsNullOrWhiteSpace(req.Estado)) throw DomainException.Validation($"estado es obligatorio. Opciones: {string.Join(", ", CampaignStates.Sequence)}.");
            var c = await store.TransitionAsync(campaignId, req.Estado, user.Username, ct);
            await audit.RecordAsync(http.User, $"campaign.stage {c.Estado}", $"campaign:{c.CampaignId}", c.CampaignId, ct: ct);
            return Results.Ok(c);
        }).RequireAuthorization(Policies.CampaignAdmin);

        // Atajo: concluir la campaña.
        g.MapPost("/{campaignId}/close", async (string campaignId, ICampaignStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            var c = await store.TransitionAsync(campaignId, CampaignStates.Concluida, user.Username, ct);
            await audit.RecordAsync(http.User, "campaign.stage concluida", $"campaign:{c.CampaignId}", c.CampaignId, ct: ct);
            return Results.Ok(c);
        }).RequireAuthorization(Policies.CampaignAdmin);
    }

    // ---------------------------------------------------------------- documentos legales (etapa "asignada" en adelante)
    private static void MapCampaignDocuments(WebApplication app)
    {
        var g = app.MapGroup("/campaigns/{campaignId}/documents").WithTags("Documentos de la campaña").RequireAuthorization(Policies.CampaignAdmin);

        // multipart/form-data: campos "tipo" y "descripcion" primero, y el archivo al final.
        g.MapPost("/", async (string campaignId, HttpRequest request, ICampaignDocumentStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            return await ReadUploadAsync(request, async (fields, fileName, contentType, body) =>
            {
                fields.TryGetValue("descripcion", out var descripcion);
                var doc = await store.AddAsync(campaignId, new DocumentRequest(Required(fields, "tipo"), descripcion, fileName, contentType, user.Username), body, ct);
                await audit.RecordAsync(http.User, $"campaign.document tipo={doc.Tipo}", $"document:{doc.DocumentId}", campaignId, ct: ct);
                return Results.Created($"/campaigns/{campaignId}/documents/{doc.DocumentId}", doc);
            }, ct);
        }).DisableAntiforgery();

        g.MapGet("/", async (string campaignId, ICampaignDocumentStore store, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            return Results.Ok(await store.ListAsync(campaignId, ct));
        });

        g.MapGet("/{documentId}", async (string campaignId, string documentId, ICampaignDocumentStore store, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            var content = await store.OpenAsync(campaignId, documentId, ct);
            if (content is null) return NotFound("No existe el documento.");
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Cache-Control"] = "private, no-store";
            return Results.File(content.Content, content.Document.ContentType, fileDownloadName: content.Document.NombreOriginal ?? content.Document.DocumentId);
        });
    }

    // ---------------------------------------------------------------- tablero, Excel y sincronización
    private static void MapReports(WebApplication app)
    {
        // Tablero de una campaña (coordinación de la campaña o general).
        app.MapGet("/campaigns/{campaignId}/stats", async (string campaignId, IStatsService stats, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            return Results.Ok(await stats.GetCampaignStatsAsync(campaignId, ct));
        }).RequireAuthorization(Policies.CampaignAdmin).WithTags("Tablero");

        // Tablero de la coordinación general: todas las campañas, o las indicadas (campaignIds=a,b) o las de una unidad geográfica.
        app.MapGet("/stats", async (string? campaignIds, string? claveGeografica, ICampaignStore campaigns, IStatsService stats, IClock clock, CancellationToken ct) =>
        {
            var list = await SelectCampaignsAsync(campaigns, campaignIds, claveGeografica, ct);
            var each = new List<CampaignStats>();
            foreach (var c in list) each.Add(await stats.GetCampaignStatsAsync(c.CampaignId, ct));
            return Results.Ok(StatsCalculator.Aggregate(each, clock.UtcNow));
        }).RequireAuthorization(Policies.GlobalAdmin).WithTags("Tablero");

        // Excel de una campaña.
        app.MapGet("/campaigns/{campaignId}/export.xlsx", async (string campaignId, IStatsService stats, IClock clock, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            var one = await stats.GetCampaignStatsAsync(campaignId, ct);
            await audit.RecordAsync(http.User, "report.export", $"campaign:{campaignId}", campaignId, ct: ct);
            return Results.File(ExcelExporter.Build(new[] { one }, clock.UtcNow), XlsxType, $"campana-{campaignId}.xlsx");
        }).RequireAuthorization(Policies.CampaignAdmin).WithTags("Tablero");

        // Excel con las campañas seleccionadas para patrocinadores y gobierno.
        app.MapGet("/exports/campaigns.xlsx", async (string? campaignIds, string? claveGeografica, ICampaignStore campaigns, IStatsService stats, IClock clock, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var list = await SelectCampaignsAsync(campaigns, campaignIds, claveGeografica, ct);
            var each = new List<CampaignStats>();
            foreach (var c in list) each.Add(await stats.GetCampaignStatsAsync(c.CampaignId, ct));
            await audit.RecordAsync(http.User, $"report.export campañas={each.Count}", "report:campaigns", null, ct: ct);
            return Results.File(ExcelExporter.Build(each, clock.UtcNow), XlsxType, "campanas.xlsx");
        }).RequireAuthorization(Policies.GlobalAdmin).WithTags("Tablero");

        // Etapa de conclusión: actualizar el fileshare general.
        app.MapPost("/campaigns/{campaignId}/sync", async (string campaignId, IFileShareSync sync, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            var report = await sync.SyncCampaignAsync(campaignId, ct);
            await audit.RecordAsync(http.User, $"sync subidos={report.Subidos} conflictos={report.Conflictos.Count} errores={report.Errores.Count}", $"campaign:{campaignId}", campaignId, ct: ct);
            return Results.Ok(new { report.CampaignId, report.Hora, report.Subidos, report.Omitidos, report.Bytes, report.Conflictos, report.Errores, completo = report.Completo });
        }).RequireAuthorization(Policies.CampaignAdmin).WithTags("Sincronización");

        app.MapGet("/campaigns/{campaignId}/sync", async (string campaignId, IFileShareSync sync, HttpContext http, CancellationToken ct) =>
        {
            CurrentUser.From(http.User).EnsureCampaignAccess(campaignId);
            var report = await sync.LastReportAsync(campaignId, ct);
            return report is null ? NotFound("Esta campaña aún no se ha sincronizado.") : Results.Ok(new { report.CampaignId, report.Hora, report.Subidos, report.Omitidos, report.Bytes, report.Conflictos, report.Errores, completo = report.Completo });
        }).RequireAuthorization(Policies.CampaignAdmin).WithTags("Sincronización");
    }

    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static async Task<List<Campaign>> SelectCampaignsAsync(ICampaignStore campaigns, string? campaignIds, string? claveGeografica, CancellationToken ct)
    {
        var all = await campaigns.ListAsync(ct);
        if (!string.IsNullOrWhiteSpace(campaignIds))
        {
            var wanted = campaignIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            foreach (var id in wanted) Ids.RequireCampaignId(id);
            var missing = wanted.Where(w => all.All(c => c.CampaignId != w)).ToList();
            if (missing.Count > 0) throw DomainException.NotFound($"No existen estas campañas: {string.Join(", ", missing)}.");
            return all.Where(c => wanted.Contains(c.CampaignId)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(claveGeografica)) return all.Where(c => c.UnidadGeografica.Clave == Normalizer.Slug(claveGeografica)).ToList();
        return all.ToList();
    }
}
