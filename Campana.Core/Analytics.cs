using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campana.Core;

public static class VisitsBuilder
{
    /// <summary>Agrupa los servicios por visita. Los eventos sin visita quedan aparte.</summary>
    public static PatientVisits Build(string patientId, IEnumerable<EventSummary> eventos)
    {
        var all = StateBuilder.Order(eventos);
        var visits = all.Where(e => e.Tipo == EventTypes.Visita).ToList();
        var byVisit = all.Where(e => e.Tipo != EventTypes.Visita && e.VisitId is not null).GroupBy(e => e.VisitId!).ToDictionary(g => g.Key, g => g.ToList());
        var views = visits.Select(v => new VisitView(v.EventId, v.CampaignId, v.CreatedAt, v.ConsultorioId, v.CreatedBy,
            byVisit.TryGetValue(v.EventId, out var s) ? s : new List<EventSummary>())).ToList();
        var known = visits.Select(v => v.EventId).ToHashSet();
        var loose = all.Where(e => e.Tipo != EventTypes.Visita && (e.VisitId is null || !known.Contains(e.VisitId))).ToList();
        return new PatientVisits(patientId, views, loose);
    }
}

/// <summary>Cuenta lo que pasó en una campaña. Son reglas puras: el almacenamiento solo entrega los eventos.</summary>
public static class StatsCalculator
{
    public static readonly string[] BiradsKeys = { "0", "1", "2", "3", "4", "5", "sin_dato" };

