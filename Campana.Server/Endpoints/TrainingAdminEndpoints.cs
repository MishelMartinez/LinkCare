using System.Security;
using System.Text;
using System.Xml.Linq;
using Campana.Core;
using Campana.Server.Infrastructure;
using Campana.Server.Security;
using Campana.Server.Training;

namespace Campana.Server.Endpoints;

public static partial class ApiEndpoints
{
    // ---------------------------------------------------------------- autocapacitación (pública: las pacientes no inician sesión)
    private static void MapTraining(WebApplication app)
    {
        var g = app.MapGroup("/capacitacion").WithTags("Autocapacitación").AllowAnonymous();

        g.MapGet("/temas", (TrainingCatalog catalog) => Results.Ok(catalog.Topics()));

        g.MapGet("/episodios", (string? tema, TrainingCatalog catalog) =>
        {
            if (tema is not null && !TrainingCatalog.IsValidId(tema)) throw DomainException.Validation("tema inválido.");
            return Results.Ok(catalog.Episodes(tema));
        });

        // Audio con soporte de rangos: se puede adelantar o retomar sin descargar todo.
        g.MapGet("/episodios/{episodeId}/audio", (string episodeId, TrainingCatalog catalog) =>
        {
            var ep = catalog.Episode(episodeId);
            return ep is null ? NotFound("No existe el episodio.") : Results.File(ep.Value.Path, ep.Value.ContentType, enableRangeProcessing: true);
        });

        // Podcast estándar (RSS): se puede suscribir desde cualquier aplicación de podcasts dentro de la red del campamento.
        g.MapGet("/podcast.xml", (string? tema, TrainingCatalog catalog, HttpContext http) =>
        {
            if (tema is not null && !TrainingCatalog.IsValidId(tema)) throw DomainException.Validation("tema inválido.");
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            var topics = catalog.Topics().ToDictionary(t => t.Id);
            var title = tema is not null && topics.TryGetValue(tema, out var t0) ? t0.Titulo : "Prevención y detección oportuna de cáncer de mama y cervicouterino";
            XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
            var channel = new XElement("channel",
                new XElement("title", title),
                new XElement("link", baseUrl + "/capacitacion/"),
                new XElement("language", "es-MX"),
                new XElement("description", "Audios de autocapacitación de EIRA."));
            foreach (var e in catalog.Episodes(tema))
            {
                channel.Add(new XElement("item",
                    new XElement("title", e.Titulo),
                    new XElement("description", e.Descripcion ?? e.Titulo),
                    new XElement("guid", new XAttribute("isPermaLink", "false"), e.Id),
                    new XElement("enclosure", new XAttribute("url", baseUrl + e.Url), new XAttribute("length", e.Bytes), new XAttribute("type", e.ContentType)),
                    new XElement(itunes + "episode", e.Numero)));
            }
            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "itunes", itunes), channel));
            return Results.Text(doc.Declaration + "\n" + doc.Root, "application/rss+xml", Encoding.UTF8);
        });

        g.MapGet("/presentaciones", (TrainingCatalog catalog) => Results.Ok(catalog.Decks()));

        g.MapGet("/presentaciones/{deckId}", (string deckId, TrainingCatalog catalog) =>
            catalog.Deck(deckId) is { } d ? Results.Ok(d) : NotFound("No existe la presentación."));

        // Número de diapositiva desde 1.
        g.MapGet("/presentaciones/{deckId}/diapositivas/{numero:int}", (string deckId, int numero, TrainingCatalog catalog) =>
        {
            var s = catalog.Slide(deckId, numero);
            return s is null ? NotFound("No existe la diapositiva.") : Results.File(s.Value.Path, s.Value.ContentType);
        });

        // Estado de la presentación en grupo. Quien no pueda usar SignalR lo consulta cada pocos segundos.
        app.MapGet("/campaigns/{campaignId}/presentacion", (string campaignId, PresentationCoordinator presentation) =>
        {
            Ids.RequireCampaignId(campaignId);
            return Results.Ok(presentation.Get(campaignId));
        }).AllowAnonymous().WithTags("Autocapacitación");

        // Quien presenta (coordinación de la campaña) cambia la diapositiva; todas las pantallas conectadas lo reciben.
        app.MapPut("/campaigns/{campaignId}/presentacion", async (string campaignId, SetSlideRequest req, PresentationCoordinator presentation, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.DeckId)) throw DomainException.Validation("deckId es obligatorio.");
            return Results.Ok(await presentation.SetAsync(http.User, campaignId, req.DeckId, req.Index, ct));
        }).RequireAuthorization(Policies.CampaignAdmin).WithTags("Autocapacitación");
    }

    // ---------------------------------------------------------------- administración (coordinación general)
    private static void MapAdmin(WebApplication app)
    {
        app.MapPost("/admin/rebuild", async (IRebuildService rebuild, AuditService audit, ICampaignStore campaigns, HttpContext http, CancellationToken ct) =>
        {
            var count = await rebuild.RebuildAllAsync(ct);
            var active = await campaigns.GetActiveAsync(ct);
            await audit.RecordAsync(http.User, $"admin.rebuild pacientes={count}", "system", active?.CampaignId, ct: ct);
            return Results.Ok(new { pacientes = count });
        }).RequireAuthorization(Policies.GlobalAdmin).WithTags("Administración");

        // Bitácora. El coordinador general consulta todo; el de campaña, solo la suya.
        app.MapGet("/audit", async (string? campaignId, string? usuario, string? accion, string? recurso, DateTimeOffset? desde, DateTimeOffset? hasta, int? limit,
            IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            if (campaignId is null && !user.IsGeneral) throw new DomainException(ErrorCode.Forbidden, "Indique campaignId: solo la coordinación general consulta la bitácora completa.");
            if (campaignId is not null) user.EnsureCampaignAccess(campaignId);
            return Results.Ok(await audit.QueryAsync(new AuditQuery(campaignId, desde, hasta, usuario, accion, recurso, limit ?? 200), ct));
        }).RequireAuthorization(Policies.CampaignAdmin).WithTags("Administración");

        app.MapPost("/campaigns/{campaignId}/history-import", async (string campaignId, ImportRequest req, IHistoryImporter importer, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RutaPaquete)) throw DomainException.Validation("rutaPaquete es obligatoria.");
            var result = await importer.ImportAsync(req.RutaPaquete, campaignId, ct);
            await audit.RecordAsync(http.User, $"history.import origen={result.CampanaOrigen} pacientes={result.Pacientes}", $"import:{result.ImportId}", campaignId, ct: ct);
            return Results.Ok(result);
        }).RequireAuthorization(Policies.GlobalAdmin).WithTags("Historial");

        app.MapPost("/history/purge", async (PurgeRequest req, IHistoryImporter importer, ICampaignStore campaigns, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.CampanaOrigen)) throw DomainException.Validation("campanaOrigen es obligatoria.");
            var deleted = await importer.PurgeImportedAsync(req.CampanaOrigen, ct);
            var active = await campaigns.GetActiveAsync(ct);
            await audit.RecordAsync(http.User, $"history.purge origen={req.CampanaOrigen} eliminados={deleted}", $"campaign:{req.CampanaOrigen}", active?.CampaignId, ct: ct);
            return Results.Ok(new { eliminados = deleted });
        }).RequireAuthorization(Policies.GlobalAdmin).WithTags("Historial");

        // Minicubo: el historial concentrado de una unidad geográfica para llevarlo a la siguiente campaña.
        var m = app.MapGroup("/minicubes").WithTags("Historial").RequireAuthorization(Policies.GlobalAdmin);

        m.MapPost("/", async (MinicubeApiRequest req, IMinicubeBuilder builder, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            var result = await builder.BuildAsync(new MinicubeRequest(req.ClaveGeografica, req.CampaignIds, req.IncluirImagenes ?? true, req.SoloConcluidas ?? true), user.Username, ct);
            await audit.RecordAsync(http.User, $"minicube.create pacientes={result.Pacientes} archivos={result.Archivos}", $"minicube:{result.MinicubeId}", null, ct: ct);
            return Results.Created($"/minicubes/{result.MinicubeId}", result);
        });

        m.MapGet("/", async (IMinicubeBuilder builder, CancellationToken ct) => Results.Ok(await builder.ListAsync(ct)));

        m.MapGet("/{minicubeId}/download", async (string minicubeId, IMinicubeBuilder builder, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (!(await builder.ListAsync(ct)).Any(x => x.MinicubeId == minicubeId)) return NotFound("No existe el minicubo.");
            await audit.RecordAsync(http.User, "minicube.download", $"minicube:{minicubeId}", null, ct: ct);
            return Results.Stream(async stream => await builder.WriteZipAsync(minicubeId, stream, ct), "application/zip", $"{minicubeId}.zip");
        });
    }
}
