namespace Campana.Core;

public enum ErrorCode
{
    ValidationError,
    NotFound,
    Conflict,
    CampaignConflict,
    CampaignClosed,
    /// <summary>La campaña no está en la etapa que exige la operación (por ejemplo, capturar fuera de la etapa de consulta).</summary>
    CampaignState,
    /// <summary>Dos personas cambiaron el mismo dato de una paciente. No se resuelve solo: lo decide una persona.</summary>
    IdentityConflict,
    DuplicateCandidates,
    FileTooLarge,
    FileTypeNotAllowed,
    IntegrityError,
    InsufficientStorage,
    Forbidden,
}

/// <summary>Error de dominio que la API traduce a un ProblemDetails.</summary>
public class DomainException : Exception
{
    public ErrorCode Code { get; }
    public object? Details { get; }

    public DomainException(ErrorCode code, string message, object? details = null) : base(message)
    {
        Code = code;
        Details = details;
    }

    public static DomainException Validation(string message, object? details = null) => new(ErrorCode.ValidationError, message, details);
    public static DomainException NotFound(string message) => new(ErrorCode.NotFound, message);
    public static DomainException Conflict(string message) => new(ErrorCode.Conflict, message);
}
