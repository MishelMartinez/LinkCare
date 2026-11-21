using System.Collections.Concurrent;
using System.Security.Cryptography;
using Campana.Core;

namespace Campana.Storage.FileSystem;

public sealed class FileSystemCampaignStore : ICampaignStore
{
    private sealed class Entry
    {
        public Campaign Campaign;
        public int Inflight;
        public bool Closing;
        public readonly object Gate = new();
        public Entry(Campaign c) => Campaign = c;
    }

    private readonly FsContext _ctx;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _loaded;

    public FileSystemCampaignStore(FsContext ctx) => _ctx = ctx;

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            if (Directory.Exists(_ctx.Paths.CampaignsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.CampaignsDir))
                {
                    var id = Path.GetFileName(dir);
                    if (!Ids.IsCampaignId(id)) continue;
                    var c = await JsonFiles.ReadAsync<Campaign>(_ctx.Paths.CampaignFile(id), ct).ConfigureAwait(false);
                    if (c is not null) _entries[id] = new Entry(c);
                }
            }
            _loaded = true;
        }
        finally { _loadGate.Release(); }
    }

    public async Task<Campaign> CreateAsync(NewCampaign request, string createdBy, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        CampaignRules.Validate(request);
        var campaign = CampaignRules.Build(request, _ctx.Clock.UtcNow, createdBy);
        var id = campaign.CampaignId;

        using var _ = await _ctx.Locks.AcquireCampaignCreationAsync(ct).ConfigureAwait(false);
        if (_entries.ContainsKey(id)) throw DomainException.Conflict($"Ya existe la campaña '{id}'.");

        Directory.CreateDirectory(_ctx.Paths.CampaignDir(id));
        Directory.CreateDirectory(_ctx.Paths.CampaignGlobalDir(id));
        foreach (var c in request.Consultorios)
        {
            var file = _ctx.Safe.Resolve("campaigns", id, "consultorios", c.ConsultorioId, "consultorio.json");
            await _ctx.Writer.WriteJsonImmutableAsync(file, c, ct).ConfigureAwait(false);
        }
        await _ctx.Writer.WriteJsonImmutableAsync(_ctx.Paths.CampaignFile(id), campaign, ct).ConfigureAwait(false);
        _entries[id] = new Entry(campaign);
        return campaign;
    }

    public async Task<Campaign?> GetAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (!_entries.TryGetValue(campaignId, out var e)) return null;
        lock (e.Gate) return e.Campaign;
    }

    public async Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var list = new List<Campaign>();
        foreach (var e in _entries.Values) lock (e.Gate) list.Add(e.Campaign);
        return list.OrderBy(c => c.FechaInicio).ThenBy(c => c.CampaignId, StringComparer.Ordinal).ToList();
    }

    public async Task<Campaign?> GetActiveAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        foreach (var e in _entries.Values)
            lock (e.Gate) if (e.Campaign.IsOperating) return e.Campaign;
        return null;
    }

    public async Task<Campaign> TransitionAsync(string campaignId, string to, string by, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (!_entries.TryGetValue(campaignId, out var e)) throw DomainException.NotFound($"No existe la campaña '{campaignId}'.");

        // Solo una campaña puede estar en campo: la comprobación y el cambio van bajo el mismo candado.
        using var creation = to == CampaignStates.Concientizacion ? await _ctx.Locks.AcquireCampaignCreationAsync(ct).ConfigureAwait(false) : null;
        using var _ = await _ctx.Locks.AcquireCampaignAsync(campaignId, ct).ConfigureAwait(false);

        Campaign next;
        lock (e.Gate) next = CampaignRules.Advance(e.Campaign, to, by, _ctx.Clock.UtcNow);

        if (to == CampaignStates.Concientizacion)
        {
            foreach (var other in _entries.Values)
                lock (other.Gate)
                    if (other != e && other.Campaign.IsOperating)
                        throw new DomainException(ErrorCode.CampaignConflict,
                            $"Ya hay una campaña en campo ('{other.Campaign.CampaignId}', etapa '{other.Campaign.Estado}'). Concluya esa antes de iniciar otra.");
        }

        if (to != CampaignStates.Concluida)
        {
            await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.CampaignFile(campaignId), next, ct).ConfigureAwait(false);
            lock (e.Gate) e.Campaign = next;
            return next;
        }

        // Concluir: no se aceptan escrituras nuevas y se espera a las que están en curso.
        lock (e.Gate) e.Closing = true;
        try
        {
            while (true)
            {
                lock (e.Gate) if (e.Inflight == 0) break;
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.CampaignFile(campaignId), next, ct).ConfigureAwait(false);
            lock (e.Gate) e.Campaign = next;
            return next;
        }
        finally
        {
            lock (e.Gate) e.Closing = false;
        }
    }

    public async Task<ICampaignLease> AcquireWriteLeaseAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (!_entries.TryGetValue(campaignId, out var e)) throw DomainException.NotFound($"No existe la campaña '{campaignId}'.");
        lock (e.Gate)
        {
            if (e.Closing) throw new DomainException(ErrorCode.CampaignClosed, $"La campaña '{campaignId}' se está concluyendo.");
            if (!e.Campaign.AllowsClinicalWrites) throw CampaignRules.NotWritable(e.Campaign);
            e.Inflight++;
            return new Lease(e.Campaign, () => { lock (e.Gate) e.Inflight--; });
        }
    }

    private sealed class Lease : ICampaignLease
    {
        private Action? _release;
        public Campaign Campaign { get; }
        public Lease(Campaign c, Action release) { Campaign = c; _release = release; }
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Documentos que dan certeza legal a la campaña. Son inmutables y se guardan junto con su hash.</summary>
public sealed class FileSystemCampaignDocumentStore : ICampaignDocumentStore
{
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;

    public FileSystemCampaignDocumentStore(FsContext ctx, ICampaignStore campaigns) { _ctx = ctx; _campaigns = campaigns; }

    private string DocsDir(string cid) => _ctx.Safe.Resolve("campaigns", cid, "documentos");

    public async Task<CampaignDocument> AddAsync(string campaignId, DocumentRequest request, Stream content, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        if (string.IsNullOrWhiteSpace(request.Tipo) || request.Tipo.Length > 60) throw DomainException.Validation("El tipo de documento es obligatorio (hasta 60 caracteres).");
        if (request.Descripcion is { Length: > 1000 }) throw DomainException.Validation("La descripción admite hasta 1000 caracteres.");
        _ctx.EnsureSpace();
        var campaign = await _campaigns.GetAsync(campaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        if (campaign.IsConcluded) throw new DomainException(ErrorCode.CampaignClosed, "La campaña ya concluyó: no se agregan documentos.");

        var allowed = _ctx.Options.AllowedAttachmentTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dir = DocsDir(campaignId);
        var tmp = _ctx.Writer.NewTempPath(Path.Combine(dir, "upload"));
        string? finalPath = null;
        try
        {
            long total = 0;
            var head = new byte[16];
            var headLen = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    AttachmentRules.EnsureSize(total, _ctx.Options.MaxAttachmentBytes);
                    if (headLen < head.Length)
                    {
                        var n = Math.Min(head.Length - headLen, read);
                        Array.Copy(buffer, 0, head, headLen, n);
                        headLen += n;
                    }
                    sha.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(true);
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            var detected = AttachmentRules.CheckContent(head.AsSpan(0, headLen), total, request.ContentTypeDeclarado, request.NombreOriginal, allowed);

            var id = Ids.NewDocumentId();
            var name = $"{id}.{detected.Extension}";
            finalPath = _ctx.Safe.Resolve("campaigns", campaignId, "documentos", name);
            var doc = new CampaignDocument(id, request.Tipo.Trim(), request.Descripcion?.Trim(), AttachmentRules.SanitizeName(request.NombreOriginal),
                detected.ContentType, hash, total, request.SubidoPor, _ctx.Clock.UtcNow, $"documentos/{name}");
            _ctx.Writer.MoveImmutable(tmp, finalPath);
            await _ctx.Writer.WriteJsonImmutableAsync(_ctx.Safe.Resolve("campaigns", campaignId, "documentos", id + ".json"), doc, ct).ConfigureAwait(false);
            return doc;
        }
        catch
        {
            AtomicFileWriter.TryDelete(tmp);
            if (finalPath is not null && !File.Exists(_ctx.Safe.Resolve("campaigns", campaignId, "documentos", Path.GetFileNameWithoutExtension(finalPath) + ".json")))
                AtomicFileWriter.TryDelete(finalPath);
            throw;
        }
    }

    public async Task<IReadOnlyList<CampaignDocument>> ListAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        var dir = DocsDir(campaignId);
        var list = new List<CampaignDocument>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "doc-*.json"))
        {
            var d = await JsonFiles.ReadAsync<CampaignDocument>(f, ct).ConfigureAwait(false);
            if (d is not null) list.Add(d);
        }
        return list.OrderBy(d => d.SubidoEn).ThenBy(d => d.DocumentId, StringComparer.Ordinal).ToList();
    }

    public async Task<DocumentContent?> OpenAsync(string campaignId, string documentId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        Ids.RequireDocumentId(documentId);
        var meta = await JsonFiles.ReadAsync<CampaignDocument>(_ctx.Safe.Resolve("campaigns", campaignId, "documentos", documentId + ".json"), ct).ConfigureAwait(false);
        if (meta is null) return null;
        var path = _ctx.Safe.ResolveRelative(_ctx.Paths.CampaignDir(campaignId), meta.Ruta);
        if (!File.Exists(path)) return null;
        return new DocumentContent(meta, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }
}

