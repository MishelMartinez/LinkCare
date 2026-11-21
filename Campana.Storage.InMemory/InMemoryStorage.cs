using System.Security.Cryptography;
using Campana.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Campana.Storage.InMemory;

public sealed class InMemoryOptions
{
    public long MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;
    public string[] AllowedAttachmentTypes { get; set; } = { "application/pdf", "image/jpeg", "image/png" };
}

internal sealed record StoredEvent(EventDocument Doc, string Ruta);
internal sealed record StoredAttachment(byte[] Data, string ContentType);

internal sealed class PatientData
{
    public PatientIdentityDocument Identity { get; }
    public Dictionary<string, List<StoredEvent>> Campaigns { get; } = new();
    public PatientData(PatientIdentityDocument identity) => Identity = identity;
}

internal sealed class CampaignEntry
{
    public Campaign Campaign;
    public int Inflight;
    public bool Closing;
    public readonly object Gate = new();
    public CampaignEntry(Campaign c) => Campaign = c;
}

/// <summary>Estado compartido por todos los almacenes en memoria. Un solo candado global: es para pruebas y demostraciones.</summary>
public sealed class InMemoryState
{
    internal readonly object Gate = new();
    internal readonly Dictionary<string, CampaignEntry> Campaigns = new();
    internal readonly Dictionary<string, PatientData> Patients = new();
    internal readonly Dictionary<string, RequestRecord> Requests = new();
    internal readonly List<PatientLink> Links = new();
    internal readonly Dictionary<string, StoredAttachment> Attachments = new();
    internal readonly Dictionary<string, List<(CampaignDocument Doc, byte[] Data)>> Documents = new();

    internal string Resolve(string id)
    {
        var current = id;
        var visited = new HashSet<string> { current };
        while (true)
        {
            var next = Links.FirstOrDefault(l => l.IsActive && l.FromId == current);
            if (next is null || !visited.Add(next.ToId)) return current;
            current = next.ToId;
        }
    }

    internal List<string> Group(string canonical)
    {
        var ids = new HashSet<string> { canonical };
        foreach (var l in Links.Where(l => l.IsActive)) ids.Add(l.FromId);
        return ids.Where(i => Resolve(i) == canonical).ToList();
    }

    internal List<EventDocument> UpdateEvents(IEnumerable<string> ids)
        => ids.Where(Patients.ContainsKey).SelectMany(id => Patients[id].Campaigns.Values.SelectMany(l => l))
            .Where(e => e.Doc.Tipo == EventTypes.ActualizacionDatos).Select(e => e.Doc)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.EventId, StringComparer.Ordinal).ToList();

    internal PatientIdentityDocument Merged(PatientData p)
        => IdentityRules.Apply(p.Identity, IdentityRules.ToUpdates(UpdateEvents(new[] { p.Identity.PatientId })));

    internal PatientIndexEntry ToEntry(PatientData p)
        => PatientIndexing.ToEntry(Merged(p), p.Campaigns.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());

    internal List<PatientIndexEntry> Entries() => Patients.Values.Select(ToEntry).ToList();

    internal static string RequestKey(string cid, string req) => cid + "/" + req;
}

public sealed class InMemoryCampaignStore : ICampaignStore
{
    private readonly InMemoryState _s;
    private readonly IClock _clock;
    public InMemoryCampaignStore(InMemoryState s, IClock clock) { _s = s; _clock = clock; }

    public Task<Campaign> CreateAsync(NewCampaign request, string createdBy, CancellationToken ct)
    {
        CampaignRules.Validate(request);
        var campaign = CampaignRules.Build(request, _clock.UtcNow, createdBy);
        lock (_s.Gate)
        {
            if (_s.Campaigns.ContainsKey(campaign.CampaignId)) throw DomainException.Conflict($"Ya existe la campaña '{campaign.CampaignId}'.");
            _s.Campaigns[campaign.CampaignId] = new CampaignEntry(campaign);
        }
        return Task.FromResult(campaign);
    }

    private CampaignEntry Entry(string campaignId)
    {
        Ids.RequireCampaignId(campaignId);
        lock (_s.Gate) return _s.Campaigns.TryGetValue(campaignId, out var found) ? found : throw DomainException.NotFound($"No existe la campaña '{campaignId}'.");
    }

