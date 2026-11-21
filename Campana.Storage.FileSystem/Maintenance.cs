using Campana.Core;

namespace Campana.Storage.FileSystem;

public sealed class FileSystemRebuildService : IRebuildService
{
    private readonly FsContext _ctx;
    private readonly FileSystemPatientStore _patients;
    private readonly PatientStateRebuilder _rebuilder;

    public FileSystemRebuildService(FsContext ctx, FileSystemPatientStore patients)
    {
        _ctx = ctx;
        _patients = patients;
        _rebuilder = new PatientStateRebuilder(ctx, patients.States);
    }

    internal PatientStateRebuilder Rebuilder => _rebuilder;

    public async Task RebuildPatientAsync(string patientId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        if (!Directory.Exists(_ctx.Paths.PatientDir(patientId))) throw DomainException.NotFound("No existe la paciente.");
        using var _ = await _ctx.Locks.AcquirePatientAsync(patientId, ct).ConfigureAwait(false);
        await _rebuilder.RebuildAsync(patientId, ct).ConfigureAwait(false);
    }

    public async Task RebuildIndexesAsync(CancellationToken ct)
    {
        await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
        using var _ = await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false);
        await _patients.Index.RebuildCoreAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> RebuildAllAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.PatientsDir))
        {
            var pid = Path.GetFileName(dir);
            if (!Ids.IsPatientId(pid)) continue;
            await RebuildPatientAsync(pid, ct).ConfigureAwait(false);
            count++;
        }
        await RebuildIndexesAsync(ct).ConfigureAwait(false);
        return count;
    }
}

public sealed class FileSystemConsistencyService : IConsistencyService
{
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;
    private readonly FileSystemPatientStore _patients;
    private readonly FileSystemRebuildService _rebuild;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private volatile ConsistencyReport? _last;

    public FileSystemConsistencyService(FsContext ctx, ICampaignStore campaigns, FileSystemPatientStore patients, FileSystemRebuildService rebuild)
    {
        _ctx = ctx;
        _campaigns = campaigns;
        _patients = patients;
        _rebuild = rebuild;
    }

    public ConsistencyReport? LastReport => _last;

    public async Task<ConsistencyReport> RunAsync(CancellationToken ct)
    {
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
            var active = await _campaigns.GetActiveAsync(ct).ConfigureAwait(false);
            var errors = new List<string>();
            var findings = new List<string>();
            var checkedCount = 0;
            var repaired = 0;
            var recovered = 0;

            var onDisk = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.PatientsDir))
            {
                var pid = Path.GetFileName(dir);
                if (Ids.IsPatientId(pid) && File.Exists(_ctx.Paths.IdentityFile(pid))) onDisk.Add(pid);
            }

            var indexRebuilt = false;
            // Estados de las pacientes
            foreach (var pid in onDisk)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var _ = await _ctx.Locks.AcquirePatientAsync(pid, ct).ConfigureAwait(false);
                    var outcome = await _rebuild.Rebuilder.RebuildAsync(pid, ct).ConfigureAwait(false);
                    checkedCount++;
                    recovered += outcome.RequestsRecovered;
                    if (outcome.Changed)
                    {
                        repaired++;
                        findings.Add($"Estado reconstruido: {pid}.");
                    }
                    errors.AddRange(outcome.Errors);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{pid}: {ex.Message}");
                }
            }

            // Índice contra disco
            var index = _patients.Index.Snapshot().ToDictionary(e => e.PatientId);
            var mismatch = index.Count != onDisk.Count || onDisk.Any(pid => !index.TryGetValue(pid, out var e) || !e.Campanas.SequenceEqual(_patients.Index.CampaignsOf(pid)));
            if (mismatch)
            {
                using (await _ctx.Locks.AcquireIndexAsync(ct).ConfigureAwait(false))
                    await _patients.Index.RebuildCoreAsync(ct).ConfigureAwait(false);
                indexRebuilt = true;
                findings.Add("Índice de pacientes reconstruido.");
            }

            var report = new ConsistencyReport(_ctx.Clock.UtcNow, checkedCount, repaired, indexRebuilt, recovered, errors, findings);
            var path = active is not null ? _ctx.Paths.HealthFile(active.CampaignId) : _ctx.Paths.RootHealthFile;
            await _ctx.Writer.WriteJsonReplaceAsync(path, report, ct).ConfigureAwait(false);
            _last = report;
            return report;
        }
        finally { _runGate.Release(); }
    }
}
