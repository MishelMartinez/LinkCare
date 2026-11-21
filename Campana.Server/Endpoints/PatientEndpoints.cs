using System.Text.Json;
using System.Text.Json.Nodes;
using Campana.Core;
using Campana.Server.Infrastructure;
using Campana.Server.Security;

namespace Campana.Server.Endpoints;

public static partial class ApiEndpoints
{
    private static void MapPatients(WebApplication app)
    {
        // Alta de paciente. Si hay posibles coincidencias responde 409: una persona decide si es la misma o es nueva.
        app.MapPost("/campaigns/{campaignId}/patients", async (string campaignId, RegisterPatientDto dto, IPatientStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Nombres) || string.IsNullOrWhiteSpace(dto.Apellidos))
                throw DomainException.Validation("nombres y apellidos son obligatorios.");
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            var identity = new PatientIdentity(dto.Nombres, dto.Apellidos, dto.FechaNacimiento, dto.Documento, dto.Contacto, dto.EntidadNacimiento);
            var result = await store.RegisterAsync(campaignId, new RegisterPatientRequest(identity, dto.ConfirmarNueva, dto.UsarPacienteId), user.Username, ct);

            switch (result.Outcome)
            {
                case RegisterOutcome.DuplicateCandidates:
                    return Problem(409, "duplicate_candidates",
                        "Hay expedientes que podrían ser la misma persona. Elija uno (usarPacienteId) o confirme que es nueva (confirmarNueva). El sistema nunca los une solo.",
                        new Dictionary<string, object?> { ["candidatas"] = result.Candidatas });
                case RegisterOutcome.Created:
                    await audit.RecordAsync(http.User, "patient.create", $"patient:{result.PatientId}", campaignId, ct: ct);
                    return Results.Created($"/patients/{result.PatientId}", new RegisterPatientResponse("created", result.PatientId, result.Candidatas));
                default:
                    await audit.RecordAsync(http.User, "patient.join-campaign", $"patient:{result.PatientId}", campaignId, ct: ct);
                    return Results.Ok(new RegisterPatientResponse("linkedExisting", result.PatientId, result.Candidatas));
            }
        }).RequireAuthorization(Policies.ClinicalWrite).WithTags("Pacientes");

        var g = app.MapGroup("/patients").WithTags("Pacientes").RequireAuthorization(Policies.ClinicalRead);

        g.MapGet("/", async (string? q, string? campaignId, int? limit, IPatientStore store, CancellationToken ct) =>
            Results.Ok(await store.SearchAsync(new PatientQuery(q, campaignId, limit ?? 50), ct)));

        // Búsqueda de coincidencias por varios atributos, antes de registrar. Solo informa.
        g.MapGet("/matches", async (string? nombres, string? apellidos, DateOnly? fechaNacimiento, string? documento, string? telefono, string? celular, string? localidad,
            IPatientStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(nombres) || string.IsNullOrWhiteSpace(apellidos) || fechaNacimiento is null)
                throw DomainException.Validation("nombres, apellidos y fechaNacimiento son obligatorios.");
            var identity = new PatientIdentity(nombres, apellidos, fechaNacimiento.Value,
                string.IsNullOrWhiteSpace(documento) ? null : new DocumentoInfo(null, documento), new ContactoInfo(telefono, localidad, celular));
            return Results.Ok(await store.FindMatchesAsync(identity, ct));
        });

        g.MapGet("/{patientId}", async (string patientId, IPatientStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var r = await store.GetAsync(patientId, ct);
            if (r is null) return NotFound("No existe la paciente.");
            await audit.RecordAsync(http.User, "patient.view", $"patient:{r.PatientId}", null, ct: ct);
            return Results.Ok(r);
        });

        g.MapGet("/{patientId}/events", async (string patientId, IPatientStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var r = await store.GetAsync(patientId, ct);
            if (r is null) return NotFound("No existe la paciente.");
            await audit.RecordAsync(http.User, "patient.view", $"patient:{r.PatientId}", null, ct: ct);
            return Results.Ok(r.Eventos);
        });

        g.MapGet("/{patientId}/events/{eventId}", async (string patientId, string eventId, IPatientStore patients, IEventStore events, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            Ids.RequireEventId(eventId);
            var record = await patients.GetAsync(patientId, ct);
            var summary = record?.Eventos.FirstOrDefault(e => e.EventId == eventId);
            if (summary is null) return NotFound("No existe la atención.");
            var doc = await events.GetAsync(summary.PatientId, eventId, ct);
            if (doc is null) return NotFound("No existe la atención.");
            await audit.RecordAsync(http.User, "patient.view", $"patient:{record!.PatientId}", null, ct: ct);
            return Results.Ok(doc);
        });

        // Historial por visitas: cada visita con los servicios que se dieron en ella, de todas las campañas.
        g.MapGet("/{patientId}/visits", async (string patientId, IPatientStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var r = await store.GetAsync(patientId, ct);
            if (r is null) return NotFound("No existe la paciente.");
            await audit.RecordAsync(http.User, "patient.view", $"patient:{r.PatientId}", null, ct: ct);
            return Results.Ok(VisitsBuilder.Build(r.PatientId, r.Eventos));
        });

        // Vínculos entre expedientes: siempre los confirma una persona de la coordinación y se pueden deshacer.
        g.MapGet("/{patientId}/links", async (string patientId, IPatientStore store, CancellationToken ct) =>
            Results.Ok(await store.ListLinksAsync(patientId, ct)));

        g.MapPost("/{patientId}/links", async (string patientId, LinkRequest req, IPatientStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ToId)) throw DomainException.Validation("toId es obligatorio.");
            if (!req.Confirmado) throw DomainException.Validation("Un vínculo debe confirmarlo una persona: envíe confirmado=true después de revisar ambos expedientes.");
            var user = CurrentUser.From(http.User);
            await store.LinkAsync(new PatientLink(patientId, req.ToId, user.Username, default, req.Motivo), ct);
            await audit.RecordAsync(http.User, "patient.link", $"patient:{patientId}->{req.ToId}", null, ct: ct);
            return Results.Ok(await store.GetAsync(patientId, ct));
        }).RequireAuthorization(Policies.CampaignAdmin);

        g.MapPost("/{patientId}/unlink", async (string patientId, UnlinkRequest req, IPatientStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            await store.UnlinkAsync(patientId, user.Username, req.Motivo ?? "", ct);
            await audit.RecordAsync(http.User, "patient.unlink", $"patient:{patientId}", null, ct: ct);
            return Results.Ok(await store.ListLinksAsync(patientId, ct));
        }).RequireAuthorization(Policies.CampaignAdmin);

        // Bitácora de cambios del expediente: quién hizo qué y cuándo.
        g.MapGet("/{patientId}/changelog", async (string patientId, IPatientStore patients, IEventStore events, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var r = await patients.GetAsync(patientId, ct);
            if (r is null) return NotFound("No existe la paciente.");
            await audit.RecordAsync(http.User, "patient.view", $"patient:{r.PatientId}", null, ct: ct);
            var entries = new List<ChangeEntry>();
            entries.Add(new ChangeEntry(r.Identity.CreadaEn, r.Identity.CreadaPor ?? "(sin dato)", null, r.Identity.CreadaEnCampana, "alta", "Alta del expediente"));
            foreach (var e in r.Eventos)
            {
                var text = e.Tipo switch
                {
                    EventTypes.Visita => "Visita",
                    EventTypes.Mama => "Revisión mamaria",
                    EventTypes.Papanicolaou => "Papanicolaou",
                    EventTypes.Adjunto => "Archivo adjunto",
                    EventTypes.Correccion => "Corrección de una atención",
                    EventTypes.Registro => "Registro",
                    EventTypes.ActualizacionDatos => "Actualización de datos",
                    _ => e.Tipo,
                };
                if (e.Tipo == EventTypes.ActualizacionDatos)
                {
                    var doc = await events.GetAsync(e.PatientId, e.EventId, ct);
                    if (doc is not null && doc.Payload.TryGetProperty("cambios", out var c) && c.ValueKind == JsonValueKind.Object)
                        text += ": " + string.Join(", ", c.EnumerateObject().Select(p => p.Name)); // solo los nombres de los campos, no los valores
                }
                entries.Add(new ChangeEntry(e.CreatedAt, e.CreatedBy, e.ConsultorioId, e.CampaignId, e.Tipo, text));
            }
            foreach (var l in await patients.ListLinksAsync(patientId, ct))
            {
                entries.Add(new ChangeEntry(l.LinkedAt, l.LinkedBy, null, null, "vinculo", $"Vinculó el expediente {l.FromId} con {l.ToId}. Motivo: {l.Motivo}"));
                if (l.RevokedAt is not null)
                    entries.Add(new ChangeEntry(l.RevokedAt.Value, l.RevokedBy ?? "", null, null, "vinculo-revocado", $"Deshizo el vínculo de {l.FromId}. Motivo: {l.RevokeMotivo}"));
            }
            return Results.Ok(entries.OrderBy(x => x.Fecha).ToList());
        });

        // Quién consultó o modificó el expediente (bitácora de accesos). Solo la coordinación.
        g.MapGet("/{patientId}/access-log", async (string patientId, IAuditLog audit, IPatientStore patients, int? limit, CancellationToken ct) =>
        {
            Ids.RequirePatientId(patientId);
            var canonical = await patients.GetAsync(patientId, ct);
            var id = canonical?.PatientId ?? patientId;
            return Results.Ok(await audit.QueryAsync(new AuditQuery(null, null, null, null, "patient.", $"patient:{id}", limit ?? 200), ct));
        }).RequireAuthorization(Policies.CampaignAdmin);
    }

    // ---------------------------------------------------------------- visitas y cambios de identidad
    private static void MapVisitsAndIdentity(WebApplication app)
    {
        // Abre una visita: la llegada de la paciente. Los servicios que reciba llevan este visitId.
        app.MapPost("/campaigns/{campaignId}/patients/{patientId}/visits", async (string campaignId, string patientId, VisitRequest req,
            IEventStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ClientRequestId) || string.IsNullOrWhiteSpace(req.ConsultorioId))
                throw DomainException.Validation("clientRequestId y consultorioId son obligatorios.");
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            user.EnsureCanWriteAs(req.ConsultorioId);
            var payload = JsonSerializer.SerializeToElement(string.IsNullOrWhiteSpace(req.Motivo) ? new { } : (object)new { motivo = req.Motivo.Trim() }, CampanaJson.Compact);
            var result = await store.AppendAsync(new NewEvent(req.ClientRequestId, EventTypes.Visita, 1, patientId, campaignId, req.ConsultorioId, user.Username, payload), ct);
            if (!result.Duplicado) await audit.RecordAsync(http.User, "visit.create", $"event:{result.EventId}", campaignId, req.ConsultorioId, ct);
            var body = new { visitId = result.EventId, result.Ruta, result.Duplicado, result.Version };
            return result.Duplicado ? Results.Ok(body) : Results.Created($"/patients/{patientId}/visits", body);
        }).RequireAuthorization(Policies.ClinicalWrite).WithTags("Visitas");

        // Actualiza solo los datos que cambiaron. baseVersion es la versión de identidad que vio quien envía el cambio:
        // si otra persona cambió el mismo dato con otro valor desde entonces, responde 409 identity_conflict y no guarda nada.
        app.MapPatch("/campaigns/{campaignId}/patients/{patientId}/identity", async (string campaignId, string patientId, IdentityPatchRequest req,
            IEventStore store, IPatientStore patients, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ClientRequestId) || string.IsNullOrWhiteSpace(req.ConsultorioId) || req.BaseVersion is null)
                throw DomainException.Validation("clientRequestId, consultorioId y baseVersion son obligatorios.");
            if (req.Cambios.ValueKind != JsonValueKind.Object) throw DomainException.Validation("cambios debe ser un objeto con los campos a cambiar.");
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            user.EnsureCanWriteAs(req.ConsultorioId);
            var payload = JsonSerializer.SerializeToElement(new { baseVersion = req.BaseVersion.Value, cambios = req.Cambios }, CampanaJson.Compact);
            var result = await store.AppendAsync(new NewEvent(req.ClientRequestId, EventTypes.ActualizacionDatos, 1, patientId, campaignId, req.ConsultorioId, user.Username, payload), ct);
            if (!result.Duplicado) await audit.RecordAsync(http.User, "patient.update", $"patient:{patientId}", campaignId, req.ConsultorioId, ct);
            var record = await patients.GetAsync(patientId, ct);
            return Results.Ok(new { result.EventId, result.Duplicado, identityVersion = record?.IdentityVersion ?? 0, identidad = record?.Identity });
        }).RequireAuthorization(Policies.ClinicalWrite).WithTags("Pacientes");
    }

    // ---------------------------------------------------------------- servicios (estudios) y adjuntos
    private static void MapEvents(WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId}/patients/{patientId}/events", async (string campaignId, string patientId, JsonElement body,
            IEventStore store, ISchemaValidator validator, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (body.ValueKind != JsonValueKind.Object) throw DomainException.Validation("El cuerpo debe ser un objeto JSON.");

            // El patientId sale de la ruta. Lo demás del sobre lo manda el cliente y se valida con el esquema.
            var envelope = JsonNode.Parse(body.GetRawText())!.AsObject();
            envelope["patientId"] = patientId;
            using var envDoc = JsonDocument.Parse(envelope.ToJsonString());
            var check = validator.ValidateEnvelope(envDoc.RootElement);
            if (!check.IsValid) throw DomainException.Validation("El sobre de la atención no es válido.", check.Errors);

            var e = envDoc.RootElement;
            var consultorioId = e.GetProperty("consultorioId").GetString()!;
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            user.EnsureCanWriteAs(consultorioId);

            var evt = new NewEvent(
                e.GetProperty("clientRequestId").GetString()!,
                e.GetProperty("tipo").GetString()!,
                e.GetProperty("tipoSchemaVersion").GetInt32(),
                patientId, campaignId, consultorioId, user.Username,
                e.GetProperty("payload").Clone(),
                e.TryGetProperty("visitId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null);

            var result = await store.AppendAsync(evt, ct);
            if (!result.Duplicado)
                await audit.RecordAsync(http.User, $"event.create tipo={evt.Tipo}", $"event:{result.EventId}", campaignId, consultorioId, ct);
            var response = new AppendEventResponse(result.EventId, result.Ruta, result.Duplicado, result.Version);
            return result.Duplicado ? Results.Ok(response) : Results.Created($"/patients/{patientId}/events/{result.EventId}", response);
        }).RequireAuthorization(Policies.ClinicalWrite).WithTags("Atenciones");
    }

    private static void MapAttachments(WebApplication app)
    {
        // multipart/form-data: primero los campos (clientRequestId, consultorioId, visitId y sha256 opcionales) y al final el archivo.
        app.MapPost("/campaigns/{campaignId}/patients/{patientId}/attachments", async (string campaignId, string patientId,
            HttpRequest request, IAttachmentStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = CurrentUser.From(http.User);
            user.EnsureCampaignAccess(campaignId);
            return await ReadUploadAsync(request, async (fields, fileName, contentType, body) =>
            {
                var requestId = Required(fields, "clientRequestId");
                var consultorioId = Required(fields, "consultorioId");
                user.EnsureCanWriteAs(consultorioId);
                fields.TryGetValue("sha256", out var sha);
                fields.TryGetValue("visitId", out var visit);
                var result = await store.SaveAsync(new AttachmentRequest(patientId, campaignId, consultorioId, user.Username, requestId, fileName, contentType,
                    string.IsNullOrWhiteSpace(sha) ? null : sha, string.IsNullOrWhiteSpace(visit) ? null : visit), body, ct);
                if (!result.Duplicado)
                    await audit.RecordAsync(http.User, "attachment.create", $"attachment:{result.AttachmentId}", campaignId, consultorioId, ct);
                return result.Duplicado ? Results.Ok(result) : Results.Created($"/campaigns/{campaignId}/patients/{patientId}/attachments/{result.AttachmentId}", result);
            }, ct);
        }).RequireAuthorization(Policies.ClinicalWrite).WithTags("Adjuntos").DisableAntiforgery();

        app.MapGet("/campaigns/{campaignId}/patients/{patientId}/attachments/{attachmentId}", async (string campaignId, string patientId, string attachmentId,
            IAttachmentStore store, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var content = await store.OpenAsync(patientId, campaignId, attachmentId, ct);
            if (content is null) return NotFound("No existe el adjunto.");
            await audit.RecordAsync(http.User, "patient.view-attachment", $"patient:{patientId}", campaignId, ct: ct);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Cache-Control"] = "private, no-store";
            return Results.File(content.Content, content.ContentType, fileDownloadName: attachmentId + (content.ContentType switch { "application/pdf" => ".pdf", "image/png" => ".png", _ => ".jpg" }));
        }).RequireAuthorization(Policies.ClinicalRead).WithTags("Adjuntos");
    }
}