    public Task<Campaign?> GetAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        lock (_s.Gate) return Task.FromResult(_s.Campaigns.TryGetValue(campaignId, out var e) ? Snapshot(e) : null);
    }

    public Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct)
    {
        lock (_s.Gate)
            return Task.FromResult<IReadOnlyList<Campaign>>(_s.Campaigns.Values.Select(Snapshot)
                .OrderBy(c => c.FechaInicio).ThenBy(c => c.CampaignId, StringComparer.Ordinal).ToList());
    }

    public Task<Campaign?> GetActiveAsync(CancellationToken ct)
    {
        lock (_s.Gate)
            foreach (var e in _s.Campaigns.Values)
                lock (e.Gate) if (e.Campaign.IsOperating) return Task.FromResult<Campaign?>(e.Campaign);
        return Task.FromResult<Campaign?>(null);
    }

    private static Campaign Snapshot(CampaignEntry e) { lock (e.Gate) return e.Campaign; }

    public async Task<Campaign> TransitionAsync(string campaignId, string to, string by, CancellationToken ct)
    {
        var e = Entry(campaignId);
        Campaign next;
        lock (_s.Gate)
        {
            lock (e.Gate) next = CampaignRules.Advance(e.Campaign, to, by, _clock.UtcNow);
            if (to == CampaignStates.Concientizacion)
            {
                var other = _s.Campaigns.Values.FirstOrDefault(o => o != e && Snapshot(o).IsOperating);
                if (other is not null)
                    throw new DomainException(ErrorCode.CampaignConflict, $"Ya hay una campaña en campo ('{Snapshot(other).CampaignId}'). Concluya esa antes de iniciar otra.");
            }
            if (to != CampaignStates.Concluida) { lock (e.Gate) e.Campaign = next; return next; }
            lock (e.Gate) e.Closing = true;
        }
        try
        {
            while (true)
            {
                lock (e.Gate) if (e.Inflight == 0) break;
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            lock (e.Gate) e.Campaign = next;
            return next;
        }
        finally { lock (e.Gate) e.Closing = false; }
    }

    internal ICampaignLease AcquireLease(string campaignId)
    {
        var e = Entry(campaignId);
        lock (e.Gate)
        {
            if (e.Closing) throw new DomainException(ErrorCode.CampaignClosed, $"La campaña '{campaignId}' se está concluyendo.");
            if (!e.Campaign.AllowsClinicalWrites) throw CampaignRules.NotWritable(e.Campaign);
            e.Inflight++;
            return new Lease(e.Campaign, () => { lock (e.Gate) e.Inflight--; });
        }
    }

    public Task<ICampaignLease> AcquireWriteLeaseAsync(string campaignId, CancellationToken ct) => Task.FromResult(AcquireLease(campaignId));

    private sealed class Lease : ICampaignLease
    {
        private Action? _release;
        public Campaign Campaign { get; }
        public Lease(Campaign c, Action release) { Campaign = c; _release = release; }
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref _release, null)?.Invoke(); return ValueTask.CompletedTask; }
    }
}

public sealed class InMemoryCampaignDocumentStore : ICampaignDocumentStore
{
    private readonly InMemoryState _s;
    private readonly InMemoryCampaignStore _campaigns;
    private readonly InMemoryOptions _opts;
    private readonly IClock _clock;
    public InMemoryCampaignDocumentStore(InMemoryState s, InMemoryCampaignStore campaigns, InMemoryOptions opts, IClock clock) { _s = s; _campaigns = campaigns; _opts = opts; _clock = clock; }

