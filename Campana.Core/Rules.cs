using System.Text.Json;

namespace Campana.Core;

/// <summary>Reglas de negocio compartidas por todas las implementaciones de almacenamiento.</summary>
public static class CampaignRules
{
    public static void Validate(NewCampaign r)
    {
        if (string.IsNullOrWhiteSpace(r.Lugar)) throw DomainException.Validation("El lugar es obligatorio.");
        if (r.FechaFin < r.FechaInicio) throw DomainException.Validation("La fecha de fin no puede ser anterior a la de inicio.");
        EventPaths.ResolveZone(r.ZonaHoraria);
        if (r.Consultorios is null || r.Consultorios.Count == 0) throw DomainException.Validation("Debe haber al menos un consultorio.");
        var seen = new HashSet<string>();
        foreach (var c in r.Consultorios)
        {
            Ids.RequireConsultorioId(c.ConsultorioId);
            if (string.IsNullOrWhiteSpace(c.Nombre)) throw DomainException.Validation($"El consultorio {c.ConsultorioId} necesita un nombre.");
            if (!seen.Add(c.ConsultorioId)) throw DomainException.Validation($"Consultorio repetido: {c.ConsultorioId}.");
        }
        if (r.UnidadGeografica is { Clave: { } k } && k.Trim().Length > 0 && Normalizer.Slug(k).Length == 0)
            throw DomainException.Validation("La clave geográfica no es válida.");
    }

    public static GeoUnit NormalizeGeo(GeoUnit? geo, string lugar)
    {
        var clave = Normalizer.Slug(geo?.Clave);
        if (clave.Length == 0) clave = Normalizer.Slug(string.Join(' ', new[] { geo?.Estado, geo?.Municipio, geo?.Localidad }.Where(x => !string.IsNullOrWhiteSpace(x))));
        if (clave.Length == 0) clave = Normalizer.Slug(lugar);
        return new GeoUnit(geo?.Estado?.Trim(), geo?.Municipio?.Trim(), geo?.Localidad?.Trim(), clave);
    }

    public static Campaign Build(NewCampaign request, DateTimeOffset now, string createdBy) => new(
        Ids.BuildCampaignId(request.FechaInicio, request.Lugar), request.Lugar.Trim(), CampaignStates.Asignada, request.ZonaHoraria,
        request.FechaInicio, request.FechaFin, request.Consultorios,
        request.Formularios ?? new Dictionary<string, int> { ["mama"] = 1, ["papanicolaou"] = 1 },
        now, null, NormalizeGeo(request.UnidadGeografica, request.Lugar),
        new List<StateTransition> { new(CampaignStates.Asignada, now, createdBy) });

    /// <summary>Avanza una etapa. Falla si se intenta saltar o retroceder.</summary>
    public static Campaign Advance(Campaign c, string to, string by, DateTimeOffset now)
    {
        if (!CampaignStates.IsValid(to)) throw DomainException.Validation($"Etapa desconocida: '{to}'. Opciones: {string.Join(", ", CampaignStates.Sequence)}.");
        if (c.Estado == to) throw new DomainException(ErrorCode.CampaignState, $"La campaña ya está en la etapa '{to}'.");
        if (!CampaignStates.CanAdvance(c.Estado, to))
            throw new DomainException(ErrorCode.CampaignState, $"No se puede pasar de '{c.Estado}' a '{to}'. La siguiente etapa es '{Next(c.Estado)}'.");
        var transitions = new List<StateTransition>(c.Transiciones) { new(to, now, by) };
        return c with { Estado = to, ConcluidaEn = to == CampaignStates.Concluida ? now : c.ConcluidaEn, Transiciones = transitions };
    }

    public static string Next(string estado)
    {
        var i = Array.IndexOf(CampaignStates.Sequence, estado);
        return i >= 0 && i + 1 < CampaignStates.Sequence.Length ? CampaignStates.Sequence[i + 1] : "(ninguna)";
    }

    /// <summary>Error que corresponde cuando no se puede capturar en la campaña.</summary>
    public static DomainException NotWritable(Campaign c) => c.IsConcluded
        ? new DomainException(ErrorCode.CampaignClosed, $"La campaña '{c.CampaignId}' ya concluyó.")
        : new DomainException(ErrorCode.CampaignState, $"La campaña '{c.CampaignId}' está en la etapa '{c.Estado}'. La captura solo se permite en la etapa 'consulta'.");
}

