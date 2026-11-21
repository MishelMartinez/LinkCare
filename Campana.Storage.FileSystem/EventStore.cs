using System.Text.Json;
using Campana.Core;

namespace Campana.Storage.FileSystem;

internal sealed record RebuildOutcome(bool Changed, int RequestsRecovered, List<string> Errors);

/// <summary>Regenera state.json (de campaña y consolidado) desde los archivos de evento. No toma candados.</summary>
internal sealed class PatientStateRebuilder
{
    private readonly FsContext _ctx;
    private readonly StateFiles _states;

    public PatientStateRebuilder(FsContext ctx, StateFiles states)
    {
        _ctx = ctx;
        _states = states;
    }

    private async Task<T?> TryReadAsync<T>(Func<Task<T?>> read) where T : class
    {
        try { return await read().ConfigureAwait(false); }
        catch (JsonException) { return null; } // un estado ilegible se trata como ausente: se regenera desde los eventos
    }

    public async Task<RebuildOutcome> RebuildAsync(string pid, CancellationToken ct)
    {
        var changed = false;
        var recovered = 0;
        var errors = new List<string>();
        var campaignsDir = _ctx.Paths.PatientCampaignsDir(pid);
        var builtStates = new List<CampaignState>();

        if (Directory.Exists(campaignsDir))
        {
            foreach (var cdir in Directory.EnumerateDirectories(campaignsDir))
            {
                var cid = Path.GetFileName(cdir);
                if (!Ids.IsCampaignId(cid)) continue;

                var summaries = new List<EventSummary>();
                var docs = new List<EventDocument>();
                foreach (var file in Directory.EnumerateFiles(cdir, "*.json", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(cdir, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (rel == "state.json" || rel.StartsWith("media/", StringComparison.Ordinal)) continue;
                    try
                    {
                        var doc = CampanaJson.Deserialize<EventDocument>(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false));
                        if (doc is null || !Ids.IsEventId(doc.EventId)) throw new JsonException("Evento sin eventId válido.");
                        docs.Add(doc);
                        summaries.Add(new EventSummary(doc.EventId, pid, cid, doc.Tipo, doc.ConsultorioId, doc.CreatedAt, _ctx.Paths.ToPatientRelative(pid, file), doc.VisitId, doc.CreatedBy));
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        errors.Add($"{pid}/{rel}: {ex.Message}");
                    }
                }

                var built = StateBuilder.BuildCampaign(pid, cid, summaries);
                var existing = await TryReadAsync(() => _states.ReadCampaignAsync(pid, cid, ct)).ConfigureAwait(false);
                if (existing is null || CampanaJson.Serialize(existing) != CampanaJson.Serialize(built))
                {
                    await _states.WriteCampaignAsync(built, ct).ConfigureAwait(false);
                    changed = true;
                }
                builtStates.Add(built);

                // Solo las campañas locales (con campaign.json) tienen registro de solicitudes y de pacientes atendidas.
                if (File.Exists(_ctx.Paths.CampaignFile(cid)))
                {
                    foreach (var doc in docs)
                    {
                        if (!Ids.IsClientRequestId(doc.ClientRequestId)) continue;
                        var rec = new RequestRecord(doc.EventId, pid, summaries.First(s => s.EventId == doc.EventId).Ruta, doc.CreatedAt);
                        if (await _ctx.Writer.TryWriteJsonImmutableAsync(_ctx.Paths.RequestFile(cid, doc.ClientRequestId), rec, ct).ConfigureAwait(false))
                        {
                            recovered++;
                            changed = true;
                        }
                    }
                    if (built.Eventos.Count > 0)
                        await _states.AddSeenAsync(cid, pid, built.Eventos[0].CreatedAt, ct).ConfigureAwait(false);
                }
            }
        }

        var consolidated = StateBuilder.BuildPatient(pid, builtStates);
        var existingConsolidated = await TryReadAsync(() => _states.ReadPatientAsync(pid, ct)).ConfigureAwait(false);
        if (existingConsolidated is null || CampanaJson.Serialize(existingConsolidated) != CampanaJson.Serialize(consolidated))
        {
            await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.PatientStateFile(pid), consolidated, ct).ConfigureAwait(false);
            changed = true;
        }
        return new RebuildOutcome(changed, recovered, errors);
    }
}

public sealed class FileSystemEventStore : IEventStore
{
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;
    private readonly FileSystemPatientStore _patients;
    private readonly ISchemaValidator _validator;
    private readonly PatientStateRebuilder _rebuilder;

    public FileSystemEventStore(FsContext ctx, ICampaignStore campaigns, FileSystemPatientStore patients, ISchemaValidator validator)
    {
        _ctx = ctx;
        _campaigns = campaigns;
        _patients = patients;
        _validator = validator;
        _rebuilder = new PatientStateRebuilder(ctx, patients.States);
    }