    public async Task<CampaignDocument> AddAsync(string campaignId, DocumentRequest request, Stream content, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        if (string.IsNullOrWhiteSpace(request.Tipo) || request.Tipo.Length > 60) throw DomainException.Validation("El tipo de documento es obligatorio (hasta 60 caracteres).");
        if (request.Descripcion is { Length: > 1000 }) throw DomainException.Validation("La descripción admite hasta 1000 caracteres.");
        var campaign = await _campaigns.GetAsync(campaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        if (campaign.IsConcluded) throw new DomainException(ErrorCode.CampaignClosed, "La campaña ya concluyó: no se agregan documentos.");

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            AttachmentRules.EnsureSize(ms.Length + read, _opts.MaxAttachmentBytes);
            ms.Write(buffer, 0, read);
        }
        var data = ms.ToArray();
        var detected = AttachmentRules.CheckContent(data.AsSpan(0, Math.Min(16, data.Length)), data.Length, request.ContentTypeDeclarado, request.NombreOriginal,
            _opts.AllowedAttachmentTypes.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var id = Ids.NewDocumentId();
        var doc = new CampaignDocument(id, request.Tipo.Trim(), request.Descripcion?.Trim(), AttachmentRules.SanitizeName(request.NombreOriginal), detected.ContentType,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), data.Length, request.SubidoPor, _clock.UtcNow, $"documentos/{id}.{detected.Extension}");
        lock (_s.Gate)
        {
            if (!_s.Documents.TryGetValue(campaignId, out var list)) _s.Documents[campaignId] = list = new();
            list.Add((doc, data));
        }
        return doc;
    }

    public Task<IReadOnlyList<CampaignDocument>> ListAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        lock (_s.Gate)
            return Task.FromResult<IReadOnlyList<CampaignDocument>>(_s.Documents.TryGetValue(campaignId, out var l)
                ? l.Select(x => x.Doc).OrderBy(d => d.SubidoEn).ThenBy(d => d.DocumentId, StringComparer.Ordinal).ToList() : new List<CampaignDocument>());
    }

    public Task<DocumentContent?> OpenAsync(string campaignId, string documentId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        Ids.RequireDocumentId(documentId);
        lock (_s.Gate)
        {
            var found = _s.Documents.TryGetValue(campaignId, out var l) ? l.FirstOrDefault(x => x.Doc.DocumentId == documentId) : default;
            return Task.FromResult<DocumentContent?>(found.Doc is null ? null : new DocumentContent(found.Doc, new MemoryStream(found.Data, writable: false)));
        }
    }
}

public sealed class InMemoryPatientStore : IPatientStore
{
    private readonly InMemoryState _s;
    private readonly InMemoryCampaignStore _campaigns;
    private readonly IClock _clock;
    public InMemoryPatientStore(InMemoryState s, InMemoryCampaignStore campaigns, IClock clock) { _s = s; _campaigns = campaigns; _clock = clock; }

    public Task<RegisterPatientResult> RegisterAsync(string campaignId, RegisterPatientRequest request, string createdBy, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        PatientRules.ValidateIdentity(request.Identity, _clock.UtcNow);
        using var lease = new SyncLease(_campaigns.AcquireLease(campaignId));
        lock (_s.Gate)
        {
            if (request.UsarPacienteId is not null)
            {
                Ids.RequirePatientId(request.UsarPacienteId);
                if (!_s.Patients.ContainsKey(request.UsarPacienteId)) throw DomainException.NotFound("No existe la paciente indicada.");
                var canonical = _s.Resolve(request.UsarPacienteId);
                _s.Patients[canonical].Campaigns.TryAdd(campaignId, new List<StoredEvent>());
                return Task.FromResult(new RegisterPatientResult(RegisterOutcome.LinkedExisting, canonical, Array.Empty<PatientCandidate>()));
            }

            var index = _s.Entries();
            var candidates = PatientSearch.Candidates(index, request.Identity, _s.Resolve, id => index.FirstOrDefault(e => e.PatientId == id));
            if (candidates.Count > 0 && !request.ConfirmarNueva)
                return Task.FromResult(new RegisterPatientResult(RegisterOutcome.DuplicateCandidates, null, candidates));

            var pid = Ids.NewPatientId();
            var data = new PatientData(PatientRules.BuildIdentity(pid, request.Identity, campaignId, _clock.UtcNow, createdBy));
            data.Campaigns[campaignId] = new List<StoredEvent>();
            _s.Patients[pid] = data;
            return Task.FromResult(new RegisterPatientResult(RegisterOutcome.Created, pid, Array.Empty<PatientCandidate>()));
        }
    }

    public Task<IReadOnlyList<PatientCandidate>> FindMatchesAsync(PatientIdentity identity, CancellationToken ct)
    {
        lock (_s.Gate)
        {
            var index = _s.Entries();
            return Task.FromResult<IReadOnlyList<PatientCandidate>>(PatientSearch.Candidates(index, identity, _s.Resolve, id => index.FirstOrDefault(e => e.PatientId == id)));
        }
    }

