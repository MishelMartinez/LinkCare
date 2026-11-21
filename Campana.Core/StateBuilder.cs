namespace Campana.Core;

/// <summary>Reglas puras para derivar los state.json a partir de los eventos.</summary>
public static class StateBuilder
{
    public static List<EventSummary> Order(IEnumerable<EventSummary> eventos)
        => eventos.OrderBy(e => e.CreatedAt).ThenBy(e => e.EventId, StringComparer.Ordinal).ToList();

    public static CampaignState BuildCampaign(string patientId, string campaignId, IEnumerable<EventSummary> eventos)
    {
        var list = Order(eventos);
        return new CampaignState(patientId, campaignId, list.Count, list);
    }

    public static PatientState BuildPatient(string patientId, IEnumerable<CampaignState> campaigns)
    {
        var list = Order(campaigns.SelectMany(c => c.Eventos));
        return new PatientState(patientId, list.Count, list);
    }

    public static EventSummary Summarize(EventDocument doc, string rutaRelativaAPaciente)
        => new(doc.EventId, doc.PatientId, doc.CampaignId, doc.Tipo, doc.ConsultorioId, doc.CreatedAt, rutaRelativaAPaciente, doc.VisitId, doc.CreatedBy);
}

public static class EventPaths
{
    public static TimeZoneInfo ResolveZone(string zonaHoraria)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(zonaHoraria); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw DomainException.Validation($"Zona horaria desconocida: {zonaHoraria}");
        }
    }

    /// <summary>campaigns/{id}/{AAAA}/{MM}/{DD}/T{hhmmss}-{tipo}-{consultorio}-{eventId8}.json (relativa a la carpeta de la paciente).</summary>
    public static string RelativePath(string campaignId, TimeZoneInfo zone, DateTimeOffset createdAt, string tipo, string consultorioId, string eventId)
    {
        var local = TimeZoneInfo.ConvertTime(createdAt, zone);
        var file = $"T{local:HHmmss}-{tipo}-{consultorioId}-{eventId[..8]}.json";
        return $"campaigns/{campaignId}/{local:yyyy}/{local:MM}/{local:dd}/{file}";
    }
}
