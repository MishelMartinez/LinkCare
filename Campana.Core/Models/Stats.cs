namespace Campana.Core;

public sealed record ConsultorioStats(string ConsultorioId, string Nombre, int Visitas, int Estudios);
public sealed record DayStats(DateOnly Fecha, int Visitas, int Estudios);

/// <summary>
/// Cifras de una campaña para el tablero. Son agregados: no llevan nombres ni datos personales.
/// "Casos sospechosos" son estudios con hallazgo de alerta (mama: BIRADS 4 o 5; Papanicolaou: marcado como sospechoso).
/// No son diagnósticos: confirmarlos es decisión médica.
/// </summary>
public sealed record CampaignStats(
    string CampaignId,
    string Lugar,
    string Estado,
    string UnidadGeografica,
    DateTimeOffset GeneradoEn,
    int PacientesRegistradas,
    int PacientesAtendidas,
    int PacientesNuevas,
    int PacientesRecurrentes,
    int Visitas,
    int Estudios,
    int EstudiosMama,
    int EstudiosPapanicolaou,
    int CasosSospechosos,
    int PacientesConCasoSospechoso,
    int CasosSeguimiento,
    IReadOnlyDictionary<string, int> PorBirads,
    int Adjuntos,
    IReadOnlyList<ConsultorioStats> PorConsultorio,
    IReadOnlyList<DayStats> PorDia);

/// <summary>Lo que hizo un usuario en una campaña. Solo cuentas: no incluye datos de pacientes.</summary>
public sealed record UserActivity(
    string Usuario,
    int Visitas,
    int Estudios,
    int Adjuntos,
    int CambiosDeDatos,
    int Pacientes,
    DateTimeOffset? UltimaActividad);

public sealed record GlobalStats(
    DateTimeOffset GeneradoEn,
    int Campanas,
    int PacientesAtendidas,
    int PacientesNuevas,
    int PacientesRecurrentes,
    int Visitas,
    int Estudios,
    int EstudiosMama,
    int EstudiosPapanicolaou,
    int CasosSospechosos,
    int PacientesConCasoSospechoso,
    int CasosSeguimiento,
    IReadOnlyDictionary<string, int> PorBirads,
    IReadOnlyList<CampaignStats> PorCampana);

/// <summary>Lo que una paciente hizo en una campaña. Lo arma el almacenamiento y lo cuenta StatsCalculator.</summary>
public sealed record PatientActivity(string PatientId, IReadOnlyList<EventDocument> Eventos, bool Recurrente);

public sealed record MinicubeRequest(string? ClaveGeografica, List<string>? CampaignIds, bool IncluirImagenes = true, bool SoloConcluidas = true);

public sealed record MinicubeResult(
    string MinicubeId,
    string Ruta,
    string? ClaveGeografica,
    IReadOnlyList<string> Campanas,
    int Pacientes,
    int Archivos,
    long Bytes,
    DateTimeOffset CreadoEn,
    string CreadoPor);

public sealed record SyncConflict(string Ruta, string Motivo, string? HashLocal, string? HashRemoto);

public sealed record SyncReport(
    string CampaignId,
    DateTimeOffset Hora,
    int Subidos,
    int Omitidos,
    long Bytes,
    IReadOnlyList<SyncConflict> Conflictos,
    IReadOnlyList<string> Errores)
{
    public bool Completo => Conflictos.Count == 0 && Errores.Count == 0;
}

// ---- Formato de minicubo.json: el resumen concentrado de campañas anteriores que viaja con el paquete de historial.
public sealed record MinicubeCampaign(string CampaignId, string Lugar, DateOnly FechaInicio, DateOnly FechaFin, string Estado);

public sealed record MinicubeService(string EventId, string Tipo, DateTimeOffset Fecha, string Consultorio, int? Birads, int? ConductaReferencia, bool? Sospechosa);

public sealed record MinicubeVisit(string CampaignId, string? VisitId, DateTimeOffset Fecha, IReadOnlyList<MinicubeService> Servicios);

public sealed record MinicubeFile(string Ruta, string ContentType, string Sha256, long Tamano);

public sealed record MinicubePatient(
    string PatientId,
    string Nombre,
    DateOnly FechaNacimiento,
    string? Documento,
    IReadOnlyList<MinicubeVisit> Visitas,
    IReadOnlyList<MinicubeFile> Adjuntos);

public sealed record MinicubeSummary(
    string MinicubeId,
    DateTimeOffset CreadoEn,
    string CreadoPor,
    string? ClaveGeografica,
    IReadOnlyList<MinicubeCampaign> Campanas,
    IReadOnlyList<MinicubePatient> Pacientes);