    public Task<PatientRecord?> GetAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        lock (_s.Gate)
        {
            var canonical = _s.Resolve(patientId);
            if (!_s.Patients.TryGetValue(canonical, out var data)) return Task.FromResult<PatientRecord?>(null);
            var group = _s.Group(canonical);
            var events = group.Where(_s.Patients.ContainsKey)
                .SelectMany(id => _s.Patients[id].Campaigns.SelectMany(c => c.Value))
                .Select(se => StateBuilder.Summarize(se.Doc, se.Ruta));
            var ordered = StateBuilder.Order(events);
            var updates = IdentityRules.ToUpdates(_s.UpdateEvents(group));
            return Task.FromResult<PatientRecord?>(new PatientRecord(canonical, IdentityRules.Apply(data.Identity, updates),
                group.Where(g => g != canonical).OrderBy(g => g, StringComparer.Ordinal).ToList(), ordered.Count, ordered, updates.Count, data.Campaigns.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList()));
        }
    }

    public Task<IReadOnlyList<PatientSummary>> SearchAsync(PatientQuery query, CancellationToken ct)
    {
        lock (_s.Gate) return Task.FromResult<IReadOnlyList<PatientSummary>>(PatientSearch.Filter(_s.Entries(), query, _s.Resolve));
    }

    public Task LinkAsync(PatientLink link, CancellationToken ct)
    {
        Ids.RequirePatientId(link.FromId);
        Ids.RequirePatientId(link.ToId);
        if (string.IsNullOrWhiteSpace(link.LinkedBy)) throw DomainException.Validation("Debe indicarse quién confirma el vínculo.");
        if (string.IsNullOrWhiteSpace(link.Motivo)) throw DomainException.Validation("Indique el motivo: un vínculo siempre lo confirma una persona.");
        lock (_s.Gate)
        {
            foreach (var id in new[] { link.FromId, link.ToId })
                if (!_s.Patients.ContainsKey(id)) throw DomainException.NotFound($"No existe la paciente {id}.");
            var from = _s.Resolve(link.FromId);
            var to = _s.Resolve(link.ToId);
            if (from == to) return Task.CompletedTask;
            _s.Links.Add(link with { FromId = from, ToId = to, LinkedAt = link.LinkedAt == default ? _clock.UtcNow : link.LinkedAt });
        }
        return Task.CompletedTask;
    }

    public Task UnlinkAsync(string patientId, string revokedBy, string motivo, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        if (string.IsNullOrWhiteSpace(motivo)) throw DomainException.Validation("Indique el motivo para deshacer el vínculo.");
        lock (_s.Gate)
        {
            var i = _s.Links.FindIndex(l => l.IsActive && l.FromId == patientId);
            if (i < 0) throw DomainException.NotFound("Ese expediente no tiene un vínculo activo.");
            _s.Links[i] = _s.Links[i] with { RevokedBy = revokedBy, RevokedAt = _clock.UtcNow, RevokeMotivo = motivo };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PatientLink>> ListLinksAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        lock (_s.Gate)
        {
            var ids = new HashSet<string>(_s.Group(_s.Resolve(patientId))) { patientId };
            return Task.FromResult<IReadOnlyList<PatientLink>>(_s.Links.Where(l => ids.Contains(l.FromId) || ids.Contains(l.ToId)).OrderBy(l => l.LinkedAt).ToList());
        }
    }

    public Task<bool> IsRegisteredInCampaignAsync(string patientId, string campaignId, CancellationToken ct)
    {
        lock (_s.Gate) return Task.FromResult(_s.Patients.TryGetValue(patientId, out var p) && p.Campaigns.ContainsKey(campaignId));
    }

    public Task<string> ResolveCanonicalIdAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        lock (_s.Gate)
        {
            if (!_s.Patients.ContainsKey(patientId)) throw DomainException.NotFound("No existe la paciente.");
            return Task.FromResult(_s.Resolve(patientId));
        }
    }

    private sealed class SyncLease : IDisposable
    {
        private readonly ICampaignLease _l;
        public SyncLease(ICampaignLease l) => _l = l;
        public void Dispose() => _l.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public sealed class InMemoryEventStore : IEventStore
{
    private readonly InMemoryState _s;
    private readonly InMemoryCampaignStore _campaigns;
    private readonly ISchemaValidator _validator;
    private readonly IClock _clock;

    public InMemoryEventStore(InMemoryState s, InMemoryCampaignStore campaigns, ISchemaValidator validator, IClock clock)
    { _s = s; _campaigns = campaigns; _validator = validator; _clock = clock; }

    public Task<AppendResult> AppendAsync(NewEvent evt, CancellationToken ct) => Task.FromResult(Append(evt, allowReserved: false, commit: null));

    internal RequestRecord? FindRequest(string cid, string req)
    {
        lock (_s.Gate) return _s.Requests.TryGetValue(InMemoryState.RequestKey(cid, req), out var r) ? r : null;
    }

    internal EventDocument? ReadEvent(string pid, string ruta)
    {
        lock (_s.Gate)
            return _s.Patients.TryGetValue(pid, out var p) ? p.Campaigns.Values.SelectMany(l => l).FirstOrDefault(e => e.Ruta == ruta)?.Doc : null;
    }

    private AppendResult Duplicate(RequestRecord prior)
    {
        lock (_s.Gate)
        {
            var version = _s.Patients.TryGetValue(prior.PatientId, out var p) ? p.Campaigns.Values.Sum(l => l.Count) : 0;
            return new AppendResult(prior.EventId, prior.Ruta, true, version);
        }
    }

    internal AppendResult Append(NewEvent evt, bool allowReserved, Action? commit)
    {
        EventRules.ValidateShape(evt, allowReserved);
        var prior = FindRequest(evt.CampaignId, evt.ClientRequestId);
        if (prior is not null) return Duplicate(prior);

        string patientId;
        lock (_s.Gate)
        {
            if (!_s.Patients.ContainsKey(evt.PatientId)) throw DomainException.NotFound("No existe la paciente.");
            patientId = _s.Resolve(evt.PatientId);
        }

        var validation = _validator.ValidatePayload(evt.Tipo, evt.TipoSchemaVersion, evt.Payload);
        if (!validation.IsValid) throw DomainException.Validation("El contenido no cumple el esquema.", validation.Errors);

        var lease = _campaigns.AcquireLease(evt.CampaignId);
        try
        {
            var campaign = lease.Campaign;
            var zone = EventPaths.ResolveZone(campaign.ZonaHoraria);
            lock (_s.Gate)
            {
                if (campaign.Consultorios.All(c => c.ConsultorioId != evt.ConsultorioId))
                    throw DomainException.NotFound($"El consultorio '{evt.ConsultorioId}' no existe en la campaña.");
                var patient = _s.Patients[patientId];
                if (!patient.Campaigns.TryGetValue(evt.CampaignId, out var list))
                    throw DomainException.NotFound("La paciente no está registrada en esta campaña.");

                prior = FindRequest(evt.CampaignId, evt.ClientRequestId);
                if (prior is not null) return Duplicate(prior);

                var known = _s.Group(patientId).Where(_s.Patients.ContainsKey).SelectMany(id => _s.Patients[id].Campaigns.Values.SelectMany(l => l)).ToList();
                if (evt.Tipo == EventTypes.Correccion)
                {
                    var target = EventRules.CorrectsEventId(evt.Payload);
                    if (known.All(e => e.Doc.EventId != target))
                        throw DomainException.Validation("correctsEventId no corresponde a una atención de la paciente.");
                }
                if (evt.VisitId is not null && !known.Any(e => e.Doc.EventId == evt.VisitId && e.Doc.Tipo == EventTypes.Visita && e.Doc.CampaignId == evt.CampaignId))
                    throw DomainException.Validation("visitId no corresponde a una visita de la paciente en esta campaña.");
                if (evt.Tipo == EventTypes.ActualizacionDatos)
                {
                    var (baseVersion, cambios) = IdentityRules.ParsePayload(evt.Payload);
                    IdentityRules.CheckConflicts(IdentityRules.ToUpdates(_s.UpdateEvents(_s.Group(patientId))), baseVersion, cambios);
                }

                commit?.Invoke();
                var eventId = Ids.NewEventId();
                var createdAt = _clock.UtcNow;
                if (patient.Campaigns.Values.SelectMany(l => l).Select(e => e.Doc.CreatedAt).DefaultIfEmpty(default).Max() is var last && createdAt <= last)
                    createdAt = last.AddTicks(1); // sin empates: el orden de los eventos define las versiones de identidad
                var ruta = EventPaths.RelativePath(evt.CampaignId, zone, createdAt, evt.Tipo, evt.ConsultorioId, eventId);
                var doc = new EventDocument(1, eventId, evt.ClientRequestId, evt.Tipo, evt.TipoSchemaVersion, patientId, evt.CampaignId,
                    evt.ConsultorioId, evt.CreatedBy, createdAt, evt.Payload.Clone(), evt.VisitId);
                list.Add(new StoredEvent(doc, ruta));
                _s.Requests[InMemoryState.RequestKey(evt.CampaignId, evt.ClientRequestId)] = new RequestRecord(eventId, patientId, ruta, createdAt);
                return new AppendResult(eventId, ruta, false, patient.Campaigns.Values.Sum(l => l.Count));
            }
        }
        finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public Task<IReadOnlyList<EventSummary>> ListAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        lock (_s.Gate)
        {
            if (!_s.Patients.TryGetValue(patientId, out var p)) return Task.FromResult<IReadOnlyList<EventSummary>>(new List<EventSummary>());
            return Task.FromResult<IReadOnlyList<EventSummary>>(StateBuilder.Order(p.Campaigns.Values.SelectMany(l => l).Select(e => StateBuilder.Summarize(e.Doc, e.Ruta))));
        }
    }

    public Task<EventDocument?> GetAsync(string patientId, string eventId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        Ids.RequireEventId(eventId);
        lock (_s.Gate)
            return Task.FromResult(_s.Patients.TryGetValue(patientId, out var p)
                ? p.Campaigns.Values.SelectMany(l => l).FirstOrDefault(e => e.Doc.EventId == eventId)?.Doc : null);
    }
}

public sealed class InMemoryAttachmentStore : IAttachmentStore
{
    private readonly InMemoryState _s;
    private readonly InMemoryCampaignStore _campaigns;
    private readonly InMemoryEventStore _events;
    private readonly InMemoryOptions _opts;

    public InMemoryAttachmentStore(InMemoryState s, InMemoryCampaignStore campaigns, InMemoryEventStore events, InMemoryOptions opts)
    { _s = s; _campaigns = campaigns; _events = events; _opts = opts; }

    public async Task<AttachmentResult> SaveAsync(AttachmentRequest request, Stream content, CancellationToken ct)
    {
        AttachmentRules.ValidateRequest(request);
        var prior = _events.FindRequest(request.CampaignId, request.ClientRequestId);
        if (prior is not null) return DuplicateOf(prior);

        string pid;
        lock (_s.Gate)
        {
            if (!_s.Patients.ContainsKey(request.PatientId)) throw DomainException.NotFound("No existe la paciente.");
            pid = _s.Resolve(request.PatientId);
        }
        var campaign = await _campaigns.GetAsync(request.CampaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        if (!campaign.AllowsClinicalWrites) throw CampaignRules.NotWritable(campaign);

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            AttachmentRules.EnsureSize(ms.Length + read, _opts.MaxAttachmentBytes);
            ms.Write(buffer, 0, read);
        }
        var data = ms.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var detected = AttachmentRules.CheckContent(data.AsSpan(0, Math.Min(16, data.Length)), data.Length, request, _opts.AllowedAttachmentTypes.ToHashSet(StringComparer.OrdinalIgnoreCase));
        AttachmentRules.CheckHash(request.Sha256Esperado, hash);

        var attachmentId = Ids.NewAttachmentId();
        var rutaRelativa = $"campaigns/{request.CampaignId}/media/{attachmentId}.{detected.Extension}";
        var payload = AttachmentRules.BuildPayload(attachmentId, hash, data.Length, detected.ContentType, request.NombreOriginal, rutaRelativa);
        var evt = new NewEvent(request.ClientRequestId, EventTypes.Adjunto, 1, pid, request.CampaignId, request.ConsultorioId, request.CreatedBy, payload, request.VisitId);
        var result = _events.Append(evt, allowReserved: true, commit: () => _s.Attachments[$"{pid}/{request.CampaignId}/{attachmentId}"] = new StoredAttachment(data, detected.ContentType));
        if (result.Duplicado) return DuplicateOf(new RequestRecord(result.EventId, pid, result.Ruta, default));
        return new AttachmentResult(attachmentId, result.EventId, hash, data.Length, detected.ContentType, rutaRelativa, false);
    }

    private AttachmentResult DuplicateOf(RequestRecord prior)
    {
        var doc = _events.ReadEvent(prior.PatientId, prior.Ruta) ?? throw DomainException.NotFound("No se encontró el adjunto original.");
        var p = doc.Payload;
        return new AttachmentResult(p.GetProperty("attachmentId").GetString()!, doc.EventId, p.GetProperty("sha256").GetString()!,
            p.GetProperty("tamano").GetInt64(), p.GetProperty("contentType").GetString()!, p.GetProperty("rutaRelativa").GetString()!, true);
    }

    public Task<AttachmentContent?> OpenAsync(string patientId, string campaignId, string attachmentId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        Ids.RequireCampaignId(campaignId);
        Ids.RequireAttachmentId(attachmentId);
        lock (_s.Gate)
        {
            foreach (var id in _s.Group(_s.Resolve(patientId)))
                if (_s.Attachments.TryGetValue($"{id}/{campaignId}/{attachmentId}", out var a))
                    return Task.FromResult<AttachmentContent?>(new AttachmentContent(new MemoryStream(a.Data, writable: false), a.ContentType));
        }
        return Task.FromResult<AttachmentContent?>(null);
    }
}

/// <summary>En memoria no hay archivos que reconstruir; existe para cumplir el contrato.</summary>
public sealed class InMemoryRebuildService : IRebuildService
{
    private readonly InMemoryState _s;
    public InMemoryRebuildService(InMemoryState s) => _s = s;
    public Task RebuildPatientAsync(string patientId, CancellationToken ct)
    {
        lock (_s.Gate) if (!_s.Patients.ContainsKey(patientId)) throw DomainException.NotFound("No existe la paciente.");
        return Task.CompletedTask;
    }
    public Task RebuildIndexesAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<int> RebuildAllAsync(CancellationToken ct) { lock (_s.Gate) return Task.FromResult(_s.Patients.Count); }
}

public sealed class InMemoryConsistencyService : IConsistencyService
{
    private readonly InMemoryState _s;
    private readonly IClock _clock;
    private volatile ConsistencyReport? _last;
    public InMemoryConsistencyService(InMemoryState s, IClock clock) { _s = s; _clock = clock; }
    public ConsistencyReport? LastReport => _last;
    public Task<ConsistencyReport> RunAsync(CancellationToken ct)
    {
        int n;
        lock (_s.Gate) n = _s.Patients.Count;
        var r = new ConsistencyReport(_clock.UtcNow, n, 0, false, 0, Array.Empty<string>(), Array.Empty<string>());
        _last = r;
        return Task.FromResult(r);
    }
}

public sealed class InMemoryHistoryImporter : IHistoryImporter
{
    public Task<ImportResult> ImportAsync(string packagePath, string activeCampaignId, CancellationToken ct)
        => throw DomainException.Validation("La importación de historial no está disponible con almacenamiento en memoria.");
    public Task<int> PurgeImportedAsync(string originCampaignId, CancellationToken ct) => Task.FromResult(0);
}

public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = new();
    public IReadOnlyList<AuditEntry> Entries { get { lock (_entries) return _entries.ToList(); } }
    public Task WriteAsync(AuditEntry entry, CancellationToken ct) { lock (_entries) _entries.Add(entry); return Task.CompletedTask; }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery q, CancellationToken ct)
    {
        lock (_entries)
            return Task.FromResult<IReadOnlyList<AuditEntry>>(_entries
                .Where(e => (q.CampaignId is null || e.CampaignId == q.CampaignId) && (q.Desde is null || e.Hora >= q.Desde) && (q.Hasta is null || e.Hora <= q.Hasta)
                            && (q.Usuario is null || e.Usuario == q.Usuario) && (q.Accion is null || e.Accion.StartsWith(q.Accion, StringComparison.Ordinal))
                            && (q.Recurso is null || e.Recurso == q.Recurso))
                .OrderByDescending(e => e.Hora).Take(Math.Clamp(q.Limit, 1, 1000)).ToList());
    }
}

