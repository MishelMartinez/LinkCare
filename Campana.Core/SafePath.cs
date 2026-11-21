namespace Campana.Core;

/// <summary>Construye rutas dentro de la raíz de datos y rechaza todo lo que intente salir de ella.</summary>
public sealed class SafePath
{
    public string Root { get; }
    private readonly string _rootWithSep;

    public SafePath(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("DataRoot vacío.", nameof(dataRoot));
        var full = Path.GetFullPath(dataRoot);
        Root = full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        _rootWithSep = Root + Path.DirectorySeparatorChar;
    }

    /// <summary>Valida un segmento de ruta (nombre de carpeta o archivo).</summary>
    public static void ValidateSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment)) throw DomainException.Validation("Segmento de ruta vacío.");
        if (segment.Length > 200) throw DomainException.Validation("Segmento de ruta demasiado largo.");
        if (segment is "." or "..") throw DomainException.Validation("Segmento de ruta inválido.");
        foreach (var ch in segment)
        {
            if (ch is '/' or '\\' or '\0' or ':' || char.IsControl(ch))
                throw DomainException.Validation("Segmento de ruta con caracteres no permitidos.");
        }
        if (segment.Contains("..", StringComparison.Ordinal)) throw DomainException.Validation("Segmento de ruta inválido.");
    }

    public string Resolve(params string[] segments)
    {
        foreach (var s in segments) ValidateSegment(s);
        var combined = Path.GetFullPath(Path.Combine(new[] { Root }.Concat(segments).ToArray()));
        EnsureInside(combined);
        return combined;
    }

    public void EnsureInside(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(_rootWithSep, StringComparison.Ordinal) && !string.Equals(full, Root, StringComparison.Ordinal))
            throw DomainException.Validation("La ruta queda fuera de la raíz de datos.");
    }

    /// <summary>Convierte una ruta relativa con '/' (como las que guardan los JSON) en una ruta completa segura.</summary>
    public string ResolveRelative(string baseDir, string relativeWithSlashes)
    {
        if (string.IsNullOrEmpty(relativeWithSlashes) || Path.IsPathRooted(relativeWithSlashes) || relativeWithSlashes.Contains('\\'))
            throw DomainException.Validation("Ruta relativa inválida.");
        var parts = relativeWithSlashes.Split('/');
        foreach (var p in parts) ValidateSegment(p);
        var full = Path.GetFullPath(Path.Combine(new[] { baseDir }.Concat(parts).ToArray()));
        EnsureInside(full);
        return full;
    }

    public string ToRelativeWithSlashes(string baseDir, string fullPath)
        => Path.GetRelativePath(baseDir, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
