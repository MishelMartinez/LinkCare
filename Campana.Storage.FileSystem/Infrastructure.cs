using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Campana.Core;

namespace Campana.Storage.FileSystem;

public sealed class FileSystemOptions
{
    public string DataRoot { get; set; } = "";
    public long MinFreeDiskBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;
    public string[] AllowedAttachmentTypes { get; set; } = { "application/pdf", "image/jpeg", "image/png" };
    /// <summary>Carpeta del fileshare general (por ejemplo, el volumen montado). Vacía: la sincronización no está disponible.</summary>
    public string? FileShareRoot { get; set; }
}

/// <summary>Punto de inyección de fallos para pruebas. En producción no hace nada.</summary>
public interface IFaultInjector { void Hit(string point); }

public sealed class NoFaults : IFaultInjector { public void Hit(string point) { } }

/// <summary>Simula una caída del proceso. No se captura en los flujos de reparación.</summary>
public sealed class InjectedFaultException : Exception
{
    public InjectedFaultException(string point) : base($"Fallo inyectado en '{point}'.") { }
}

public sealed class DriveInfoProbe : IDiskSpaceProbe
{
    private readonly string _root;
    public DriveInfoProbe(string root) => _root = root;
    public long FreeBytes()
    {
        try { return new DriveInfo(_root).AvailableFreeSpace; }
        catch { return long.MaxValue; }
    }
}

/// <summary>
/// Orden de adquisición: creación de campaña / índice → campaña → solicitud → paciente → hoja (seen).
/// </summary>
public sealed class LockManager
{
    public SemaphoreSlim CampaignCreation { get; } = new(1, 1);
    public SemaphoreSlim Index { get; } = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _patients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _campaigns = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _seen = new();
    private readonly SemaphoreSlim[] _requestStripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public Task<IDisposable> AcquireIndexAsync(CancellationToken ct) => Acquire(Index, ct);
    public Task<IDisposable> AcquireCampaignCreationAsync(CancellationToken ct) => Acquire(CampaignCreation, ct);
    public Task<IDisposable> AcquirePatientAsync(string patientId, CancellationToken ct) => Acquire(_patients.GetOrAdd(patientId, _ => new SemaphoreSlim(1, 1)), ct);
    public Task<IDisposable> AcquireCampaignAsync(string campaignId, CancellationToken ct) => Acquire(_campaigns.GetOrAdd(campaignId, _ => new SemaphoreSlim(1, 1)), ct);
    public Task<IDisposable> AcquireSeenAsync(string campaignId, CancellationToken ct) => Acquire(_seen.GetOrAdd(campaignId, _ => new SemaphoreSlim(1, 1)), ct);

    public Task<IDisposable> AcquireRequestAsync(string campaignId, string requestId, CancellationToken ct)
    {
        var h = (uint)StringComparer.Ordinal.GetHashCode(campaignId + "/" + requestId);
        return Acquire(_requestStripes[h % (uint)_requestStripes.Length], ct);
    }

    private static async Task<IDisposable> Acquire(SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
    }
}

public sealed class AtomicFileWriter
{
    private readonly SafePath _safe;
    private readonly IFaultInjector _faults;

    public AtomicFileWriter(SafePath safe, IFaultInjector? faults = null)
    {
        _safe = safe;
        _faults = faults ?? new NoFaults();
    }

