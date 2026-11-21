using System.Text.Json;
using Campana.Core;

namespace Campana.Server;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, string Username, string Role, string? ConsultorioId, IReadOnlyList<string> Campanas);
public sealed record MeResponse(string Username, string Role, string? ConsultorioId, IReadOnlyList<string> Campanas);

public sealed record UserView(string Username, string Role, string? ConsultorioId, IReadOnlyList<string> Campanas, bool Activo);
public sealed record CreateUserRequest(string? Username, string? Password, string? Role, string? ConsultorioId, List<string>? Campanas);
public sealed record UpdateUserRequest(string? Password, string? Role, string? ConsultorioId, List<string>? Campanas, bool? Activo);

public sealed record CreateCampaignRequest(
    string? Lugar,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    string? ZonaHoraria,
    List<ConsultorioInfo>? Consultorios,
    Dictionary<string, int>? Formularios,
    GeoUnit? UnidadGeografica);

public sealed record TransitionRequest(string? Estado);

public sealed record RegisterPatientDto(
    string? Nombres,
    string? Apellidos,
    DateOnly FechaNacimiento,
    DocumentoInfo? Documento,
    ContactoInfo? Contacto,
    string? EntidadNacimiento = null,
    bool ConfirmarNueva = false,
    string? UsarPacienteId = null);

public sealed record RegisterPatientResponse(string Outcome, string? PatientId, IReadOnlyList<PatientCandidate> Candidatas);

public sealed record AppendEventResponse(string EventId, string Ruta, bool Duplicado, int Version);
public sealed record VisitRequest(string? ClientRequestId, string? ConsultorioId, string? Motivo);
public sealed record IdentityPatchRequest(string? ClientRequestId, string? ConsultorioId, int? BaseVersion, JsonElement Cambios);

public sealed record LinkRequest(string? ToId, string? Motivo, bool Confirmado = false);
public sealed record UnlinkRequest(string? Motivo);
public sealed record ImportRequest(string? RutaPaquete);
public sealed record PurgeRequest(string? CampanaOrigen);
public sealed record MinicubeApiRequest(string? ClaveGeografica, List<string>? CampaignIds, bool? IncluirImagenes, bool? SoloConcluidas);
public sealed record SetSlideRequest(string? DeckId, int Index);

public sealed record ChangeEntry(DateTimeOffset Fecha, string Por, string? ConsultorioId, string? CampaignId, string Tipo, string Descripcion);

/// <summary>Luces de la tarjeta de campaña: cada una se enciende cuando su contador es mayor que cero.</summary>
public sealed record CampaignSignals(int CasosSospechosos, int SinDocumentos, int ConflictosSincronizacion, int SincronizacionPendiente);

public sealed record SyncSummary(DateTimeOffset Hora, bool Completo, int Subidos, int Conflictos, int Errores);

/// <summary>Una campaña con las cifras que muestra su tarjeta en la lista y en el kanban.</summary>
public sealed record CampaignSummary(
    Campaign Campaign,
    int Medicos,
    int PacientesRegistradas,
    int PacientesAtendidas,
    int PacientesNuevas,
    int PacientesRecurrentes,
    int Visitas,
    int Estudios,
    int CasosSospechosos,
    int Documentos,
    bool FileShareConfigurado,
    SyncSummary? Sincronizacion,
    CampaignSignals Senales);

/// <summary>Un médico que participa en la campaña, con su actividad. No incluye datos de pacientes.</summary>
public sealed record MedicoView(
    string Username,
    string? ConsultorioId,
    string? ConsultorioNombre,
    bool Activo,
    bool Asignado,
    int Visitas,
    int Estudios,
    int Adjuntos,
    int CambiosDeDatos,
    int Pacientes,
    DateTimeOffset? UltimaActividad);
