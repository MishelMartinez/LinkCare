using System.Text.Json;

namespace Campana.Core;

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public interface IDiskSpaceProbe { long FreeBytes(); }

public interface ICampaignStore
{
    /// <summary>Crea la campaña en la etapa "asignada".</summary>
    Task<Campaign> CreateAsync(NewCampaign request, string createdBy, CancellationToken ct);
    Task<Campaign?> GetAsync(string campaignId, CancellationToken ct);
    Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct);
    /// <summary>La campaña que está en campo (concientización o consulta), si hay una.</summary>
    Task<Campaign?> GetActiveAsync(CancellationToken ct);
    /// <summary>Avanza una etapa. Al concluir espera a que terminen las escrituras en curso.</summary>
    Task<Campaign> TransitionAsync(string campaignId, string to, string by, CancellationToken ct);
    /// <summary>Solo en la etapa de consulta. Mientras el lease exista, la campaña no se puede concluir.</summary>
    Task<ICampaignLease> AcquireWriteLeaseAsync(string campaignId, CancellationToken ct);
}

public static class CampaignStoreExtensions
{
    public static Task<Campaign> CreateAsync(this ICampaignStore s, NewCampaign request, CancellationToken ct) => s.CreateAsync(request, "sistema", ct);
    public static Task<Campaign> CloseAsync(this ICampaignStore s, string campaignId, CancellationToken ct) => s.TransitionAsync(campaignId, CampaignStates.Concluida, "sistema", ct);

    /// <summary>Crea la campaña y la lleva a la etapa de consulta (útil en pruebas y demostraciones).</summary>
    public static async Task<Campaign> CreateInConsultaAsync(this ICampaignStore s, NewCampaign request, CancellationToken ct, string by = "sistema")
    {
        var c = await s.CreateAsync(request, by, ct);
        await s.TransitionAsync(c.CampaignId, CampaignStates.Concientizacion, by, ct);
        return await s.TransitionAsync(c.CampaignId, CampaignStates.Consulta, by, ct);
    }
}

public sealed record DocumentContent(CampaignDocument Document, Stream Content);

public interface ICampaignDocumentStore
{
    Task<CampaignDocument> AddAsync(string campaignId, DocumentRequest request, Stream content, CancellationToken ct);
    Task<IReadOnlyList<CampaignDocument>> ListAsync(string campaignId, CancellationToken ct);
    Task<DocumentContent?> OpenAsync(string campaignId, string documentId, CancellationToken ct);
}

public interface IPatientStore
{
    /// <summary>Registra una paciente en la campaña (nueva, recurrente, o devuelve candidatas a duplicado).</summary>
    Task<RegisterPatientResult> RegisterAsync(string campaignId, RegisterPatientRequest request, string createdBy, CancellationToken ct);
    /// <summary>Busca coincidencias por varios atributos. Solo informa: nunca vincula.</summary>
    Task<IReadOnlyList<PatientCandidate>> FindMatchesAsync(PatientIdentity identity, CancellationToken ct);
    Task<PatientRecord?> GetAsync(string patientId, CancellationToken ct);
    Task<IReadOnlyList<PatientSummary>> SearchAsync(PatientQuery query, CancellationToken ct);
    /// <summary>Vínculo confirmado por una persona. No fusiona ni mueve archivos.</summary>
    Task LinkAsync(PatientLink link, CancellationToken ct);
    /// <summary>Revoca el vínculo del expediente indicado (el que se había absorbido).</summary>
    Task UnlinkAsync(string patientId, string revokedBy, string motivo, CancellationToken ct);
    Task<IReadOnlyList<PatientLink>> ListLinksAsync(string patientId, CancellationToken ct);
    Task<bool> IsRegisteredInCampaignAsync(string patientId, string campaignId, CancellationToken ct);
    /// <summary>Sigue los vínculos hasta el ID canónico. Falla con NotFound si la paciente no existe.</summary>
    Task<string> ResolveCanonicalIdAsync(string patientId, CancellationToken ct);
}

public interface IEventStore
{
    Task<AppendResult> AppendAsync(NewEvent evt, CancellationToken ct);
    Task<IReadOnlyList<EventSummary>> ListAsync(string patientId, CancellationToken ct);
    Task<EventDocument?> GetAsync(string patientId, string eventId, CancellationToken ct);
}

public interface IAttachmentStore
{
    Task<AttachmentResult> SaveAsync(AttachmentRequest request, Stream content, CancellationToken ct);
    Task<AttachmentContent?> OpenAsync(string patientId, string campaignId, string attachmentId, CancellationToken ct);
}

public interface IRebuildService
{
    Task RebuildPatientAsync(string patientId, CancellationToken ct);
    Task RebuildIndexesAsync(CancellationToken ct);
    /// <summary>Reconstruye los estados de todas las pacientes y los índices. Devuelve cuántas pacientes procesó.</summary>
    Task<int> RebuildAllAsync(CancellationToken ct);
}

public interface IConsistencyService
{
    Task<ConsistencyReport> RunAsync(CancellationToken ct);
    ConsistencyReport? LastReport { get; }
}

public interface IHistoryImporter
{
    Task<ImportResult> ImportAsync(string packagePath, string activeCampaignId, CancellationToken ct);
    /// <summary>Elimina del disco solo los archivos que se importaron de esa campaña.</summary>
    Task<int> PurgeImportedAsync(string originCampaignId, CancellationToken ct);
}

public interface IStatsService
{
    Task<CampaignStats> GetCampaignStatsAsync(string campaignId, CancellationToken ct);
    /// <summary>Qué hizo cada usuario en la campaña (quién creó cada visita, estudio o adjunto).</summary>
    Task<IReadOnlyList<UserActivity>> GetActivityByUserAsync(string campaignId, CancellationToken ct);
}

public interface IMinicubeBuilder
{
    Task<MinicubeResult> BuildAsync(MinicubeRequest request, string createdBy, CancellationToken ct);
    Task<IReadOnlyList<MinicubeResult>> ListAsync(CancellationToken ct);
    /// <summary>Escribe el minicubo como un .zip listo para llevar a otro servidor.</summary>
    Task<bool> WriteZipAsync(string minicubeId, Stream destination, CancellationToken ct);
}

public interface IFileShareSync
{
    bool IsConfigured { get; }
    Task<SyncReport> SyncCampaignAsync(string campaignId, CancellationToken ct);
    Task<SyncReport?> LastReportAsync(string campaignId, CancellationToken ct);
}

public sealed record SchemaError(string Path, string Message);

public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<SchemaError> Errors)
{
    public static readonly SchemaValidationResult Ok = new(true, Array.Empty<SchemaError>());
}

public interface ISchemaValidator
{
    /// <summary>Valida el sobre que envía el cliente.</summary>
    SchemaValidationResult ValidateEnvelope(JsonElement envelope);
    /// <summary>Valida el payload contra el esquema del tipo y versión. Falla si no existe el esquema.</summary>
    SchemaValidationResult ValidatePayload(string tipo, int version, JsonElement payload);
}

public interface ISchemaSource
{
    string? Load(string name);
}

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct);
}