    public Task<AppendResult> AppendAsync(NewEvent evt, CancellationToken ct)
        => AppendCoreAsync(evt, allowReserved: false, commit: null, ct);

    /// <summary>Lectura de una solicitud ya procesada (idempotencia).</summary>
    internal async Task<RequestRecord?> FindRequestAsync(string campaignId, string clientRequestId, CancellationToken ct)
        => await JsonFiles.ReadAsync<RequestRecord>(_ctx.Paths.RequestFile(campaignId, clientRequestId), ct).ConfigureAwait(false);

    internal async Task<EventDocument?> ReadEventAsync(string patientId, string ruta, CancellationToken ct)
    {
        var path = _ctx.Paths.FromPatientRelative(patientId, ruta);
        if (!File.Exists(path)) return null;
        return CampanaJson.Deserialize<EventDocument>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
    }

    /// <param name="commit">Se ejecuta dentro del bloqueo de la paciente, justo antes de escribir el evento (lo usan los adjuntos).</param>
    internal async Task<AppendResult> AppendCoreAsync(NewEvent evt, bool allowReserved, Func<Task>? commit, CancellationToken ct)
    {
        var result = await AppendLockedAsync(evt, allowReserved, commit, ct).ConfigureAwait(false);
        // Con los candados de la paciente ya liberados (el orden es índice → paciente), se actualiza el índice de búsqueda.
        if (!result.Duplicado && evt.Tipo == EventTypes.ActualizacionDatos)
            await _patients.RefreshIndexEntryAsync(await _patients.ResolveCanonicalIdAsync(evt.PatientId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return result;
    }

    private async Task<AppendResult> AppendLockedAsync(NewEvent evt, bool allowReserved, Func<Task>? commit, CancellationToken ct)
    {
        // 1. Validación de forma
        EventRules.ValidateShape(evt, allowReserved);

        // 2. Idempotencia: una solicitud ya procesada devuelve su resultado original, aunque la campaña ya se haya cerrado.
        var prior = await FindRequestAsync(evt.CampaignId, evt.ClientRequestId, ct).ConfigureAwait(false);
        if (prior is not null) return await DuplicateResultAsync(prior, ct).ConfigureAwait(false);

        _ctx.EnsureSpace();
        await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var patientId = await _patients.ResolveCanonicalIdAsync(evt.PatientId, ct).ConfigureAwait(false);

        // 3. Esquema del payload
        var validation = _validator.ValidatePayload(evt.Tipo, evt.TipoSchemaVersion, evt.Payload);
        if (!validation.IsValid) throw DomainException.Validation("El contenido no cumple el esquema.", validation.Errors);

        // 4. Campaña activa, consultorio y paciente registrada en la campaña
        await using var lease = await _campaigns.AcquireWriteLeaseAsync(evt.CampaignId, ct).ConfigureAwait(false);
        var campaign = lease.Campaign;
        if (campaign.Consultorios.All(c => c.ConsultorioId != evt.ConsultorioId))
            throw DomainException.NotFound($"El consultorio '{evt.ConsultorioId}' no existe en la campaña.");
        if (!await _patients.IsRegisteredInCampaignAsync(patientId, evt.CampaignId, ct).ConfigureAwait(false))
            throw DomainException.NotFound("La paciente no está registrada en esta campaña.");

        if (evt.Tipo == EventTypes.Correccion || evt.VisitId is not null)
        {
            var record = await _patients.GetAsync(patientId, ct).ConfigureAwait(false);
            if (evt.Tipo == EventTypes.Correccion)
            {
                var target = EventRules.CorrectsEventId(evt.Payload);
                if (record is null || record.Eventos.All(e => e.EventId != target))
                    throw DomainException.Validation("correctsEventId no corresponde a una atención de la paciente.");
            }
            if (evt.VisitId is not null && (record is null || !record.Eventos.Any(e => e.EventId == evt.VisitId && e.Tipo == EventTypes.Visita && e.CampaignId == evt.CampaignId)))
                throw DomainException.Validation("visitId no corresponde a una visita de la paciente en esta campaña.");
        }

        var zone = EventPaths.ResolveZone(campaign.ZonaHoraria);

        // 5. Escritura bajo candados: solicitud → paciente
        using var reqLock = await _ctx.Locks.AcquireRequestAsync(evt.CampaignId, evt.ClientRequestId, ct).ConfigureAwait(false);
        prior = await FindRequestAsync(evt.CampaignId, evt.ClientRequestId, ct).ConfigureAwait(false);
        if (prior is not null) return await DuplicateResultAsync(prior, ct).ConfigureAwait(false);

        using var patLock = await _ctx.Locks.AcquirePatientAsync(patientId, ct).ConfigureAwait(false);

        var eventId = Ids.NewEventId();
        var createdAt = _ctx.Clock.UtcNow;
        // El orden de los eventos de una paciente (y con él las versiones de identidad) sale de su hora: nunca debe haber empates.
        var known = await _patients.States.ReadPatientAsync(patientId, ct).ConfigureAwait(false);
        if (known is { Eventos.Count: > 0 })
        {
            var last = known.Eventos.Max(e => e.CreatedAt);
            if (createdAt <= last) createdAt = last.AddTicks(1);
        }
        var relPath = EventPaths.RelativePath(evt.CampaignId, zone, createdAt, evt.Tipo, evt.ConsultorioId, eventId);
        var fullPath = _ctx.Paths.FromPatientRelative(patientId, relPath);
        var doc = new EventDocument(1, eventId, evt.ClientRequestId, evt.Tipo, evt.TipoSchemaVersion, patientId, evt.CampaignId,
            evt.ConsultorioId, evt.CreatedBy, createdAt, evt.Payload.Clone(), evt.VisitId);

        // Cambios de identidad: se comprueba el conflicto bajo el candado de la paciente, así dos cambios simultáneos no se pisan.
        if (evt.Tipo == EventTypes.ActualizacionDatos)
        {
            var (baseVersion, cambios) = IdentityRules.ParsePayload(evt.Payload);
            var group = _patients.Links.Group(patientId);
            var existing = IdentityRules.ToUpdates(await _patients.ReadUpdateEventsAsync(group, ct).ConfigureAwait(false));
            IdentityRules.CheckConflicts(existing, baseVersion, cambios);
        }

        if (commit is not null) await commit().ConfigureAwait(false);
        await _ctx.Writer.WriteJsonImmutableAsync(fullPath, doc, ct).ConfigureAwait(false);
        _ctx.Faults.Hit("after-event-write");

        var summary = new EventSummary(eventId, patientId, evt.CampaignId, evt.Tipo, evt.ConsultorioId, createdAt, relPath, evt.VisitId, evt.CreatedBy);
        int version;
        try
        {
            await _ctx.Writer.WriteJsonImmutableAsync(_ctx.Paths.RequestFile(evt.CampaignId, evt.ClientRequestId),
                new RequestRecord(eventId, patientId, relPath, createdAt), ct).ConfigureAwait(false);
            _ctx.Faults.Hit("after-request-record");

            var existing = await _patients.States.ReadCampaignAsync(patientId, evt.CampaignId, ct).ConfigureAwait(false);
            var list = (existing?.Eventos ?? new List<EventSummary>()).Append(summary);
            var cs = StateBuilder.BuildCampaign(patientId, evt.CampaignId, list);
            await _patients.States.WriteCampaignAsync(cs, ct).ConfigureAwait(false);
            _ctx.Faults.Hit("after-campaign-state");

            var consolidated = await _patients.States.RewriteConsolidatedAsync(patientId, ct).ConfigureAwait(false);
            version = consolidated.Version;
            if (cs.Version == 1) await _patients.States.AddSeenAsync(evt.CampaignId, patientId, createdAt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InjectedFaultException and not OperationCanceledException)
        {
            // El evento ya está en disco (fuente de verdad). Se intenta reparar los derivados en el mismo flujo.
            var outcome = await _rebuilder.RebuildAsync(patientId, CancellationToken.None).ConfigureAwait(false);
            var st = await _patients.States.ReadPatientAsync(patientId, CancellationToken.None).ConfigureAwait(false);
            version = st?.Version ?? 0;
            _ = outcome;
        }

        return new AppendResult(eventId, relPath, false, version);
    }

    private async Task<AppendResult> DuplicateResultAsync(RequestRecord prior, CancellationToken ct)
    {
        var st = await _patients.States.ReadPatientAsync(prior.PatientId, ct).ConfigureAwait(false);
        return new AppendResult(prior.EventId, prior.Ruta, true, st?.Version ?? 0);
    }

    public async Task<IReadOnlyList<EventSummary>> ListAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        var st = await _patients.States.ReadPatientAsync(patientId, ct).ConfigureAwait(false);
        return st?.Eventos ?? new List<EventSummary>();
    }

    public async Task<EventDocument?> GetAsync(string patientId, string eventId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        Ids.RequireEventId(eventId);
        var st = await _patients.States.ReadPatientAsync(patientId, ct).ConfigureAwait(false);
        var summary = st?.Eventos.FirstOrDefault(e => e.EventId == eventId);
        if (summary is null) return null;
        return await ReadEventAsync(patientId, summary.Ruta, ct).ConfigureAwait(false);
    }
}
