using System.Text.Json;

namespace Campana.Core;

public static class EventTypes
{
    public const string Registro = "registro";
    public const string Mama = "mama";
    public const string Papanicolaou = "papanicolaou";
    public const string Adjunto = "adjunto";
    public const string Correccion = "correccion";
    /// <summary>Una visita (llegada de la paciente). Los servicios que recibe llevan su visitId.</summary>
    public const string Visita = "visita";
    /// <summary>Cambio parcial de datos de identidad, con detección de conflictos.</summary>
    public const string ActualizacionDatos = "actualizacion-datos";
}

/// <summary>Atención tal como la envía el cliente, más los datos que agrega el servidor.</summary>
public sealed record NewEvent(
    string ClientRequestId,
    string Tipo,
    int TipoSchemaVersion,
    string PatientId,
    string CampaignId,
    string ConsultorioId,
    string CreatedBy,
    JsonElement Payload,
    string? VisitId = null);

public sealed record EventDocument(
    int SchemaVersion,
    string EventId,
    string ClientRequestId,
    string Tipo,
    int TipoSchemaVersion,
    string PatientId,
    string CampaignId,
    string ConsultorioId,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    JsonElement Payload,
    string? VisitId = null);

/// <summary>Ruta relativa a la carpeta de la paciente (por ejemplo campaigns/camp-x/2026/08/18/T...json).</summary>
public sealed record EventSummary(
    string EventId,
    string PatientId,
    string CampaignId,
    string Tipo,
    string ConsultorioId,
    DateTimeOffset CreatedAt,
    string Ruta,
    string? VisitId = null,
    string CreatedBy = "");

public sealed record CampaignState(string PatientId, string CampaignId, int Version, List<EventSummary> Eventos);

public sealed record PatientState(string PatientId, int Version, List<EventSummary> Eventos);

public sealed record AppendResult(string EventId, string Ruta, bool Duplicado, int Version);

public sealed record RequestRecord(string EventId, string PatientId, string Ruta, DateTimeOffset CreatedAt);

public sealed record AttachmentRequest(
    string PatientId,
    string CampaignId,
    string ConsultorioId,
    string CreatedBy,
    string ClientRequestId,
    string? NombreOriginal,
    string? ContentTypeDeclarado,
    string? Sha256Esperado,
    string? VisitId = null);

public sealed record AttachmentResult(
    string AttachmentId,
    string EventId,
    string Sha256,
    long Tamano,
    string ContentType,
    string RutaRelativa,
    bool Duplicado);

public sealed record AttachmentContent(Stream Content, string ContentType);

public sealed record AuditEntry(
    DateTimeOffset Hora,
    string Usuario,
    string? ConsultorioId,
    string Accion,
    string Recurso,
    string? CampaignId);

public sealed record AuditQuery(string? CampaignId, DateTimeOffset? Desde, DateTimeOffset? Hasta, string? Usuario, string? Accion, string? Recurso, int Limit = 200);
