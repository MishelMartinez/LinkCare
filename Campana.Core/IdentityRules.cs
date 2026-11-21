using System.Globalization;
using System.Text.Json;

namespace Campana.Core;

public sealed record IdentityUpdate(int Version, IReadOnlyDictionary<string, string?> Cambios, string Por, DateTimeOffset En, string EventId);

public sealed record IdentityConflictDetail(string Campo, string? ValorActual, string? ValorEnviado, int VersionActual, string ModificadoPor);

/// <summary>
/// Cambios parciales de identidad. Cada cambio es un evento "actualizacion-datos" con la versión de identidad que vio quien lo envía.
/// Si otra persona cambió el mismo dato con otro valor después de esa versión, hay conflicto y lo resuelve una persona.
/// Los cambios a datos distintos se combinan sin problema.
/// </summary>
public static class IdentityRules
{
    public static readonly string[] AllowedFields =
    {
        "nombres", "apellidos", "fechaNacimiento", "entidadNacimiento",
        "documento.tipo", "documento.numero",
        "contacto.telefono", "contacto.celular", "contacto.localidad", "contacto.residencia",
    };

    private static readonly HashSet<string> Required = new() { "nombres", "apellidos", "fechaNacimiento" };

    public static (int BaseVersion, Dictionary<string, string?> Cambios) ParsePayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("baseVersion", out var bv) || bv.ValueKind != JsonValueKind.Number || !bv.TryGetInt32(out var baseVersion))
            throw DomainException.Validation("baseVersion es obligatoria (la versión de identidad que vio quien envía el cambio).");
        if (!payload.TryGetProperty("cambios", out var cambios) || cambios.ValueKind != JsonValueKind.Object)
            throw DomainException.Validation("cambios es obligatorio.");
        var result = new Dictionary<string, string?>();
        var errors = new List<SchemaError>();
        foreach (var prop in cambios.EnumerateObject())
        {
            if (!AllowedFields.Contains(prop.Name)) { errors.Add(new SchemaError("/cambios/" + prop.Name, "Campo no permitido.")); continue; }
            string? value = prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                _ => null,
            };
            if (prop.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                errors.Add(new SchemaError("/cambios/" + prop.Name, "Debe ser texto o null."));
                continue;
            }
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (value is null && Required.Contains(prop.Name)) errors.Add(new SchemaError("/cambios/" + prop.Name, "No se puede dejar vacío."));
            if (value is { Length: > 500 }) errors.Add(new SchemaError("/cambios/" + prop.Name, "Hasta 500 caracteres."));
            if (prop.Name is "nombres" or "apellidos" && value is { Length: > 200 }) errors.Add(new SchemaError("/cambios/" + prop.Name, "Hasta 200 caracteres."));
            if (prop.Name == "fechaNacimiento" && value is not null)
            {
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d < new DateOnly(1900, 1, 1))
                    errors.Add(new SchemaError("/cambios/fechaNacimiento", "Use el formato AAAA-MM-DD."));
            }
            result[prop.Name] = value;
        }
        if (result.Count == 0 && errors.Count == 0) errors.Add(new SchemaError("/cambios", "Indique al menos un cambio."));
        if (errors.Count > 0) throw DomainException.Validation("Los cambios de identidad no son válidos.", errors);
        return (baseVersion, result);
    }

    public static IdentityUpdate ToUpdate(int version, EventDocument doc)
    {
        var (_, cambios) = ParsePayload(doc.Payload);
        return new IdentityUpdate(version, cambios, doc.CreatedBy, doc.CreatedAt, doc.EventId);
    }

    /// <summary>Lanza IdentityConflict si algún dato que se quiere cambiar ya lo cambió otra persona con otro valor.</summary>
    public static void CheckConflicts(IReadOnlyList<IdentityUpdate> existing, int baseVersion, IReadOnlyDictionary<string, string?> nuevos)
    {
        if (baseVersion < 0 || baseVersion > existing.Count)
            throw DomainException.Validation($"baseVersion {baseVersion} no es válida: la versión actual de identidad es {existing.Count}.");

        var conflicts = new List<IdentityConflictDetail>();
        foreach (var (campo, nuevo) in nuevos)
        {
            var last = existing.Where(u => u.Version > baseVersion && u.Cambios.ContainsKey(campo)).OrderByDescending(u => u.Version).FirstOrDefault();
            if (last is null) continue;
            var actual = last.Cambios[campo];
            if (!string.Equals(actual, nuevo, StringComparison.Ordinal))
                conflicts.Add(new IdentityConflictDetail(campo, actual, nuevo, last.Version, last.Por));
        }
        if (conflicts.Count > 0)
            throw new DomainException(ErrorCode.IdentityConflict,
                "Otra persona cambió estos datos después de la versión que usted vio. Revíselos y envíe el cambio de nuevo con la versión actual.", conflicts);
    }

    public static PatientIdentityDocument Apply(PatientIdentityDocument d, IEnumerable<IdentityUpdate> updates)
    {
        foreach (var u in updates.OrderBy(x => x.Version))
            foreach (var (campo, valor) in u.Cambios)
                d = campo switch
                {
                    "nombres" => d with { Nombres = valor ?? d.Nombres },
                    "apellidos" => d with { Apellidos = valor ?? d.Apellidos },
                    "fechaNacimiento" => valor is not null && DateOnly.TryParseExact(valor, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? d with { FechaNacimiento = f } : d,
                    "entidadNacimiento" => d with { EntidadNacimiento = valor },
                    "documento.tipo" => d with { Documento = new DocumentoInfo(valor, d.Documento?.Numero) },
                    "documento.numero" => d with { Documento = new DocumentoInfo(d.Documento?.Tipo, valor) },
                    "contacto.telefono" => d with { Contacto = (d.Contacto ?? new ContactoInfo(null, null)) with { Telefono = valor } },
                    "contacto.celular" => d with { Contacto = (d.Contacto ?? new ContactoInfo(null, null)) with { Celular = valor } },
                    "contacto.localidad" => d with { Contacto = (d.Contacto ?? new ContactoInfo(null, null)) with { Localidad = valor } },
                    "contacto.residencia" => d with { Contacto = (d.Contacto ?? new ContactoInfo(null, null)) with { Residencia = valor } },
                    _ => d,
                };
        return d;
    }

    /// <summary>Convierte los eventos de actualización (ya ordenados) en versiones 1, 2, 3…</summary>
    public static List<IdentityUpdate> ToUpdates(IEnumerable<EventDocument> updateEvents)
    {
        var list = new List<IdentityUpdate>();
        foreach (var doc in StateBuilderOrder(updateEvents)) list.Add(ToUpdate(list.Count + 1, doc));
        return list;
    }

    private static IEnumerable<EventDocument> StateBuilderOrder(IEnumerable<EventDocument> docs)
        => docs.OrderBy(d => d.CreatedAt).ThenBy(d => d.EventId, StringComparer.Ordinal);
}