    /// <summary>Escribe un archivo que no debe existir. Lanza Conflict si ya existe.</summary>
    public async Task WriteImmutableAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!await TryWriteImmutableAsync(path, data, ct).ConfigureAwait(false))
            throw DomainException.Conflict($"El archivo ya existe y es inmutable: {Path.GetFileName(path)}");
    }

    /// <summary>Devuelve false si el destino ya existe (no lo modifica).</summary>
    public async Task<bool> TryWriteImmutableAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        _safe.EnsureInside(path);
        if (File.Exists(path)) return false;
        var tmp = await WriteTempAsync(path, data, ct).ConfigureAwait(false);
        try
        {
            _faults.Hit("before-rename");
            if (File.Exists(path)) { File.Delete(tmp); return false; }
            File.Move(tmp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            TryDelete(tmp);
            return false;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Reemplaza (o crea) un archivo derivado o mutable de forma atómica.</summary>
    public async Task WriteReplaceAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        _safe.EnsureInside(path);
        var tmp = await WriteTempAsync(path, data, ct).ConfigureAwait(false);
        try
        {
            _faults.Hit("before-rename");
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Mueve un archivo ya escrito (mismo volumen) a un destino que no debe existir.</summary>
    public void MoveImmutable(string source, string destination)
    {
        _safe.EnsureInside(destination);
        PrepareDirectory(destination);
        File.Move(source, destination, overwrite: false);
    }

    public async Task WriteJsonImmutableAsync<T>(string path, T value, CancellationToken ct)
        => await WriteImmutableAsync(path, CampanaJson.SerializeToUtf8(value), ct).ConfigureAwait(false);

    public async Task<bool> TryWriteJsonImmutableAsync<T>(string path, T value, CancellationToken ct)
        => await TryWriteImmutableAsync(path, CampanaJson.SerializeToUtf8(value), ct).ConfigureAwait(false);

    public async Task WriteJsonReplaceAsync<T>(string path, T value, CancellationToken ct)
        => await WriteReplaceAsync(path, CampanaJson.SerializeToUtf8(value), ct).ConfigureAwait(false);

    public string NewTempPath(string finalPath)
    {
        PrepareDirectory(finalPath);
        return Path.Combine(Path.GetDirectoryName(finalPath)!, ".tmp-" + Guid.NewGuid().ToString("N"));
    }

    private async Task<string> WriteTempAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        PrepareDirectory(path);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await fs.WriteAsync(data, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        return tmp;
    }

    /// <summary>Crea el directorio padre y verifica que ningún componente existente sea un enlace simbólico.</summary>
    public void PrepareDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        _safe.EnsureInside(dir);
        EnsureNoSymlinks(dir);
        Directory.CreateDirectory(dir);
    }

    private void EnsureNoSymlinks(string dir)
    {
        var current = Path.GetFullPath(dir);
        var stack = new Stack<string>();
        while (current.Length > _safe.Root.Length && current.StartsWith(_safe.Root, StringComparison.Ordinal))
        {
            stack.Push(current);
            current = Path.GetDirectoryName(current)!;
        }
        foreach (var p in stack)
        {
            var info = new DirectoryInfo(p);
            if (info.Exists && info.LinkTarget is not null)
                throw DomainException.Validation("La ruta contiene un enlace simbólico no permitido.");
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* mejor esfuerzo */ }
    }
}

public static class JsonFiles
{
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return CampanaJson.Deserialize<T>(bytes);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class DataPaths
{
    private readonly SafePath _s;
    public DataPaths(SafePath s) => _s = s;
    public string Root => _s.Root;

    public string GlobalDir => _s.Resolve("_global");
    public string PatientsIndexFile => _s.Resolve("_global", "patients-index.json");
    public string PatientLinksFile => _s.Resolve("_global", "patient-links.json");
    public string ImportsDir => _s.Resolve("_global", "imports");
    public string StagingDir => _s.Resolve("_staging");
    public string SchemasDir => _s.Resolve("schemas");
    public string ConfigDir => _s.Resolve("_config");
    public string ExportsDir => _s.Resolve("_exports");
    public string SyncReportFile(string cid) => _s.Resolve("campaigns", cid, "_global", "sync-report.json");
    public string SyncManifestFile(string cid) => _s.Resolve("campaigns", cid, "_global", "sync-manifest.json");
    public string PatientsDir => _s.Resolve("patients");
    public string CampaignsDir => _s.Resolve("campaigns");

    public string PatientDir(string pid) => _s.Resolve("patients", pid);
    public string IdentityFile(string pid) => _s.Resolve("patients", pid, "identity.json");
    public string PatientStateFile(string pid) => _s.Resolve("patients", pid, "state.json");
    public string PatientCampaignsDir(string pid) => _s.Resolve("patients", pid, "campaigns");
    public string PatientCampaignDir(string pid, string cid) => _s.Resolve("patients", pid, "campaigns", cid);
    public string CampaignStateFile(string pid, string cid) => _s.Resolve("patients", pid, "campaigns", cid, "state.json");
    public string MediaDir(string pid, string cid) => _s.Resolve("patients", pid, "campaigns", cid, "media");

    public string CampaignDir(string cid) => _s.Resolve("campaigns", cid);
    public string CampaignFile(string cid) => _s.Resolve("campaigns", cid, "campaign.json");
    public string CampaignGlobalDir(string cid) => _s.Resolve("campaigns", cid, "_global");
    public string SeenFile(string cid) => _s.Resolve("campaigns", cid, "_global", "patients-seen.json");
    public string HealthFile(string cid) => _s.Resolve("campaigns", cid, "_global", "health.json");
    public string RootHealthFile => _s.Resolve("_global", "health.json");
    public string RequestFile(string cid, string requestId) => _s.Resolve("campaigns", cid, "_global", "requests", requestId + ".json");
    public string AuditDir(string? cid) => cid is null ? _s.Resolve("_global", "audit") : _s.Resolve("campaigns", cid, "_global", "audit");

    public string ToPatientRelative(string pid, string fullPath) => _s.ToRelativeWithSlashes(PatientDir(pid), fullPath);
    public string FromPatientRelative(string pid, string relative) => _s.ResolveRelative(PatientDir(pid), relative);
}

/// <summary>Dependencias compartidas por los almacenes del filesystem.</summary>
public sealed class FsContext
{
    public FileSystemOptions Options { get; }
    public SafePath Safe { get; }
    public DataPaths Paths { get; }
    public AtomicFileWriter Writer { get; }
    public LockManager Locks { get; }
    public IClock Clock { get; }
    public IDiskSpaceProbe Disk { get; }
    public IFaultInjector Faults { get; }

    public FsContext(FileSystemOptions options, IClock? clock = null, IDiskSpaceProbe? disk = null, IFaultInjector? faults = null, LockManager? locks = null)
    {
        Options = options;
        Safe = new SafePath(options.DataRoot);
        Paths = new DataPaths(Safe);
        Faults = faults ?? new NoFaults();
        Writer = new AtomicFileWriter(Safe, Faults);
        Locks = locks ?? new LockManager();
        Clock = clock ?? new SystemClock();
        Disk = disk ?? new DriveInfoProbe(Safe.Root);
    }

    public void EnsureSpace()
    {
        if (Disk.FreeBytes() < Options.MinFreeDiskBytes)
            throw new DomainException(ErrorCode.InsufficientStorage, "Espacio en disco insuficiente para aceptar nuevas escrituras.");
    }
}

public sealed record StartupReport(int TempFilesRemoved, int SchemasSeeded);

/// <summary>Prepara la raíz de datos, toma el candado de instancia y limpia temporales huérfanos.</summary>
public sealed class DataRootHost : IDisposable
{
    private FileStream? _lock;
    private readonly FsContext _ctx;

    public StartupReport Report { get; private set; } = new(0, 0);

    public DataRootHost(FsContext ctx) => _ctx = ctx;

    public StartupReport Initialize()
    {
        var root = _ctx.Safe.Root;
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"La raíz de datos '{root}' no existe o no se puede escribir: {ex.Message}", ex);
        }

        try
        {
            _lock = new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Otra instancia del servidor ya usa la raíz de datos '{root}'.", ex);
        }

        foreach (var d in new[] { _ctx.Paths.PatientsDir, _ctx.Paths.CampaignsDir, _ctx.Paths.GlobalDir, _ctx.Paths.SchemasDir, _ctx.Paths.StagingDir })
            Directory.CreateDirectory(d);

        var removed = 0;
        foreach (var f in Directory.EnumerateFiles(root, ".tmp-*", SearchOption.AllDirectories))
        {
            try { File.Delete(f); removed++; } catch { }
        }

        var seeded = 0;
        foreach (var (name, text) in DefaultSchemas.All())
        {
            var path = _ctx.Safe.ResolveRelative(_ctx.Paths.SchemasDir, name + ".json");
            if (File.Exists(path)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            seeded++;
        }

        Report = new StartupReport(removed, seeded);
        return Report;
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

public sealed class DirectorySchemaSource : ISchemaSource
{
    private readonly string _dir;
    public DirectorySchemaSource(string schemasDir) => _dir = schemasDir;

    public string? Load(string name)
    {
        foreach (var part in name.Split('/')) SafePath.ValidateSegment(part);
        var path = Path.Combine(new[] { _dir }.Concat(name.Split('/')).ToArray()) + ".json";
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
