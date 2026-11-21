namespace Campana.Core;

public sealed record DocumentoInfo(string? Tipo, string? Numero);

/// <summary>Teléfono es el fijo. Residencia es la residencia habitual (texto libre).</summary>
public sealed record ContactoInfo(string? Telefono, string? Localidad, string? Celular = null, string? Residencia = null);

public sealed record PatientIdentity(
    string Nombres,
    string Apellidos,
    DateOnly FechaNacimiento,
    DocumentoInfo? Documento,
    ContactoInfo? Contacto,
    string? EntidadNacimiento = null);

/// <summary>Contenido de identity.json (la identidad inicial). Los cambios posteriores son eventos "actualizacion-datos".</summary>
public sealed record PatientIdentityDocument(
    int SchemaVersion,
    string PatientId,
    string Nombres,
    string Apellidos,
    DateOnly FechaNacimiento,
    DocumentoInfo? Documento,
    ContactoInfo? Contacto,
    string CreadaEnCampana,
    DateTimeOffset CreadaEn,
    string? EntidadNacimiento = null,
    string? CreadaPor = null);

public sealed record RegisterPatientRequest(
    PatientIdentity Identity,
    bool ConfirmarNueva = false,
    string? UsarPacienteId = null);

public enum RegisterOutcome { Created, LinkedExisting, DuplicateCandidates }

/// <summary>Una posible coincidencia. Nunca se vincula sola: una persona debe confirmarla.</summary>
public sealed record PatientCandidate(
    string PatientId,
    string Nombre,
    DateOnly FechaNacimiento,
    string Motivo,
    IReadOnlyList<string> Campanas,
    int Puntaje = 0,
    IReadOnlyList<string>? Coincidencias = null);

public sealed record RegisterPatientResult(
    RegisterOutcome Outcome,
    string? PatientId,
    IReadOnlyList<PatientCandidate> Candidatas);

public sealed record PatientSummary(string PatientId, string Nombre, DateOnly FechaNacimiento, IReadOnlyList<string> Campanas);

public sealed record PatientQuery(string? Texto, string? CampaignId, int Limit = 50);

/// <summary>Vínculo confirmado por una persona entre dos expedientes. No mueve ni borra archivos y se puede revocar.</summary>
public sealed record PatientLink(
    string FromId,
    string ToId,
    string LinkedBy,
    DateTimeOffset LinkedAt,
    string? Motivo,
    string? RevokedBy = null,
    DateTimeOffset? RevokedAt = null,
    string? RevokeMotivo = null)
{
    public bool IsActive => RevokedAt is null;
}

public sealed record PatientRecord(
    string PatientId,
    PatientIdentityDocument Identity,
    IReadOnlyList<string> IdsVinculados,
    int Version,
    IReadOnlyList<EventSummary> Eventos,
    int IdentityVersion = 0,
    /// <summary>Campañas en las que la paciente está registrada (la interfaz decide con esto si hay que incorporarla).</summary>
    IReadOnlyList<string>? Campanas = null);

/// <summary>Entrada del índice de pacientes (derivado, reconstruible).</summary>
public sealed record PatientIndexEntry(
    string PatientId,
    string NombreNormalizado,
    string NombreVisible,
    DateOnly FechaNacimiento,
    string? DocumentoNormalizado,
    List<string> Campanas,
    string? LocalidadNormalizada = null,
    List<string>? Telefonos = null);

/// <summary>Una visita agrupa los servicios (estudios) que se hicieron en ella.</summary>
public sealed record VisitView(
    string VisitId,
    string CampaignId,
    DateTimeOffset Fecha,
    string ConsultorioId,
    string CreadaPor,
    IReadOnlyList<EventSummary> Servicios);

public sealed record PatientVisits(string PatientId, IReadOnlyList<VisitView> Visitas, IReadOnlyList<EventSummary> ServiciosSinVisita);
