namespace Campana.Core;

public sealed record MatchResult(PatientIndexEntry Entry, int Score, IReadOnlyList<string> Coincidencias);

/// <summary>
/// Busca expedientes que podrían ser la misma persona usando varios atributos.
/// Solo propone: vincular dos expedientes siempre lo confirma una persona.
/// </summary>
public static class PatientMatcher
{
    /// <summary>Puntaje mínimo para proponer una coincidencia.</summary>
    public const int Threshold = 60;

    public static List<MatchResult> FindCandidates(IEnumerable<PatientIndexEntry> index, PatientIdentity identity)
    {
        var doc = Normalizer.Document(identity.Documento?.Numero);
        var name = Normalizer.FullName(identity.Nombres, identity.Apellidos);
        var phones = Phones(identity.Contacto);
        var loc = Normalizer.Text(identity.Contacto?.Localidad);

        var results = new List<MatchResult>();
        foreach (var e in index)
        {
            var (score, why) = Score(e, doc, name, identity.FechaNacimiento, phones, loc);
            if (score >= Threshold) results.Add(new MatchResult(e, score, why));
        }
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Entry.NombreNormalizado, StringComparer.Ordinal).ToList();
    }

    public static (int Score, IReadOnlyList<string> Coincidencias) Score(PatientIndexEntry e, string doc, string name, DateOnly dob, IReadOnlyCollection<string> phones, string loc)
    {
        var why = new List<string>();
        var docMatch = doc.Length > 0 && e.DocumentoNormalizado == doc;
        var sim = name.Length > 0 ? NameSimilarity(name, e.NombreNormalizado) : 0;
        var nameExact = sim >= 0.999;
        var nameFuzzy = sim >= 0.85;
        var dobMatch = e.FechaNacimiento == dob;
        var phoneMatch = phones.Count > 0 && e.Telefonos is not null && e.Telefonos.Any(phones.Contains);
        var locMatch = loc.Length > 0 && e.LocalidadNormalizada == loc;
        var sameYear = e.FechaNacimiento.Year == dob.Year;

        if (docMatch) why.Add("documento");
        if (nameExact) why.Add("nombre"); else if (nameFuzzy) why.Add("nombre aproximado");
        if (dobMatch) why.Add("fecha de nacimiento");
        if (phoneMatch) why.Add("teléfono");
        if (locMatch) why.Add("localidad");

        int score;
        if (docMatch) score = 100;
        else if (nameExact && dobMatch) score = 95;
        else if (nameFuzzy && dobMatch) score = 85;
        else if (nameExact && phoneMatch) score = 85;
        else if (nameFuzzy && phoneMatch) score = 75;
        else if (dobMatch && phoneMatch) score = 70;
        else if (nameFuzzy && locMatch && sameYear) score = 65;
        else score = 0;
        return (score, why);
    }

    /// <summary>Teléfonos normalizados: solo dígitos, últimos 10.</summary>
    public static List<string> Phones(ContactoInfo? c)
    {
        var list = new List<string>();
        foreach (var raw in new[] { c?.Telefono, c?.Celular })
        {
            var d = new string((raw ?? "").Where(char.IsDigit).ToArray());
            if (d.Length >= 7) list.Add(d.Length > 10 ? d[^10..] : d);
        }
        return list.Distinct().ToList();
    }

    /// <summary>Similitud 0 a 1 entre dos nombres ya normalizados, sin importar el orden de las palabras.</summary>
    public static double NameSimilarity(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (ta.Length == 0 || tb.Length == 0) return 0;
        var sa = string.Join(' ', ta);
        var sb = string.Join(' ', tb);
        if (sa == sb) return 1.0;
        // "Ana López" contra "Ana López Núñez": un apellido de menos no debe esconder a la misma persona.
        var small = ta.Length <= tb.Length ? ta : tb;
        var big = ta.Length <= tb.Length ? tb : ta;
        if (small.Length >= 2 && small.All(big.Contains)) return 0.9;
        var max = Math.Max(sa.Length, sb.Length);
        return 1.0 - (double)Levenshtein(sa, sb) / max;
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

public static class PatientIndexing
{
    public static PatientIndexEntry ToEntry(PatientIdentityDocument i, List<string> campaigns)
    {
        var doc = Normalizer.Document(i.Documento?.Numero);
        var loc = Normalizer.Text(i.Contacto?.Localidad);
        return new PatientIndexEntry(
            i.PatientId,
            Normalizer.FullName(i.Nombres, i.Apellidos),
            $"{i.Nombres} {i.Apellidos}".Trim(),
            i.FechaNacimiento,
            doc.Length == 0 ? null : doc,
            campaigns,
            loc.Length == 0 ? null : loc,
            PatientMatcher.Phones(i.Contacto));
    }
}
