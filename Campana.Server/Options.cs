namespace Campana.Server;

public sealed class CampanaOptions
{
    public const string Section = "Campana";

    /// <summary>FileSystem (producción) o InMemory (pruebas y demostraciones).</summary>
    public string Storage { get; set; } = "FileSystem";
    public string DataRoot { get; set; } = "./data";
    public int Port { get; set; } = 8443;
    public long MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;
    public string[] AllowedAttachmentTypes { get; set; } = { "application/pdf", "image/jpeg", "image/png" };
    public long MinFreeDiskBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int ConsistencyIntervalMinutes { get; set; } = 5;
    public JwtOptions Jwt { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();
    /// <summary>Usuarios iniciales cuando el almacenamiento es InMemory.</summary>
    public List<UserConfig> Users { get; set; } = new();
    /// <summary>Contraseña del administrador inicial si todavía no existe _config/users.json. Si falta, se genera una y se muestra una sola vez.</summary>
    public string? AdminPassword { get; set; }
    public TrainingOptions Training { get; set; } = new();
    public FileShareOptions FileShare { get; set; } = new();
}

public sealed class TrainingOptions
{
    /// <summary>Carpeta con los audios del podcast: una subcarpeta por tema (por ejemplo CancerMama y Papanicolao). Por omisión {DataRoot}/capacitacion/audio.</summary>
    public string? AudioRoot { get; set; }
    /// <summary>Carpeta con las presentaciones: una subcarpeta por presentación, con deck.json y las diapositivas. Por omisión {DataRoot}/capacitacion/diapositivas.</summary>
    public string? SlidesRoot { get; set; }
}

public sealed class FileShareOptions
{
    /// <summary>Raíz del fileshare general (por ejemplo, el volumen montado). Vacío: la sincronización no está disponible.</summary>
    public string? Root { get; set; }
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "campana";
    public string Audience { get; set; } = "campana-clientes";
    public int ExpiresHours { get; set; } = 8;
    /// <summary>Mínimo 32 caracteres. Debe venir de una variable de entorno (Campana__Jwt__SigningKey), no del archivo.</summary>
    public string? SigningKey { get; set; }
}

public sealed class TlsOptions
{
    public bool Enabled { get; set; } = true;
    public string? CertificatePath { get; set; }
    /// <summary>Debe venir de una variable de entorno (Campana__Tls__CertificatePassword).</summary>
    public string? CertificatePassword { get; set; }
}

public sealed class UserConfig
{
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string? ConsultorioId { get; set; }
    public string PasswordHash { get; set; } = "";
    public List<string>? Campanas { get; set; }
    public bool Activo { get; set; } = true;
}

public sealed record UserRecord(string Username, string Role, string? ConsultorioId, string PasswordHash, List<string>? Campanas = null, bool Activo = true);
