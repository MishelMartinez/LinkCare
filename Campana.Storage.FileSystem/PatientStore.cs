using Campana.Core;

namespace Campana.Storage.FileSystem;

internal sealed record PatientIndexFile(List<PatientIndexEntry> Pacientes);
internal sealed record PatientLinksFile(List<PatientLink> Vinculos);
internal sealed record SeenEntry(string PatientId, DateTimeOffset PrimeraAtencion);
internal sealed record SeenFile(List<SeenEntry> Pacientes);

/// <summary>Índice de pacientes en memoria, persistido en _global/patients-index.json. Derivado: se puede reconstruir.</summary>
internal sealed class PatientIndex
{
    private readonly FsContext _ctx;
    private readonly Func<string, CancellationToken, Task<PatientIdentityDocument?>> _identityLoader;
    private readonly object _sync = new();
    private readonly Dictionary<string, PatientIndexEntry> _byId = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _loaded;

    public PatientIndex(FsContext ctx, Func<string, CancellationToken, Task<PatientIdentityDocument?>> identityLoader)
    {
        _ctx = ctx;
        _identityLoader = identityLoader;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var file = await JsonFiles.ReadAsync<PatientIndexFile>(_ctx.Paths.PatientsIndexFile, ct).ConfigureAwait(false);
            if (file is not null) Replace(file.Pacientes);
            else await RebuildCoreAsync(ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally { _loadGate.Release(); }
    }

    private void Replace(IEnumerable<PatientIndexEntry> entries)
    {
        lock (_sync)
        {
            _byId.Clear();
            foreach (var e in entries) _byId[e.PatientId] = e;
        }
    }

    public List<PatientIndexEntry> Snapshot()
    {
        lock (_sync) return _byId.Values.ToList();
    }

    public PatientIndexEntry? Get(string patientId)
    {
        lock (_sync) return _byId.TryGetValue(patientId, out var e) ? e : null;
    }

    public void Upsert(PatientIndexEntry entry)
    {
        lock (_sync) _byId[entry.PatientId] = entry;
    }

    public void Remove(string patientId)
    {
        lock (_sync) _byId.Remove(patientId);
    }

    public void AddCampaign(string patientId, string campaignId)
    {
        lock (_sync)
        {
            if (_byId.TryGetValue(patientId, out var e) && !e.Campanas.Contains(campaignId))
                _byId[patientId] = e with { Campanas = e.Campanas.Append(campaignId).OrderBy(x => x, StringComparer.Ordinal).ToList() };
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        PatientIndexFile file;
        lock (_sync) file = new PatientIndexFile(_byId.Values.OrderBy(e => e.PatientId, StringComparer.Ordinal).ToList());
        await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.PatientsIndexFile, file, ct).ConfigureAwait(false);
    }

    /// <summary>Reconstruye desde los directorios. El llamador debe tener el candado de índice (o estar cargando).</summary>
    public async Task RebuildCoreAsync(CancellationToken ct)
    {
        var entries = new List<PatientIndexEntry>();
        if (Directory.Exists(_ctx.Paths.PatientsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.PatientsDir))
            {
                var pid = Path.GetFileName(dir);
                if (!Ids.IsPatientId(pid)) continue;
                var identity = await _identityLoader(pid, ct).ConfigureAwait(false);
                if (identity is null) continue;
                entries.Add(PatientIndexing.ToEntry(identity, CampaignsOf(pid)));
            }
        }
        Replace(entries);
        await SaveAsync(ct).ConfigureAwait(false);
    }

    public List<string> CampaignsOf(string pid)
    {
        var dir = _ctx.Paths.PatientCampaignsDir(pid);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Where(n => Ids.IsCampaignId(n)).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}

internal sealed class PatientLinks
{
    private readonly FsContext _ctx;
    private readonly object _sync = new();
    private readonly List<PatientLink> _links = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _loaded;

