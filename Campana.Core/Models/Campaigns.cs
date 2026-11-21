namespace Campana.Core;

/// <summary>
/// Etapas de la campaña, en orden:
/// asignada (el gobierno asigna el espacio; solo documentos legales) →
/// concientizacion (llegada al espacio, pláticas y podcast mientras se monta el laboratorio) →
/// consulta (laboratorio instalado: captura de expedientes) →
/// concluida (se actualiza el fileshare general y se consulta el tablero).
/// </summary>
public static class CampaignStates
{
    public const string Asignada = "asignada";
    public const string Concientizacion = "concientizacion";
    public const string Consulta = "consulta";
    public const string Concluida = "concluida";

    public static readonly string[] Sequence = { Asignada, Concientizacion, Consulta, Concluida };

    public static bool IsValid(string? s) => s is not null && Array.IndexOf(Sequence, s) >= 0;

    /// <summary>Solo se avanza una etapa a la vez y nunca se retrocede.</summary>
    public static bool CanAdvance(string from, string to)
    {
        var a = Array.IndexOf(Sequence, from);
        var b = Array.IndexOf(Sequence, to);
        return a >= 0 && b == a + 1;
    }
}

public sealed record ConsultorioInfo(string ConsultorioId, string Nombre);

/// <summary>Unidad geográfica de la campaña. El minicubo de historial se arma por esta clave.</summary>
public sealed record GeoUnit(string? Estado, string? Municipio, string? Localidad, string Clave);

public sealed record StateTransition(string Estado, DateTimeOffset En, string Por);

public sealed record NewCampaign(
    string Lugar,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    string ZonaHoraria,
    List<ConsultorioInfo> Consultorios,
    Dictionary<string, int>? Formularios,
    GeoUnit? UnidadGeografica = null);

public sealed record Campaign(
    string CampaignId,
    string Lugar,
    string Estado,
    string ZonaHoraria,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    List<ConsultorioInfo> Consultorios,
    Dictionary<string, int> Formularios,
    DateTimeOffset CreadaEn,
    DateTimeOffset? ConcluidaEn,
    GeoUnit UnidadGeografica,
    List<StateTransition> Transiciones)
{
    /// <summary>En campo: concientización o consulta. Solo una campaña puede estar en campo por servidor.</summary>
    public bool IsOperating => Estado is CampaignStates.Concientizacion or CampaignStates.Consulta;
    public bool AllowsClinicalWrites => Estado == CampaignStates.Consulta;
    public bool IsConcluded => Estado == CampaignStates.Concluida;
}

/// <summary>Autoriza escribir en una campaña mientras dure. Impide que la campaña se concluya a mitad de una escritura.</summary>
public interface ICampaignLease : IAsyncDisposable
{
    Campaign Campaign { get; }
}

public sealed record DocumentRequest(string Tipo, string? Descripcion, string? NombreOriginal, string? ContentTypeDeclarado, string SubidoPor);

/// <summary>Documento que da certeza legal a la campaña (convenios, oficios, autorizaciones).</summary>
public sealed record CampaignDocument(
    string DocumentId,
    string Tipo,
    string? Descripcion,
    string? NombreOriginal,
    string ContentType,
    string Sha256,
    long Tamano,
    string SubidoPor,
    DateTimeOffset SubidoEn,
    string Ruta);
