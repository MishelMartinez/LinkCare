namespace Campana.Core;

public sealed record ConsistencyReport(
    DateTimeOffset Hora,
    int PacientesRevisadas,
    int PacientesReparadas,
    bool IndiceReconstruido,
    int SolicitudesRecuperadas,
    IReadOnlyList<string> Errores,
    IReadOnlyList<string> Hallazgos);

public sealed record ImportResult(
    string ImportId,
    string CampanaOrigen,
    int Pacientes,
    int ArchivosImportados,
    int ArchivosOmitidos,
    IReadOnlyList<string> Advertencias,
    IReadOnlyList<string>? Origenes = null);

public sealed record HealthSnapshot(
    string Estado,
    long EspacioLibreBytes,
    string? CampanaActiva,
    ConsistencyReport? UltimaVerificacion);