    public PatientLinks(FsContext ctx) => _ctx = ctx;

    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var file = await JsonFiles.ReadAsync<PatientLinksFile>(_ctx.Paths.PatientLinksFile, ct).ConfigureAwait(false);
            if (file is not null) lock (_sync) { _links.Clear(); _links.AddRange(file.Vinculos); }
            _loaded = true;
        }
        finally { _loadGate.Release(); }
    }

    public string Resolve(string id)
    {
        lock (_sync)
        {
            var current = id;
            var visited = new HashSet<string> { current };
            while (true)
            {
                var next = _links.FirstOrDefault(l => l.IsActive && l.FromId == current);
                if (next is null || !visited.Add(next.ToId)) return current;
                current = next.ToId;
            }
        }
    }

    public List<string> Group(string canonical)
    {
        lock (_sync)
        {
            var ids = new HashSet<string> { canonical };
            foreach (var l in _links.Where(l => l.IsActive)) ids.Add(l.FromId);
            var result = ids.Where(i => Resolve(i) == canonical).ToList();
            if (!result.Contains(canonical)) result.Add(canonical);
            return result;
        }
    }

    public List<PatientLink> For(IReadOnlyCollection<string> ids)
    {
        lock (_sync) return _links.Where(l => ids.Contains(l.FromId) || ids.Contains(l.ToId)).OrderBy(l => l.LinkedAt).ToList();
    }

    public async Task AddAsync(PatientLink link, CancellationToken ct)
    {
        PatientLinksFile file;
        lock (_sync)
        {
            _links.Add(link);
            file = new PatientLinksFile(_links.ToList());
        }
        await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.PatientLinksFile, file, ct).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string fromId, string by, string motivo, DateTimeOffset now, CancellationToken ct)
    {
        PatientLinksFile file;
        lock (_sync)
        {
            var i = _links.FindIndex(l => l.IsActive && l.FromId == fromId);
            if (i < 0) throw DomainException.NotFound("Ese expediente no tiene un vínculo activo.");
            _links[i] = _links[i] with { RevokedBy = by, RevokedAt = now, RevokeMotivo = motivo };
            file = new PatientLinksFile(_links.ToList());
        }
        await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.PatientLinksFile, file, ct).ConfigureAwait(false);
    }
}

/// <summary>Operaciones sobre los state.json y archivos derivados. No toman candados: el llamador es responsable.</summary>
internal sealed class StateFiles
{
    private readonly FsContext _ctx;
    public StateFiles(FsContext ctx) => _ctx = ctx;

    public Task<CampaignState?> ReadCampaignAsync(string pid, string cid, CancellationToken ct)
        => JsonFiles.ReadAsync<CampaignState>(_ctx.Paths.CampaignStateFile(pid, cid), ct);

    public Task<PatientState?> ReadPatientAsync(string pid, CancellationToken ct)
        => JsonFiles.ReadAsync<PatientState>(_ctx.Paths.PatientStateFile(pid), ct);

    public Task WriteCampaignAsync(CampaignState s, CancellationToken ct)
        => _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.CampaignStateFile(s.PatientId, s.CampaignId), s, ct);

    public async Task<PatientState> RewriteConsolidatedAsync(string pid, CancellationToken ct)
    {
        var states = new List<CampaignState>();
        var dir = _ctx.Paths.PatientCampaignsDir(pid);
        if (Directory.Exists(dir))
        {
            foreach (var cdir in Directory.EnumerateDirectories(dir))
            {
                var cid = Path.GetFileName(cdir);
                if (!Ids.IsCampaignId(cid)) continue;
                var s = await ReadCampaignAsync(pid, cid, ct).ConfigureAwait(false);
                if (s is not null) states.Add(s);
            }
        }
        var consolidated = StateBuilder.BuildPatient(pid, states);
        await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.PatientStateFile(pid), consolidated, ct).ConfigureAwait(false);
        return consolidated;
    }

    public async Task AddSeenAsync(string cid, string pid, DateTimeOffset at, CancellationToken ct)
    {
        using var _ = await _ctx.Locks.AcquireSeenAsync(cid, ct).ConfigureAwait(false);
        var file = await JsonFiles.ReadAsync<SeenFile>(_ctx.Paths.SeenFile(cid), ct).ConfigureAwait(false) ?? new SeenFile(new List<SeenEntry>());
        if (file.Pacientes.Any(p => p.PatientId == pid)) return;
        file.Pacientes.Add(new SeenEntry(pid, at));
        await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.SeenFile(cid), file, ct).ConfigureAwait(false);
    }
}

public sealed class FileSystemPatientStore : IPatientStore
{
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;
    internal PatientIndex Index { get; }
    internal PatientLinks Links { get; }
    internal StateFiles States { get; }

    public FileSystemPatientStore(FsContext ctx, ICampaignStore campaigns)
    {
        _ctx = ctx;
        _campaigns = campaigns;
        States = new StateFiles(ctx);
        Links = new PatientLinks(ctx);
        Index = new PatientIndex(ctx, LoadMergedIdentityAsync);
    }

