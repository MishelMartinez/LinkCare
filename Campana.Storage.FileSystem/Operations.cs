using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campana.Core;

namespace Campana.Storage.FileSystem;

/// <summary>Lee lo que hizo cada paciente en una campaña y deja que Core lo cuente.</summary>
public sealed class FileSystemStatsService : IStatsService
{
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;
    private readonly FileSystemPatientStore _patients;

    public FileSystemStatsService(FsContext ctx, ICampaignStore campaigns, FileSystemPatientStore patients)
    { _ctx = ctx; _campaigns = campaigns; _patients = patients; }

    public async Task<CampaignStats> GetCampaignStatsAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        var campaign = await _campaigns.GetAsync(campaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);

        var activities = new List<PatientActivity>();
        var registradas = 0;
        foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.PatientsDir))
        {
            var pid = Path.GetFileName(dir);
            if (!Ids.IsPatientId(pid) || !File.Exists(_ctx.Paths.CampaignStateFile(pid, campaignId))) continue;
            registradas++;

            var mine = await _patients.States.ReadCampaignAsync(pid, campaignId, ct).ConfigureAwait(false);
            if (mine is null || mine.Eventos.Count == 0) continue;
            var docs = new List<EventDocument>();
            foreach (var s in mine.Eventos)
            {
                if (s.Tipo is EventTypes.Mama or EventTypes.Papanicolaou or EventTypes.Correccion)
                {
                    var path = _ctx.Paths.FromPatientRelative(pid, s.Ruta);
                    if (!File.Exists(path)) continue;
                    var doc = CampanaJson.Deserialize<EventDocument>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
                    if (doc is not null) docs.Add(doc);
                }
                else docs.Add(new EventDocument(1, s.EventId, "", s.Tipo, 1, pid, s.CampaignId, s.ConsultorioId, s.CreatedBy, s.CreatedAt, default, s.VisitId));
            }

            // Recurrente: la paciente (o un expediente vinculado) ya tenía atenciones en otra campaña antes de esta.
            var first = mine.Eventos.Min(e => e.CreatedAt);
            var canonical = _patients.Links.Resolve(pid);
            var recurrente = false;
            foreach (var id in _patients.Links.Group(canonical))
            {
                var st = await _patients.States.ReadPatientAsync(id, ct).ConfigureAwait(false);
                if (st is not null && st.Eventos.Any(e => e.CampaignId != campaignId && e.CreatedAt < first)) { recurrente = true; break; }
            }
            activities.Add(new PatientActivity(pid, docs, recurrente));
        }
        return StatsCalculator.Compute(campaign, registradas, activities, _ctx.Clock.UtcNow);
    }

    public async Task<IReadOnlyList<UserActivity>> GetActivityByUserAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        _ = await _campaigns.GetAsync(campaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        var events = new List<EventSummary>();
        foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.PatientsDir))
        {
            var pid = Path.GetFileName(dir);
            if (!Ids.IsPatientId(pid)) continue;
            var st = await _patients.States.ReadCampaignAsync(pid, campaignId, ct).ConfigureAwait(false);
            if (st is not null) events.AddRange(st.Eventos);
        }
        return StatsCalculator.ByUser(events);
    }
}

/// <summary>
/// Arma el minicubo: un paquete con el historial concentrado de una unidad geográfica (texto e imágenes)
/// para llevarlo a la siguiente campaña y reconocer ahí a las pacientes recurrentes sin internet.
/// </summary>
public sealed class FileSystemMinicubeBuilder : IMinicubeBuilder
{
    private static readonly Regex IdRx = new("^mc-[0-9]{14}-[0-9a-f]{8}$", RegexOptions.Compiled);
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;
    private readonly FileSystemPatientStore _patients;

    public FileSystemMinicubeBuilder(FsContext ctx, ICampaignStore campaigns, FileSystemPatientStore patients)
    { _ctx = ctx; _campaigns = campaigns; _patients = patients; }