public sealed class FileSystemAuditLog : IAuditLog
{
    private readonly FsContext _ctx;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public FileSystemAuditLog(FsContext ctx) => _ctx = ctx;

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        var dir = _ctx.Paths.AuditDir(entry.CampaignId);
        var path = _ctx.Safe.Resolve(Path.GetRelativePath(_ctx.Safe.Root, dir).Split(Path.DirectorySeparatorChar).Append($"{entry.Hora:yyyy-MM-dd}.log").ToArray());
        var line = System.Text.Json.JsonSerializer.Serialize(entry, CampanaJson.Compact) + "\n";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ctx.Writer.PrepareDirectory(path);
            await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lee la bitácora. Sin campaignId busca en la global y en la de todas las campañas.</summary>
    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery q, CancellationToken ct)
    {
        var dirs = new List<string>();
        if (q.CampaignId is not null) { Ids.RequireCampaignId(q.CampaignId); dirs.Add(_ctx.Paths.AuditDir(q.CampaignId)); }
        else
        {
            dirs.Add(_ctx.Paths.AuditDir(null));
            if (Directory.Exists(_ctx.Paths.CampaignsDir))
                foreach (var c in Directory.EnumerateDirectories(_ctx.Paths.CampaignsDir))
                    if (Ids.IsCampaignId(Path.GetFileName(c))) dirs.Add(_ctx.Paths.AuditDir(Path.GetFileName(c)));
        }

        var result = new List<AuditEntry>();
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                string[] lines;
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try { lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false); }
                finally { _gate.Release(); }
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    AuditEntry? e;
                    try { e = System.Text.Json.JsonSerializer.Deserialize<AuditEntry>(line, CampanaJson.Compact); }
                    catch (System.Text.Json.JsonException) { continue; }
                    if (e is null) continue;
                    if (q.Desde is not null && e.Hora < q.Desde) continue;
                    if (q.Hasta is not null && e.Hora > q.Hasta) continue;
                    if (q.Usuario is not null && e.Usuario != q.Usuario) continue;
                    if (q.Accion is not null && !e.Accion.StartsWith(q.Accion, StringComparison.Ordinal)) continue;
                    if (q.Recurso is not null && e.Recurso != q.Recurso) continue;
                    result.Add(e);
                }
            }
        }
        return result.OrderByDescending(e => e.Hora).Take(Math.Clamp(q.Limit, 1, 1000)).ToList();
    }
}