    internal async Task EnsureLoadedAsync(CancellationToken ct)
    {
        await Index.EnsureLoadedAsync(ct).ConfigureAwait(false);
        await Links.EnsureLoadedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Lee los eventos de actualización de datos de esos expedientes (los más viejos primero).</summary>
    internal async Task<List<EventDocument>> ReadUpdateEventsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var docs = new List<EventDocument>();
        foreach (var id in ids)
        {
            var st = await States.ReadPatientAsync(id, ct).ConfigureAwait(false);
            if (st is null) continue;
            foreach (var s in st.Eventos.Where(e => e.Tipo == EventTypes.ActualizacionDatos))
            {
                var path = _ctx.Paths.FromPatientRelative(id, s.Ruta);
                if (!File.Exists(path)) continue;
                var doc = CampanaJson.Deserialize<EventDocument>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
                if (doc is not null) docs.Add(doc);
            }
        }
        return docs.OrderBy(d => d.CreatedAt).ThenBy(d => d.EventId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Identidad inicial más los cambios posteriores de ese expediente.</summary>
    internal async Task<PatientIdentityDocument?> LoadMergedIdentityAsync(string pid, CancellationToken ct)
    {
        var baseDoc = await JsonFiles.ReadAsync<PatientIdentityDocument>(_ctx.Paths.IdentityFile(pid), ct).ConfigureAwait(false);
        if (baseDoc is null) return null;
        var updates = IdentityRules.ToUpdates(await ReadUpdateEventsAsync(new[] { pid }, ct).ConfigureAwait(false));
        return IdentityRules.Apply(baseDoc, updates);
    }

    /// <summary>Vuelve a calcular la entrada del índice de una paciente (después de cambiar sus datos).</summary>
    internal async Task RefreshIndexEntryAsync(string pid, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        using var _ = await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false);
        var merged = await LoadMergedIdentityAsync(pid, ct).ConfigureAwait(false);
        if (merged is null) return;
        Index.Upsert(PatientIndexing.ToEntry(merged, Index.CampaignsOf(pid)));
        await Index.SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task<RegisterPatientResult> RegisterAsync(string campaignId, RegisterPatientRequest request, string createdBy, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        PatientRules.ValidateIdentity(request.Identity, _ctx.Clock.UtcNow);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        _ctx.EnsureSpace();

        await using var lease = await _campaigns.AcquireWriteLeaseAsync(campaignId, ct).ConfigureAwait(false);
        using var indexLock = await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false);

        if (request.UsarPacienteId is not null)
        {
            Ids.RequirePatientId(request.UsarPacienteId);
            if (Index.Get(request.UsarPacienteId) is null) throw DomainException.NotFound("No existe la paciente indicada.");
            var canonical = Links.Resolve(request.UsarPacienteId);
            using var patLock = await _ctx.Locks.AcquirePatientAsync(canonical, ct).ConfigureAwait(false);
            await EnsureCampaignFolderAsync(canonical, campaignId, ct).ConfigureAwait(false);
            await Index.SaveAsync(ct).ConfigureAwait(false);
            return new RegisterPatientResult(RegisterOutcome.LinkedExisting, canonical, Array.Empty<PatientCandidate>());
        }

        var candidates = PatientSearch.Candidates(Index.Snapshot(), request.Identity, Links.Resolve, Index.Get);
        if (candidates.Count > 0 && !request.ConfirmarNueva)
            return new RegisterPatientResult(RegisterOutcome.DuplicateCandidates, null, candidates);

        var pid = Ids.NewPatientId();
        var now = _ctx.Clock.UtcNow;
        var doc = PatientRules.BuildIdentity(pid, request.Identity, campaignId, now, createdBy);
        using (await _ctx.Locks.AcquirePatientAsync(pid, ct).ConfigureAwait(false))
        {
            await _ctx.Writer.WriteJsonImmutableAsync(_ctx.Paths.IdentityFile(pid), doc, ct).ConfigureAwait(false);
            await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.CampaignStateFile(pid, campaignId), StateBuilder.BuildCampaign(pid, campaignId, Array.Empty<EventSummary>()), ct).ConfigureAwait(false);
            await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.PatientStateFile(pid), StateBuilder.BuildPatient(pid, Array.Empty<CampaignState>()), ct).ConfigureAwait(false);
        }
        Index.Upsert(PatientIndexing.ToEntry(doc, new List<string> { campaignId }));
        await Index.SaveAsync(ct).ConfigureAwait(false);
        return new RegisterPatientResult(RegisterOutcome.Created, pid, Array.Empty<PatientCandidate>());
    }

    public async Task<IReadOnlyList<PatientCandidate>> FindMatchesAsync(PatientIdentity identity, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return PatientSearch.Candidates(Index.Snapshot(), identity, Links.Resolve, Index.Get);
    }

    private async Task EnsureCampaignFolderAsync(string pid, string cid, CancellationToken ct)
    {
        if (!File.Exists(_ctx.Paths.CampaignStateFile(pid, cid)))
        {
            await States.WriteCampaignAsync(StateBuilder.BuildCampaign(pid, cid, Array.Empty<EventSummary>()), ct).ConfigureAwait(false);
            await States.RewriteConsolidatedAsync(pid, ct).ConfigureAwait(false);
        }
        Index.AddCampaign(pid, cid);
    }

    public async Task<string> ResolveCanonicalIdAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (!File.Exists(_ctx.Paths.IdentityFile(patientId))) throw DomainException.NotFound("No existe la paciente.");
        return Links.Resolve(patientId);
    }

    public Task<bool> IsRegisteredInCampaignAsync(string patientId, string campaignId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        Ids.RequireCampaignId(campaignId);
        return Task.FromResult(File.Exists(_ctx.Paths.CampaignStateFile(patientId, campaignId)));
    }

    public async Task<PatientRecord?> GetAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var canonical = Links.Resolve(patientId);
        var baseDoc = await JsonFiles.ReadAsync<PatientIdentityDocument>(_ctx.Paths.IdentityFile(canonical), ct).ConfigureAwait(false);
        if (baseDoc is null) return null;

        var group = Links.Group(canonical);
        var events = new List<EventSummary>();
        foreach (var id in group)
        {
            var st = await States.ReadPatientAsync(id, ct).ConfigureAwait(false);
            if (st is not null) events.AddRange(st.Eventos);
        }
        var ordered = StateBuilder.Order(events);
        var updates = IdentityRules.ToUpdates(await ReadUpdateEventsAsync(group, ct).ConfigureAwait(false));
        var merged = IdentityRules.Apply(baseDoc, updates);
        return new PatientRecord(canonical, merged, group.Where(g => g != canonical).OrderBy(g => g, StringComparer.Ordinal).ToList(), ordered.Count, ordered, updates.Count, Index.CampaignsOf(canonical).OrderBy(c => c, StringComparer.Ordinal).ToList());
    }

    public async Task<IReadOnlyList<PatientSummary>> SearchAsync(PatientQuery query, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return PatientSearch.Filter(Index.Snapshot(), query, Links.Resolve);
    }

    public async Task LinkAsync(PatientLink link, CancellationToken ct)
    {
        Ids.RequirePatientId(link.FromId);
        Ids.RequirePatientId(link.ToId);
        if (string.IsNullOrWhiteSpace(link.LinkedBy)) throw DomainException.Validation("Debe indicarse quién confirma el vínculo.");
        if (string.IsNullOrWhiteSpace(link.Motivo)) throw DomainException.Validation("Indique el motivo: un vínculo siempre lo confirma una persona.");
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        using var _ = await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false);
        foreach (var id in new[] { link.FromId, link.ToId })
            if (!File.Exists(_ctx.Paths.IdentityFile(id))) throw DomainException.NotFound($"No existe la paciente {id}.");
        var from = Links.Resolve(link.FromId);
        var to = Links.Resolve(link.ToId);
        if (from == to) return; // ya vinculadas
        await Links.AddAsync(link with { FromId = from, ToId = to, LinkedAt = link.LinkedAt == default ? _ctx.Clock.UtcNow : link.LinkedAt }, ct).ConfigureAwait(false);
    }

    public async Task UnlinkAsync(string patientId, string revokedBy, string motivo, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        if (string.IsNullOrWhiteSpace(motivo)) throw DomainException.Validation("Indique el motivo para deshacer el vínculo.");
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        using var _ = await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false);
        await Links.RevokeAsync(patientId, revokedBy, motivo, _ctx.Clock.UtcNow, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PatientLink>> ListLinksAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var canonical = Links.Resolve(patientId);
        var ids = new HashSet<string>(Links.Group(canonical)) { patientId };
        return Links.For(ids);
    }
}