public sealed class InMemoryStatsService : IStatsService
{
    private readonly InMemoryState _s;
    private readonly ICampaignStore _campaigns;
    private readonly IClock _clock;
    public InMemoryStatsService(InMemoryState s, ICampaignStore campaigns, IClock clock) { _s = s; _campaigns = campaigns; _clock = clock; }

    public async Task<CampaignStats> GetCampaignStatsAsync(string campaignId, CancellationToken ct)
    {
        var campaign = await _campaigns.GetAsync(campaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        lock (_s.Gate)
        {
            var registradas = 0;
            var activities = new List<PatientActivity>();
            foreach (var (pid, p) in _s.Patients)
            {
                if (!p.Campaigns.TryGetValue(campaignId, out var mine)) continue;
                registradas++;
                if (mine.Count == 0) continue;
                var first = mine.Min(e => e.Doc.CreatedAt);
                var recurrente = _s.Group(_s.Resolve(pid)).Where(_s.Patients.ContainsKey)
                    .SelectMany(id => _s.Patients[id].Campaigns.Where(c => c.Key != campaignId).SelectMany(c => c.Value))
                    .Any(e => e.Doc.CreatedAt < first);
                activities.Add(new PatientActivity(pid, mine.Select(e => e.Doc).ToList(), recurrente));
            }
            return StatsCalculator.Compute(campaign, registradas, activities, _clock.UtcNow);
        }
    }

    public async Task<IReadOnlyList<UserActivity>> GetActivityByUserAsync(string campaignId, CancellationToken ct)
    {
        _ = await _campaigns.GetAsync(campaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        lock (_s.Gate)
            return StatsCalculator.ByUser(_s.Patients.Values
                .Where(p => p.Campaigns.ContainsKey(campaignId))
                .SelectMany(p => p.Campaigns[campaignId]).Select(e => StateBuilder.Summarize(e.Doc, e.Ruta)));
    }
}

public sealed class InMemoryMinicubeBuilder : IMinicubeBuilder
{
    public Task<MinicubeResult> BuildAsync(MinicubeRequest request, string createdBy, CancellationToken ct)
        => throw DomainException.Validation("El minicubo no está disponible con almacenamiento en memoria.");
    public Task<IReadOnlyList<MinicubeResult>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MinicubeResult>>(new List<MinicubeResult>());
    public Task<bool> WriteZipAsync(string minicubeId, Stream destination, CancellationToken ct) => Task.FromResult(false);
}

public sealed class InMemoryFileShareSync : IFileShareSync
{
    public bool IsConfigured => false;
    public Task<SyncReport> SyncCampaignAsync(string campaignId, CancellationToken ct)
        => throw DomainException.Conflict("La sincronización con el fileshare no está disponible con almacenamiento en memoria.");
    public Task<SyncReport?> LastReportAsync(string campaignId, CancellationToken ct) => Task.FromResult<SyncReport?>(null);
}

public static class InMemoryServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryStorage(this IServiceCollection services, InMemoryOptions? options = null)
    {
        options ??= new InMemoryOptions();
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton(options);
        services.AddSingleton<InMemoryState>();
        services.AddSingleton<ISchemaValidator>(_ => new JsonSchemaValidator(new DictionarySchemaSource()));
        services.AddSingleton<InMemoryCampaignStore>();
        services.AddSingleton<ICampaignStore>(sp => sp.GetRequiredService<InMemoryCampaignStore>());
        services.AddSingleton<IPatientStore, InMemoryPatientStore>();
        services.AddSingleton<InMemoryEventStore>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<InMemoryEventStore>());
        services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        services.AddSingleton<ICampaignDocumentStore, InMemoryCampaignDocumentStore>();
        services.AddSingleton<IStatsService, InMemoryStatsService>();
        services.AddSingleton<IMinicubeBuilder, InMemoryMinicubeBuilder>();
        services.AddSingleton<IFileShareSync, InMemoryFileShareSync>();
        services.AddSingleton<IRebuildService, InMemoryRebuildService>();
        services.AddSingleton<IConsistencyService, InMemoryConsistencyService>();
        services.AddSingleton<IHistoryImporter, InMemoryHistoryImporter>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        services.TryAddSingleton<IDiskSpaceProbe>(new MemoryDiskProbe());
        return services;
    }

    private sealed class MemoryDiskProbe : IDiskSpaceProbe { public long FreeBytes() => long.MaxValue; }
}