    public async Task<MinicubeResult> BuildAsync(MinicubeRequest request, string createdBy, CancellationToken ct)
    {
        await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var all = await _campaigns.ListAsync(ct).ConfigureAwait(false);
        List<Campaign> selected;
        string? clave = null;
        if (request.CampaignIds is { Count: > 0 })
        {
            selected = new List<Campaign>();
            foreach (var wanted in request.CampaignIds.Distinct())
                selected.Add(all.FirstOrDefault(c => c.CampaignId == Ids.RequireCampaignId(wanted)) ?? throw DomainException.NotFound($"No existe la campaña '{wanted}'."));
        }
        else
        {
            clave = Normalizer.Slug(request.ClaveGeografica);
            if (clave.Length == 0) throw DomainException.Validation("Indique claveGeografica o campaignIds.");
            selected = all.Where(c => c.UnidadGeografica.Clave == clave).ToList();
        }
        if (request.SoloConcluidas)
        {
            var open = selected.Where(c => !c.IsConcluded).Select(c => c.CampaignId).ToList();
            if (open.Count > 0 && request.CampaignIds is { Count: > 0 })
                throw new DomainException(ErrorCode.CampaignState, $"Estas campañas aún no concluyen: {string.Join(", ", open)}.");
            selected = selected.Where(c => c.IsConcluded).ToList();
        }
        if (selected.Count == 0) throw DomainException.NotFound("No hay campañas que cumplan el criterio para armar el minicubo.");
        _ctx.EnsureSpace();

        var now = _ctx.Clock.UtcNow;
        var id = $"mc-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var dest = _ctx.Safe.Resolve("_exports", id);
        var cids = selected.Select(c => c.CampaignId).ToHashSet();

        var files = new List<ManifestFile>();
        var summaryPatients = new List<MinicubePatient>();
        long bytes = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.PatientsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var pid = Path.GetFileName(dir);
                if (!Ids.IsPatientId(pid)) continue;
                var mine = cids.Where(c => File.Exists(_ctx.Paths.CampaignStateFile(pid, c))).ToList();
                if (mine.Count == 0) continue;

                async Task CopyAsync(string full)
                {
                    var rel = "patients/" + pid + "/" + _ctx.Paths.ToPatientRelative(pid, full);
                    var target = _ctx.Safe.ResolveRelative(dest, rel);
                    _ctx.Writer.PrepareDirectory(target);
                    File.Copy(full, target, overwrite: false);
                    files.Add(new ManifestFile(rel, await JsonFiles.Sha256Async(target, ct).ConfigureAwait(false)));
                    bytes += new FileInfo(target).Length;
                }

                await CopyAsync(_ctx.Paths.IdentityFile(pid)).ConfigureAwait(false);
                var attachments = new List<MinicubeFile>();
                var events = new List<(EventSummary S, EventDocument? D)>();
                foreach (var cid in mine)
                {
                    var cdir = _ctx.Paths.PatientCampaignDir(pid, cid);
                    foreach (var f in Directory.EnumerateFiles(cdir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var rel = Path.GetRelativePath(cdir, f).Replace(Path.DirectorySeparatorChar, '/');
                        if (rel == "state.json" || Path.GetFileName(f).StartsWith(".tmp-", StringComparison.Ordinal)) continue;
                        if (rel.StartsWith("media/", StringComparison.Ordinal) && !request.IncluirImagenes) continue;
                        await CopyAsync(f).ConfigureAwait(false);
                        if (rel.StartsWith("media/", StringComparison.Ordinal))
                            attachments.Add(new MinicubeFile($"patients/{pid}/campaigns/{cid}/{rel}",
                                FileSignatures.ContentTypeFromExtension(Path.GetExtension(f).TrimStart('.')) ?? "application/octet-stream",
                                files[^1].Sha256, new FileInfo(f).Length));
                    }
                    var st = await _patients.States.ReadCampaignAsync(pid, cid, ct).ConfigureAwait(false);
                    foreach (var s in st?.Eventos ?? new List<EventSummary>())
                    {
                        EventDocument? doc = null;
                        if (s.Tipo is EventTypes.Mama or EventTypes.Papanicolaou)
                        {
                            var path = _ctx.Paths.FromPatientRelative(pid, s.Ruta);
                            if (File.Exists(path)) doc = CampanaJson.Deserialize<EventDocument>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
                        }
                        events.Add((s, doc));
                    }
                }

                var identity = await _patients.LoadMergedIdentityAsync(pid, ct).ConfigureAwait(false);
                if (identity is null) continue;
                var visits = VisitsBuilder.Build(pid, events.Select(e => e.S));
                var docsById = events.Where(e => e.D is not null).ToDictionary(e => e.S.EventId, e => e.D!);
                var effective = StatsCalculator.EffectivePayloads(docsById.Values);
                MinicubeService Svc(EventSummary s)
                {
                    int? birads = null, conducta = null;
                    bool? sosp = null;
                    if (effective.TryGetValue(s.EventId, out var node))
                    {
                        if (node?["diagnostico"]?["birads"] is System.Text.Json.Nodes.JsonValue b && b.TryGetValue<int>(out var bi)) birads = bi;
                        if (node?["referencia"]?["conducta"] is System.Text.Json.Nodes.JsonValue c && c.TryGetValue<int>(out var ci)) conducta = ci;
                        if (node?["sospechosa"] is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue<bool>(out var sb)) sosp = sb;
                    }
                    return new MinicubeService(s.EventId, s.Tipo, s.CreatedAt, s.ConsultorioId, birads, conducta, sosp);
                }
                var mv = visits.Visitas.Select(v => new MinicubeVisit(v.CampaignId, v.VisitId, v.Fecha, v.Servicios.Where(s => s.Tipo is not EventTypes.Adjunto).Select(Svc).ToList())).ToList();
                foreach (var g in visits.ServiciosSinVisita.Where(s => s.Tipo is not EventTypes.Adjunto).GroupBy(s => s.CampaignId))
                    mv.Add(new MinicubeVisit(g.Key, null, g.Min(s => s.CreatedAt), g.Select(Svc).ToList()));
                summaryPatients.Add(new MinicubePatient(pid, $"{identity.Nombres} {identity.Apellidos}".Trim(), identity.FechaNacimiento,
                    string.IsNullOrWhiteSpace(identity.Documento?.Numero) ? null : identity.Documento.Numero,
                    mv.OrderBy(v => v.Fecha).ToList(), attachments));
            }

            var summary = new MinicubeSummary(id, now, createdBy, clave, selected.Select(c => new MinicubeCampaign(c.CampaignId, c.Lugar, c.FechaInicio, c.FechaFin, c.Estado)).ToList(), summaryPatients);
            await _ctx.Writer.WriteJsonImmutableAsync(Path.Combine(dest, "minicubo.json"), summary, ct).ConfigureAwait(false);
            await _ctx.Writer.WriteJsonImmutableAsync(Path.Combine(dest, "manifest.json"), new ImportManifest(null, files, cids.OrderBy(c => c, StringComparer.Ordinal).ToList()), ct).ConfigureAwait(false);
            var result = new MinicubeResult(id, dest, clave ?? selected[0].UnidadGeografica.Clave, cids.OrderBy(c => c, StringComparer.Ordinal).ToList(), summaryPatients.Count, files.Count, bytes, now, createdBy);
            await _ctx.Writer.WriteJsonImmutableAsync(Path.Combine(dest, "resultado.json"), result, ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { /* mejor esfuerzo */ }
            throw;
        }
    }

