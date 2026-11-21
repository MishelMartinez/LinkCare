using System.Text.RegularExpressions;
using Campana.Core;

namespace Campana.Storage.FileSystem;

public sealed record ManifestFile(string Ruta, string Sha256);
public sealed record ImportManifest(string? CampanaOrigen, List<ManifestFile> Archivos, List<string>? CampanasOrigen = null);
internal sealed record ImportJournal(string ImportId, string CampanaOrigen, DateTimeOffset Hora, List<ManifestFile> Archivos, List<string> Advertencias);

public sealed class FileSystemHistoryImporter : IHistoryImporter
{
    private static readonly Regex HashRx = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private readonly FsContext _ctx;
    private readonly FileSystemPatientStore _patients;
    private readonly FileSystemRebuildService _rebuild;

    public FileSystemHistoryImporter(FsContext ctx, FileSystemPatientStore patients, FileSystemRebuildService rebuild)
    {
        _ctx = ctx;
        _patients = patients;
        _rebuild = rebuild;
    }

    public async Task<ImportResult> ImportAsync(string packagePath, string activeCampaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(activeCampaignId);
        var pkg = Path.GetFullPath(packagePath);
        var manifestPath = Path.Combine(pkg, "manifest.json");
        if (!File.Exists(manifestPath)) throw DomainException.Validation("El paquete no tiene manifest.json.");
        var manifest = await JsonFiles.ReadAsync<ImportManifest>(manifestPath, ct).ConfigureAwait(false)
                       ?? throw DomainException.Validation("manifest.json inválido.");

        var origins = new List<string>();
        if (manifest.CampanaOrigen is not null) origins.Add(manifest.CampanaOrigen);
        if (manifest.CampanasOrigen is not null) origins.AddRange(manifest.CampanasOrigen);
        origins = origins.Distinct().ToList();
        if (origins.Count == 0) throw DomainException.Validation("El manifiesto debe indicar campanaOrigen o campanasOrigen.");
        foreach (var o in origins)
        {
            Ids.RequireCampaignId(o);
            if (o == activeCampaignId) throw DomainException.Validation("No se puede importar historial sobre la campaña activa.");
            if (File.Exists(_ctx.Paths.CampaignFile(o))) throw DomainException.Conflict($"La campaña '{o}' existe localmente y no puede importarse.");
        }
        if (manifest.Archivos is null || manifest.Archivos.Count == 0) throw DomainException.Validation("El paquete no contiene archivos.");

        var warnings = new List<string>();
        var files = new List<ManifestFile>();
        foreach (var f in manifest.Archivos)
        {
            var parts = (f.Ruta ?? "").Split('/');
            foreach (var p in parts) SafePath.ValidateSegment(p);
            var ok = parts.Length >= 3 && parts[0] == "patients" && Ids.IsPatientId(parts[1]) && (
                (parts.Length == 3 && parts[2] == "identity.json") ||
                (parts.Length >= 5 && parts[2] == "campaigns" && origins.Contains(parts[3])));
            if (!ok) throw DomainException.Validation($"Ruta no permitida en el paquete: {f.Ruta}");
            if (!HashRx.IsMatch(f.Sha256 ?? "")) throw DomainException.Validation($"Hash inválido para {f.Ruta}.");
            if (parts[^1] == "state.json") { warnings.Add($"Se omitió {f.Ruta} (archivo derivado)."); continue; }
            files.Add(f);
        }

        var importId = Guid.NewGuid().ToString("N");
        var staging = _ctx.Safe.Resolve("_staging", importId);
        var imported = new List<ManifestFile>();
        var skipped = 0;
        var patients = new HashSet<string>();
        try
        {
            // 1. Copiar a staging y verificar hash
            foreach (var f in files)
            {
                var src = ResolveInPackage(pkg, f.Ruta);
                if (!File.Exists(src)) throw DomainException.Validation($"Falta el archivo del paquete: {f.Ruta}");
                var dst = _ctx.Safe.ResolveRelative(staging, f.Ruta);
                _ctx.Writer.PrepareDirectory(dst);
                File.Copy(src, dst, overwrite: false);
                var actual = await JsonFiles.Sha256Async(dst, ct).ConfigureAwait(false);
                if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new DomainException(ErrorCode.IntegrityError, $"El hash no coincide para {f.Ruta}. Importación cancelada.");
            }

            // 2. Revisar conflictos antes de mover nada
            var toMove = new List<ManifestFile>();
            foreach (var f in files)
            {
                var dest = _ctx.Safe.ResolveRelative(_ctx.Safe.Root, f.Ruta);
                if (!File.Exists(dest)) { toMove.Add(f); continue; }
                var existing = await JsonFiles.Sha256Async(dest, ct).ConfigureAwait(false);
                if (string.Equals(existing, f.Sha256, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (f.Ruta.EndsWith("/identity.json", StringComparison.Ordinal))
                {
                    warnings.Add($"Se conservó la identidad local de {f.Ruta.Split('/')[1]} (difiere de la del paquete).");
                    skipped++;
                    continue;
                }
                throw DomainException.Conflict($"El archivo ya existe con contenido distinto: {f.Ruta}. Importación cancelada.");
            }

            // 3. Mover, registrar y reconstruir
            await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
            using (await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false))
            {
                foreach (var f in toMove)
                {
                    var from = _ctx.Safe.ResolveRelative(staging, f.Ruta);
                    var dest = _ctx.Safe.ResolveRelative(_ctx.Safe.Root, f.Ruta);
                    _ctx.Writer.MoveImmutable(from, dest);
                    imported.Add(f);
                    patients.Add(f.Ruta.Split('/')[1]);
                }
                foreach (var origin in origins)
                {
                    // La identidad se anota en cada diario: al purgar la última campaña de origen se puede retirar a la paciente.
                    var mine = imported.Where(f => f.Ruta.EndsWith("/identity.json", StringComparison.Ordinal) || f.Ruta.Split('/')[3] == origin).ToList();
                    var journal = new ImportJournal(importId, origin, _ctx.Clock.UtcNow, mine, warnings);
                    await _ctx.Writer.WriteJsonImmutableAsync(_ctx.Safe.Resolve("_global", "imports", $"{importId}-{origin}.json"), journal, ct).ConfigureAwait(false);
                }

                foreach (var pid in patients)
                {
                    using var _ = await _ctx.Locks.AcquirePatientAsync(pid, ct).ConfigureAwait(false);
                    await _rebuild.Rebuilder.RebuildAsync(pid, ct).ConfigureAwait(false);
                }
                await _patients.Index.RebuildCoreAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* mejor esfuerzo */ }
        }

        return new ImportResult(importId, string.Join(",", origins), patients.Count, imported.Count, skipped, warnings, origins);
    }

    private static string ResolveInPackage(string pkg, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(new[] { pkg }.Concat(relative.Split('/')).ToArray()));
        if (!full.StartsWith(pkg + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw DomainException.Validation("Ruta fuera del paquete.");
        return full;
    }

    public async Task<int> PurgeImportedAsync(string originCampaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(originCampaignId);
        if (File.Exists(_ctx.Paths.CampaignFile(originCampaignId)))
            throw DomainException.Conflict("Esa campaña se capturó en este servidor. Solo se puede borrar historial importado.");
        if (!Directory.Exists(_ctx.Paths.ImportsDir)) return 0;

        var journals = new List<(string Path, ImportJournal Journal)>();
        foreach (var f in Directory.EnumerateFiles(_ctx.Paths.ImportsDir, "*.json"))
        {
            var j = await JsonFiles.ReadAsync<ImportJournal>(f, ct).ConfigureAwait(false);
            if (j is not null && j.CampanaOrigen == originCampaignId) journals.Add((f, j));
        }
        if (journals.Count == 0) return 0;

        await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var deleted = 0;
        using (await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false))
        {
            var affected = new HashSet<string>();
            var identityImported = new HashSet<string>();
            foreach (var (_, j) in journals)
                foreach (var f in j.Archivos)
                {
                    var pid = f.Ruta.Split('/')[1];
                    affected.Add(pid);
                    if (f.Ruta.EndsWith("/identity.json", StringComparison.Ordinal)) { identityImported.Add(pid); continue; }
                    var path = _ctx.Safe.ResolveRelative(_ctx.Safe.Root, f.Ruta);
                    using var _ = await _ctx.Locks.AcquirePatientAsync(pid, ct).ConfigureAwait(false);
                    if (File.Exists(path)) { File.Delete(path); deleted++; }
                }

            foreach (var pid in affected)
            {
                using var _ = await _ctx.Locks.AcquirePatientAsync(pid, ct).ConfigureAwait(false);
                var cdir = _ctx.Paths.PatientCampaignDir(pid, originCampaignId);
                if (Directory.Exists(cdir))
                {
                    var st = Path.Combine(cdir, "state.json");
                    if (File.Exists(st)) File.Delete(st);
                    RemoveEmptyDirs(cdir);
                }
                var campaignsDir = _ctx.Paths.PatientCampaignsDir(pid);
                var remaining = Directory.Exists(campaignsDir) && Directory.EnumerateDirectories(campaignsDir).Any();
                if (!remaining && identityImported.Contains(pid))
                {
                    var pdir = _ctx.Paths.PatientDir(pid);
                    var others = Directory.EnumerateFileSystemEntries(pdir).Select(Path.GetFileName)
                        .Where(n => n is not ("identity.json" or "state.json" or "campaigns")).Any();
                    if (!others)
                    {
                        Directory.Delete(pdir, recursive: true);
                        deleted++;
                        continue;
                    }
                }
                if (Directory.Exists(_ctx.Paths.PatientDir(pid)))
                    await _rebuild.Rebuilder.RebuildAsync(pid, ct).ConfigureAwait(false);
            }
            foreach (var (path, _) in journals) File.Delete(path);
            await _patients.Index.RebuildCoreAsync(ct).ConfigureAwait(false);
        }
        return deleted;
    }

    private static void RemoveEmptyDirs(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir)) RemoveEmptyDirs(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
