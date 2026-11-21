using System.Security.Cryptography;
using System.Text.Json;
using Campana.Core;

namespace Campana.Storage.FileSystem;

public sealed class FileSystemAttachmentStore : IAttachmentStore
{
    private readonly FsContext _ctx;
    private readonly ICampaignStore _campaigns;
    private readonly FileSystemPatientStore _patients;
    private readonly FileSystemEventStore _events;

    public FileSystemAttachmentStore(FsContext ctx, ICampaignStore campaigns, FileSystemPatientStore patients, FileSystemEventStore events)
    {
        _ctx = ctx;
        _campaigns = campaigns;
        _patients = patients;
        _events = events;
    }

    public async Task<AttachmentResult> SaveAsync(AttachmentRequest request, Stream content, CancellationToken ct)
    {
        AttachmentRules.ValidateRequest(request);

        var prior = await _events.FindRequestAsync(request.CampaignId, request.ClientRequestId, ct).ConfigureAwait(false);
        if (prior is not null) return await DuplicateAsync(prior, ct).ConfigureAwait(false);

        _ctx.EnsureSpace();
        await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var pid = await _patients.ResolveCanonicalIdAsync(request.PatientId, ct).ConfigureAwait(false);
        var campaign = await _campaigns.GetAsync(request.CampaignId, ct).ConfigureAwait(false)
                       ?? throw DomainException.NotFound("No existe la campaña.");
        if (!campaign.AllowsClinicalWrites) throw CampaignRules.NotWritable(campaign);
        if (!await _patients.IsRegisteredInCampaignAsync(pid, request.CampaignId, ct).ConfigureAwait(false))
            throw DomainException.NotFound("La paciente no está registrada en esta campaña.");

        var allowed = _ctx.Options.AllowedAttachmentTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mediaDir = _ctx.Paths.MediaDir(pid, request.CampaignId);
        var tmp = _ctx.Writer.NewTempPath(Path.Combine(mediaDir, "upload"));
        string? finalPath = null;
        try
        {
            // 1. Copiar al temporal, calculando hash y tamaño, y cortando al superar el máximo
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

            // 2. Tipo (por firma del archivo) e 3. integridad
            var detected = AttachmentRules.CheckContent(head.AsSpan(0, headLen), total, request, allowed);
            AttachmentRules.CheckHash(request.Sha256Esperado, hash);

            // 4. Nombre generado por el servidor; el original solo es metadato
            var attachmentId = Ids.NewAttachmentId();
            var finalName = $"{attachmentId}.{detected.Extension}";
            finalPath = _ctx.Safe.Resolve(Path.GetRelativePath(_ctx.Safe.Root, mediaDir).Split(Path.DirectorySeparatorChar).Append(finalName).ToArray());
            var rutaRelativa = $"campaigns/{request.CampaignId}/media/{finalName}";
            var payload = AttachmentRules.BuildPayload(attachmentId, hash, total, detected.ContentType, request.NombreOriginal, rutaRelativa);

            var evt = new NewEvent(request.ClientRequestId, EventTypes.Adjunto, 1, pid, request.CampaignId, request.ConsultorioId, request.CreatedBy, payload, request.VisitId);
            var result = await _events.AppendCoreAsync(evt, allowReserved: true,
                commit: () => { _ctx.Writer.MoveImmutable(tmp, finalPath); return Task.CompletedTask; }, ct).ConfigureAwait(false);
            if (result.Duplicado) // otra solicitud con el mismo id ganó la carrera
            {
                AtomicFileWriter.TryDelete(tmp);
                return await DuplicateAsync(new RequestRecord(result.EventId, pid, result.Ruta, default), ct).ConfigureAwait(false);
            }
            return new AttachmentResult(attachmentId, result.EventId, hash, total, detected.ContentType, rutaRelativa, false);
        }
        catch (Exception ex) when (ex is not InjectedFaultException)
        {
            AtomicFileWriter.TryDelete(tmp);
            if (finalPath is not null && await _events.FindRequestAsync(request.CampaignId, request.ClientRequestId, CancellationToken.None).ConfigureAwait(false) is null)
                AtomicFileWriter.TryDelete(finalPath);
            throw;
        }
    }

    private async Task<AttachmentResult> DuplicateAsync(RequestRecord prior, CancellationToken ct)
    {
        var doc = await _events.ReadEventAsync(prior.PatientId, prior.Ruta, ct).ConfigureAwait(false)
                  ?? throw DomainException.NotFound("No se encontró el adjunto original.");
        var p = doc.Payload;
        return new AttachmentResult(
            p.GetProperty("attachmentId").GetString()!, doc.EventId, p.GetProperty("sha256").GetString()!,
            p.GetProperty("tamano").GetInt64(), p.GetProperty("contentType").GetString()!, p.GetProperty("rutaRelativa").GetString()!, true);
    }

    public async Task<AttachmentContent?> OpenAsync(string patientId, string campaignId, string attachmentId, CancellationToken ct)
    {
        Ids.RequirePatientId(patientId);
        Ids.RequireCampaignId(campaignId);
        Ids.RequireAttachmentId(attachmentId);
        await _patients.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var canonical = _patients.Links.Resolve(patientId);
        foreach (var id in _patients.Links.Group(canonical))
        {
            var dir = _ctx.Paths.MediaDir(id, campaignId);
            if (!Directory.Exists(dir)) continue;
            var file = Directory.EnumerateFiles(dir, attachmentId + ".*").FirstOrDefault();
            if (file is null) continue;
            var ct2 = FileSignatures.ContentTypeFromExtension(Path.GetExtension(file).TrimStart('.')) ?? "application/octet-stream";
            return new AttachmentContent(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true), ct2);
        }
        return null;
    }
}