public static class PatientRules
{
    public static void ValidateIdentity(PatientIdentity i, DateTimeOffset now)
    {
        var errors = new List<SchemaError>();
        if (string.IsNullOrWhiteSpace(i.Nombres) || i.Nombres.Length > 200) errors.Add(new SchemaError("/nombres", "Obligatorio, hasta 200 caracteres."));
        if (string.IsNullOrWhiteSpace(i.Apellidos) || i.Apellidos.Length > 200) errors.Add(new SchemaError("/apellidos", "Obligatorio, hasta 200 caracteres."));
        var limit = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        if (i.FechaNacimiento < new DateOnly(1900, 1, 1) || i.FechaNacimiento > limit) errors.Add(new SchemaError("/fechaNacimiento", "Fecha fuera de rango."));
        if (i.Documento?.Numero is { Length: > 64 }) errors.Add(new SchemaError("/documento/numero", "Hasta 64 caracteres."));
        if (i.Documento?.Tipo is { Length: > 64 }) errors.Add(new SchemaError("/documento/tipo", "Hasta 64 caracteres."));
        if (i.Contacto?.Telefono is { Length: > 64 }) errors.Add(new SchemaError("/contacto/telefono", "Hasta 64 caracteres."));
        if (i.Contacto?.Localidad is { Length: > 200 }) errors.Add(new SchemaError("/contacto/localidad", "Hasta 200 caracteres."));
        if (i.Contacto?.Celular is { Length: > 64 }) errors.Add(new SchemaError("/contacto/celular", "Hasta 64 caracteres."));
        if (i.Contacto?.Residencia is { Length: > 500 }) errors.Add(new SchemaError("/contacto/residencia", "Hasta 500 caracteres."));
        if (i.EntidadNacimiento is { Length: > 100 }) errors.Add(new SchemaError("/entidadNacimiento", "Hasta 100 caracteres."));
        if (errors.Count > 0) throw DomainException.Validation("Datos de la paciente inválidos.", errors);
    }

    public static PatientIdentityDocument BuildIdentity(string patientId, PatientIdentity i, string campaignId, DateTimeOffset now, string? createdBy = null)
        => new(1, patientId, i.Nombres.Trim(), i.Apellidos.Trim(), i.FechaNacimiento, i.Documento, i.Contacto, campaignId, now, i.EntidadNacimiento?.Trim(), createdBy);
}

public static class EventRules
{
    public static void ValidateShape(NewEvent evt, bool allowReserved)
    {
        Ids.RequireClientRequestId(evt.ClientRequestId);
        Ids.RequireTipo(evt.Tipo);
        Ids.RequirePatientId(evt.PatientId);
        Ids.RequireCampaignId(evt.CampaignId);
        Ids.RequireConsultorioId(evt.ConsultorioId);
        if (string.IsNullOrWhiteSpace(evt.CreatedBy)) throw DomainException.Validation("createdBy es obligatorio.");
        if (evt.Tipo == EventTypes.Adjunto && !allowReserved)
            throw DomainException.Validation("El tipo 'adjunto' solo se crea al subir un archivo adjunto.");
        if (evt.Payload.ValueKind != JsonValueKind.Object) throw DomainException.Validation("El payload debe ser un objeto JSON.");
        if (evt.VisitId is not null)
        {
            Ids.RequireEventId(evt.VisitId);
            if (evt.Tipo == EventTypes.Visita) throw DomainException.Validation("Una visita no puede pertenecer a otra visita.");
        }
    }