    /// <summary>
    /// Aplica las correcciones: el campo "datos" de una corrección se combina sobre la atención que corrige.
    /// El archivo original no cambia; solo se usa para las cifras.
    /// </summary>
    public static Dictionary<string, JsonNode> EffectivePayloads(IEnumerable<EventDocument> eventos)
    {
        var ordered = eventos.OrderBy(e => e.CreatedAt).ThenBy(e => e.EventId, StringComparer.Ordinal).ToList();
        var map = new Dictionary<string, JsonNode>();
        foreach (var e in ordered)
            if (e.Tipo is EventTypes.Mama or EventTypes.Papanicolaou) map[e.EventId] = JsonNode.Parse(e.Payload.GetRawText())!;
        foreach (var e in ordered.Where(x => x.Tipo == EventTypes.Correccion))
        {
            var target = EventRules.CorrectsEventId(e.Payload);
            if (target is null || !map.TryGetValue(target, out var node)) continue;
            if (e.Payload.TryGetProperty("datos", out var datos) && datos.ValueKind == JsonValueKind.Object)
                Merge(node.AsObject(), JsonNode.Parse(datos.GetRawText())!.AsObject());
        }
        return map;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (k, v) in patch.ToList())
        {
            if (v is JsonObject po && target[k] is JsonObject to) Merge(to, po);
            else target[k] = v?.DeepClone();
        }
    }

    public static CampaignStats Compute(Campaign campaign, int registradas, IEnumerable<PatientActivity> activities, DateTimeOffset now)
    {
        var zone = EventPaths.ResolveZone(campaign.ZonaHoraria);
        int atendidas = 0, nuevas = 0, recurrentes = 0, visitas = 0, mama = 0, papa = 0, sospechosos = 0, pacientesSosp = 0, seguimiento = 0, adjuntos = 0;
        var birads = BiradsKeys.ToDictionary(k => k, _ => 0);
        var porConsultorio = campaign.Consultorios.ToDictionary(c => c.ConsultorioId, c => (Nombre: c.Nombre, Visitas: 0, Estudios: 0));
        var porDia = new SortedDictionary<DateOnly, (int Visitas, int Estudios)>();

        foreach (var a in activities)
        {
            var effective = EffectivePayloads(a.Eventos);
            var any = false;
            var patientSuspicious = false;
            foreach (var e in a.Eventos)
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.CreatedAt, zone).DateTime);
                switch (e.Tipo)
                {
                    case EventTypes.Visita:
                        visitas++; any = true;
                        Bump(porConsultorio, e.ConsultorioId, 1, 0);
                        porDia[day] = porDia.TryGetValue(day, out var dv) ? (dv.Visitas + 1, dv.Estudios) : (1, 0);
                        break;
                    case EventTypes.Mama:
                    case EventTypes.Papanicolaou:
                        any = true;
                        Bump(porConsultorio, e.ConsultorioId, 0, 1);
                        porDia[day] = porDia.TryGetValue(day, out var ds) ? (ds.Visitas, ds.Estudios + 1) : (0, 1);
                        var payload = effective.TryGetValue(e.EventId, out var n) ? n : null;
                        if (e.Tipo == EventTypes.Mama)
                        {
                            mama++;
                            var b = Birads(payload);
                            birads[b is null ? "sin_dato" : b.Value.ToString()]++;
                            if (b is 4 or 5) { sospechosos++; patientSuspicious = true; }
                            else if (b is 0 or 3) seguimiento++;
                        }
                        else
                        {
                            papa++;
                            if (payload?["sospechosa"]?.GetValueKind() == JsonValueKind.True) { sospechosos++; patientSuspicious = true; }
                        }
                        break;
                    case EventTypes.Adjunto:
                        adjuntos++;
                        break;
                }
            }
            if (!any) continue;
            atendidas++;
            if (a.Recurrente) recurrentes++; else nuevas++;
            if (patientSuspicious) pacientesSosp++;
        }

        return new CampaignStats(
            campaign.CampaignId, campaign.Lugar, campaign.Estado, campaign.UnidadGeografica.Clave, now,
            registradas, atendidas, nuevas, recurrentes, visitas, mama + papa, mama, papa,
            sospechosos, pacientesSosp, seguimiento, birads, adjuntos,
            porConsultorio.Select(kv => new ConsultorioStats(kv.Key, kv.Value.Nombre, kv.Value.Visitas, kv.Value.Estudios)).OrderBy(c => c.ConsultorioId, StringComparer.Ordinal).ToList(),
            porDia.Select(kv => new DayStats(kv.Key, kv.Value.Visitas, kv.Value.Estudios)).ToList());
    }

    private static void Bump(Dictionary<string, (string Nombre, int Visitas, int Estudios)> map, string id, int v, int s)
    {
        map[id] = map.TryGetValue(id, out var c) ? (c.Nombre, c.Visitas + v, c.Estudios + s) : (id, v, s);
    }

    private static int? Birads(JsonNode? payload)
    {
        var node = payload?["diagnostico"]?["birads"];
        if (node is JsonValue v && v.TryGetValue<int>(out var i) && i is >= 0 and <= 5) return i;
        return null;
    }

    /// <summary>Cuenta lo que hizo cada usuario a partir de los resúmenes de eventos de la campaña.</summary>
    public static List<UserActivity> ByUser(IEnumerable<EventSummary> eventos)
        => eventos.Where(e => !string.IsNullOrEmpty(e.CreatedBy))
            .GroupBy(e => e.CreatedBy, StringComparer.Ordinal)
            .Select(g => new UserActivity(
                g.Key,
                g.Count(e => e.Tipo == EventTypes.Visita),
                g.Count(e => e.Tipo is EventTypes.Mama or EventTypes.Papanicolaou),
                g.Count(e => e.Tipo == EventTypes.Adjunto),
                g.Count(e => e.Tipo == EventTypes.ActualizacionDatos),
                g.Select(e => e.PatientId).Distinct().Count(),
                g.Max(e => e.CreatedAt)))
            .OrderBy(a => a.Usuario, StringComparer.Ordinal).ToList();

    public static GlobalStats Aggregate(IReadOnlyList<CampaignStats> campaigns, DateTimeOffset now)
    {
        var birads = BiradsKeys.ToDictionary(k => k, k => campaigns.Sum(c => c.PorBirads.TryGetValue(k, out var n) ? n : 0));
        return new GlobalStats(now, campaigns.Count,
            campaigns.Sum(c => c.PacientesAtendidas), campaigns.Sum(c => c.PacientesNuevas), campaigns.Sum(c => c.PacientesRecurrentes),
            campaigns.Sum(c => c.Visitas), campaigns.Sum(c => c.Estudios), campaigns.Sum(c => c.EstudiosMama), campaigns.Sum(c => c.EstudiosPapanicolaou),
            campaigns.Sum(c => c.CasosSospechosos), campaigns.Sum(c => c.PacientesConCasoSospechoso), campaigns.Sum(c => c.CasosSeguimiento),
            birads, campaigns);
    }
}