    public async Task<IReadOnlyList<MinicubeResult>> ListAsync(CancellationToken ct)
    {
        var list = new List<MinicubeResult>();
        if (!Directory.Exists(_ctx.Paths.ExportsDir)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.ExportsDir))
        {
            var r = await JsonFiles.ReadAsync<MinicubeResult>(Path.Combine(dir, "resultado.json"), ct).ConfigureAwait(false);
            if (r is not null) list.Add(r);
        }
        return list.OrderByDescending(r => r.CreadoEn).ToList();
    }

    public async Task<bool> WriteZipAsync(string minicubeId, Stream destination, CancellationToken ct)
    {
        if (!IdRx.IsMatch(minicubeId)) throw DomainException.Validation("minicubeId inválido.");
        var dir = _ctx.Safe.Resolve("_exports", minicubeId);
        if (!Directory.Exists(dir)) return false;
        // ZipArchive escribe de forma síncrona al cerrarse, y una respuesta HTTP no lo permite: se arma en un archivo temporal y luego se transmite.
        Directory.CreateDirectory(_ctx.Paths.StagingDir);
        var tmpPath = Path.Combine(_ctx.Paths.StagingDir, ".tmp-zip-" + Guid.NewGuid().ToString("N"));
        await using var tmp = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using (var zip = new ZipArchive(tmp, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Fastest);
                using var es = entry.Open();
                using var fs = File.OpenRead(f);
                await fs.CopyToAsync(es, ct).ConfigureAwait(false);
            }
        }
        tmp.Position = 0;
        await tmp.CopyToAsync(destination, ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// Copia la campaña al fileshare general. Nunca sobrescribe un archivo inmutable que difiera: lo reporta como conflicto.
/// Los hashes se verifican después de copiar. Se puede repetir sin riesgo.
/// </summary>
public sealed class FileSystemFileShareSync : IFileShareSync
{
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;
    private readonly FileSystemPatientStore _patients;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemFileShareSync(FsContext ctx, ICampaignStore campaigns, FileSystemPatientStore patients)
    { _ctx = ctx; _campaigns = campaigns; _patients = patients; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_ctx.Options.FileShareRoot);

    public Task<SyncReport?> LastReportAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        return JsonFiles.ReadAsync<SyncReport>(_ctx.Paths.SyncReportFile(campaignId), ct);
    }

    private enum Kind { Immutable, AppendOnly, CampaignFile }

    public async Task<SyncReport> SyncCampaignAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        if (!IsConfigured) throw DomainException.Conflict("No hay un fileshare configurado (Campana:FileShare:Root).");
        var campaign = await _campaigns.GetAsync(campaignId, ct).ConfigureAwait(false) ?? throw DomainException.NotFound("No existe la campaña.");
        var rootPath = Path.GetFullPath(_ctx.Options.FileShareRoot!);
        // Si el volumen no está montado no se crea la carpeta: se escribiría en el disco local por error.
        if (!Directory.Exists(rootPath)) throw new DomainException(ErrorCode.Conflict, $"El fileshare no está disponible: '{rootPath}'.");
        var remote = new SafePath(rootPath);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
            var uploaded = 0; var skipped = 0; long bytes = 0;
            var conflicts = new List<SyncConflict>();
            var errors = new List<string>();
            var manifest = new List<ManifestFile>();

            foreach (var (rel, kind) in EnumerateFiles(campaignId))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var local = _ctx.Safe.ResolveRelative(_ctx.Safe.Root, rel);
                    var dst = remote.ResolveRelative(remote.Root, rel);
                    var localHash = await JsonFiles.Sha256Async(local, ct).ConfigureAwait(false);

                    if (File.Exists(dst))
                    {
                        var remoteHash = await JsonFiles.Sha256Async(dst, ct).ConfigureAwait(false);
                        if (remoteHash == localHash) { skipped++; manifest.Add(new ManifestFile(rel, localHash)); continue; }
                        if (!await CanReplaceAsync(kind, local, dst, ct).ConfigureAwait(false))
                        {
                            conflicts.Add(new SyncConflict(rel, kind == Kind.Immutable ? "El archivo ya existe en el fileshare con contenido distinto." : "El fileshare tiene una versión que no es anterior a la local.", localHash, remoteHash));
                            continue;
                        }
                        await CopyVerifiedAsync(local, dst, localHash, overwrite: true, ct).ConfigureAwait(false);
                    }
                    else await CopyVerifiedAsync(local, dst, localHash, overwrite: false, ct).ConfigureAwait(false);

                    uploaded++;
                    bytes += new FileInfo(local).Length;
                    manifest.Add(new ManifestFile(rel, localHash));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DomainException)
                {
                    errors.Add($"{rel}: {ex.Message}");
                }
            }

            var report = new SyncReport(campaignId, _ctx.Clock.UtcNow, uploaded, skipped, bytes, conflicts, errors);
            await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.SyncManifestFile(campaignId), new { campaignId, hora = report.Hora, archivos = manifest }, ct).ConfigureAwait(false);
            await _ctx.Writer.WriteJsonReplaceAsync(_ctx.Paths.SyncReportFile(campaignId), report, ct).ConfigureAwait(false);
            _ = campaign;
            return report;
        }
        finally { _gate.Release(); }
    }

    private IEnumerable<(string Rel, Kind Kind)> EnumerateFiles(string cid)
    {
        var root = _ctx.Safe.Root;
        string Rel(string full) => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
        static bool Skip(string name) => name.StartsWith(".tmp-", StringComparison.Ordinal);

        yield return (Rel(_ctx.Paths.CampaignFile(cid)), Kind.CampaignFile);
        foreach (var sub in new[] { "consultorios", "documentos" })
        {
            var d = _ctx.Safe.Resolve("campaigns", cid, sub);
            if (Directory.Exists(d))
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                    if (!Skip(Path.GetFileName(f))) yield return (Rel(f), Kind.Immutable);
        }
        var audit = _ctx.Paths.AuditDir(cid);
        if (Directory.Exists(audit))
            foreach (var f in Directory.EnumerateFiles(audit, "*.log").OrderBy(x => x, StringComparer.Ordinal)) yield return (Rel(f), Kind.AppendOnly);

        foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.PatientsDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            var pid = Path.GetFileName(dir);
            if (!Ids.IsPatientId(pid) || !File.Exists(_ctx.Paths.CampaignStateFile(pid, cid))) continue;
            yield return (Rel(_ctx.Paths.IdentityFile(pid)), Kind.Immutable);
            var cdir = _ctx.Paths.PatientCampaignDir(pid, cid);
            foreach (var f in Directory.EnumerateFiles(cdir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                var inner = Path.GetRelativePath(cdir, f).Replace(Path.DirectorySeparatorChar, '/');
                if (inner == "state.json" || Skip(Path.GetFileName(f))) continue; // state.json es derivado: el otro lado lo reconstruye
                yield return (Rel(f), Kind.Immutable);
            }
        }
    }

    private static async Task<bool> CanReplaceAsync(Kind kind, string local, string remote, CancellationToken ct)
    {
        switch (kind)
        {
            case Kind.AppendOnly:
            {
                // Una bitácora local solo reemplaza a la remota si la contiene completa al inicio.
                var l = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
                var r = await File.ReadAllBytesAsync(remote, ct).ConfigureAwait(false);
                return l.Length > r.Length && l.AsSpan(0, r.Length).SequenceEqual(r);
            }
            case Kind.CampaignFile:
            {
                // campaign.json solo cambia al avanzar de etapa: la local debe extender el historial de etapas de la remota.
                var l = await JsonFiles.ReadAsync<Campaign>(local, ct).ConfigureAwait(false);
                var r = await JsonFiles.ReadAsync<Campaign>(remote, ct).ConfigureAwait(false);
                if (l is null || r is null) return false;
                return l.Transiciones.Count > r.Transiciones.Count
                    && r.Transiciones.Select((t, i) => t.Estado == l.Transiciones[i].Estado && t.En == l.Transiciones[i].En).All(x => x);
            }
            default:
                return false;
        }
    }

    private async Task CopyVerifiedAsync(string local, string destination, string expectedHash, bool overwrite, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(local, tmp, overwrite: false);
            if (await JsonFiles.Sha256Async(tmp, ct).ConfigureAwait(false) != expectedHash)
                throw new IOException("El archivo se dañó al copiarse al fileshare.");
            File.Move(tmp, destination, overwrite);
        }
        catch
        {
            AtomicFileWriter.TryDelete(tmp);
            throw;
        }
    }
}