    public static string? CorrectsEventId(JsonElement payload)
        => payload.TryGetProperty("correctsEventId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

public static class PatientSearch
{
    public static List<PatientSummary> Filter(IEnumerable<PatientIndexEntry> entries, PatientQuery query, Func<string, string> resolveCanonical)
    {
        var tokens = Normalizer.Text(query.Texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var docToken = Normalizer.Document(query.Texto);
        var limit = Math.Clamp(query.Limit, 1, 200);
        var src = entries.Where(e => resolveCanonical(e.PatientId) == e.PatientId);
        if (query.CampaignId is not null) src = src.Where(e => e.Campanas.Contains(query.CampaignId));
        if (tokens.Length > 0)
            src = src.Where(e => tokens.All(t => e.NombreNormalizado.Contains(t, StringComparison.Ordinal))
                                 || (docToken.Length > 0 && e.DocumentoNormalizado is not null && e.DocumentoNormalizado.Contains(docToken, StringComparison.Ordinal))
                                 || (docToken.Length >= 7 && e.Telefonos is not null && e.Telefonos.Any(p => p.Contains(docToken, StringComparison.Ordinal))));
        return src.OrderBy(e => e.NombreNormalizado, StringComparer.Ordinal).Take(limit)
            .Select(e => new PatientSummary(e.PatientId, e.NombreVisible, e.FechaNacimiento, e.Campanas)).ToList();
    }

    /// <summary>Candidatas ordenadas por puntaje, una por expediente canónico.</summary>
    public static List<PatientCandidate> Candidates(IEnumerable<PatientIndexEntry> index, PatientIdentity identity,
        Func<string, string> resolveCanonical, Func<string, PatientIndexEntry?> lookup)
    {
        var seen = new HashSet<string>();
        var list = new List<PatientCandidate>();
        foreach (var m in PatientMatcher.FindCandidates(index, identity))
        {
            var canonical = resolveCanonical(m.Entry.PatientId);
            if (!seen.Add(canonical)) continue;
            var e = lookup(canonical) ?? m.Entry;
            list.Add(new PatientCandidate(canonical, e.NombreVisible, e.FechaNacimiento, string.Join(", ", m.Coincidencias), e.Campanas, m.Score, m.Coincidencias));
        }
        return list;
    }
}

public static class FileSignatures
{
    public sealed record Detected(string ContentType, string Extension);

    public static Detected? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 5 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46 && head[4] == 0x2D)
            return new Detected("application/pdf", "pdf");
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return new Detected("image/jpeg", "jpg");
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            return new Detected("image/png", "png");
        return null;
    }

    public static string? ContentTypeFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        "pdf" => "application/pdf",
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        _ => null,
    };
}

public static class AttachmentRules
{
    public static readonly System.Text.RegularExpressions.Regex Sha256Rx = new("^[0-9a-fA-F]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static void ValidateRequest(AttachmentRequest r)
    {
        Ids.RequirePatientId(r.PatientId);
        Ids.RequireCampaignId(r.CampaignId);
        Ids.RequireConsultorioId(r.ConsultorioId);
        Ids.RequireClientRequestId(r.ClientRequestId);
        if (r.VisitId is not null) Ids.RequireEventId(r.VisitId);
        if (r.Sha256Esperado is not null && !Sha256Rx.IsMatch(r.Sha256Esperado)) throw DomainException.Validation("sha256 inválido.");
    }

    public static void EnsureSize(long total, long max)
    {
        if (total > max) throw new DomainException(ErrorCode.FileTooLarge, $"El archivo supera el máximo de {max} bytes.");
    }

    /// <summary>El tipo se decide por la firma del archivo, no por lo que declare el cliente.</summary>
    public static FileSignatures.Detected CheckContent(ReadOnlySpan<byte> head, long total, AttachmentRequest r, ISet<string> allowed)
        => CheckContent(head, total, r.ContentTypeDeclarado, r.NombreOriginal, allowed);

    public static FileSignatures.Detected CheckContent(ReadOnlySpan<byte> head, long total, string? contentTypeDeclarado, string? nombreOriginal, ISet<string> allowed)
    {
        if (total == 0) throw DomainException.Validation("El archivo está vacío.");
        var detected = FileSignatures.Detect(head);
        if (detected is null || !allowed.Contains(detected.ContentType))
            throw new DomainException(ErrorCode.FileTypeNotAllowed, "Tipo de archivo no permitido.");
        if (!string.IsNullOrWhiteSpace(contentTypeDeclarado))
        {
            var declared = contentTypeDeclarado.Split(';')[0].Trim().ToLowerInvariant();
            if (declared == "image/jpg") declared = "image/jpeg";
            if (declared != "application/octet-stream" && declared != detected.ContentType)
                throw new DomainException(ErrorCode.FileTypeNotAllowed, "El tipo declarado no coincide con el contenido del archivo.");
        }
        var ext = Path.GetExtension(nombreOriginal ?? "").TrimStart('.');
        if (ext.Length > 0 && FileSignatures.ContentTypeFromExtension(ext) != detected.ContentType)
            throw new DomainException(ErrorCode.FileTypeNotAllowed, "La extensión no coincide con el contenido del archivo.");
        return detected;
    }

    public static void CheckHash(string? expected, string actual)
    {
        if (expected is not null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new DomainException(ErrorCode.IntegrityError, "El hash del archivo recibido no coincide con el esperado.");
    }

    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var baseName = name.Replace('\\', '/').Split('/').Last();
        var clean = new string(baseName.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > 255 ? clean[..255] : clean;
    }

    public static JsonElement BuildPayload(string attachmentId, string sha256, long size, string contentType, string? originalName, string rutaRelativa)
        => JsonSerializer.SerializeToElement(new { attachmentId, sha256, tamano = size, contentType, nombreOriginal = SanitizeName(originalName), rutaRelativa }, CampanaJson.Compact);
}
