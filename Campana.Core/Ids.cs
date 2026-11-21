using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Campana.Core;

public static class Ids
{
    private static readonly Regex PatientRx = new(@"^pat-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled);
    private static readonly Regex CampaignRx = new(@"^camp-\d{4}-\d{2}-[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex ConsultorioRx = new(@"^c\d{2,3}$", RegexOptions.Compiled);
    private static readonly Regex AttachmentRx = new(@"^att-[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex DocumentRx = new(@"^doc-[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex EventRx = new(@"^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex RequestRx = new(@"^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled);
    private static readonly Regex TipoRx = new(@"^[a-z][a-z0-9-]{1,30}$", RegexOptions.Compiled);

    public const int MaxCampaignIdLength = 80;

    public static string NewPatientId() => "pat-" + Guid.NewGuid().ToString("D");
    public static string NewEventId() => Guid.NewGuid().ToString("N");
    public static string NewAttachmentId() => "att-" + Guid.NewGuid().ToString("N");
    public static string NewDocumentId() => "doc-" + Guid.NewGuid().ToString("N");
    public static bool IsDocumentId(string? v) => v is not null && DocumentRx.IsMatch(v);
    public static string RequireDocumentId(string? v) => IsDocumentId(v) ? v! : throw DomainException.Validation("documentId inválido.");

    public static bool IsPatientId(string? v) => v is not null && PatientRx.IsMatch(v);
    public static bool IsCampaignId(string? v) => v is not null && v.Length <= MaxCampaignIdLength && CampaignRx.IsMatch(v);
    public static bool IsConsultorioId(string? v) => v is not null && ConsultorioRx.IsMatch(v);
    public static bool IsAttachmentId(string? v) => v is not null && AttachmentRx.IsMatch(v);
    public static bool IsEventId(string? v) => v is not null && EventRx.IsMatch(v);
    public static bool IsClientRequestId(string? v) => v is not null && RequestRx.IsMatch(v);
    public static bool IsTipo(string? v) => v is not null && TipoRx.IsMatch(v);

    public static string RequirePatientId(string? v) => IsPatientId(v) ? v! : throw DomainException.Validation("patientId inválido.");
    public static string RequireCampaignId(string? v) => IsCampaignId(v) ? v! : throw DomainException.Validation("campaignId inválido.");
    public static string RequireConsultorioId(string? v) => IsConsultorioId(v) ? v! : throw DomainException.Validation("consultorioId inválido.");
    public static string RequireAttachmentId(string? v) => IsAttachmentId(v) ? v! : throw DomainException.Validation("attachmentId inválido.");
    public static string RequireEventId(string? v) => IsEventId(v) ? v! : throw DomainException.Validation("eventId inválido.");
    public static string RequireClientRequestId(string? v) => IsClientRequestId(v) ? v! : throw DomainException.Validation("clientRequestId inválido (8 a 64 caracteres: letras, números, '-' o '_').");
    public static string RequireTipo(string? v) => IsTipo(v) ? v! : throw DomainException.Validation("tipo inválido.");

    /// <summary>camp-{año}-{mes}-{lugar-normalizado}</summary>
    public static string BuildCampaignId(DateOnly fechaInicio, string lugar)
    {
        var slug = Normalizer.Slug(lugar);
        if (slug.Length == 0) throw DomainException.Validation("El lugar de la campaña no puede quedar vacío.");
        var id = string.Create(CultureInfo.InvariantCulture, $"camp-{fechaInicio.Year:D4}-{fechaInicio.Month:D2}-{slug}");
        if (id.Length > MaxCampaignIdLength) id = id[..MaxCampaignIdLength].TrimEnd('-');
        return id;
    }
}

public static class Normalizer
{
    /// <summary>Minúsculas, sin acentos, espacios colapsados.</summary>
    public static string Text(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        var lastSpace = false;
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Solo letras y números, en minúsculas.</summary>
    public static string Document(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = Text(s);
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>a-z0-9 separados por un solo guion.</summary>
    public static string Slug(string? s)
    {
        var t = Text(s);
        var sb = new StringBuilder(t.Length);
        var pendingDash = false;
        foreach (var ch in t)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                sb.Append(ch);
                pendingDash = false;
            }
            else pendingDash = true;
        }
        return sb.ToString();
    }

    public static string FullName(string nombres, string apellidos) => Text($"{nombres} {apellidos}");
}
