using System.Security.Cryptography;
using System.Text;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Turns identifiers that would be personal data (a provider's subject id, an email address) into keyed hashes, so the
/// database can match them without holding them. Each purpose uses its own key, derived from <c>Auth:HashKey</c>.
/// Losing or changing that key makes every stored hash useless: people would come back as new, empty users.
/// </summary>
public sealed class KeyedHasher
{
    public const string ConfigKey = "Auth:HashKey";

    /// <summary>The file in the data folder (<c>Storage:Path</c>) that holds a generated key when none is configured.</summary>
    public const string KeyFileName = "hash-key";

    private const int MinKeyBytes = 32;

    /// <summary>Only for Development and Testing, where no real accounts live.</summary>
    private static readonly byte[] DevelopmentKey = Encoding.UTF8.GetBytes("prikboard-development-only-hash-key-0000");

    private readonly byte[] _subjectKey;
    private readonly byte[] _emailKey;

    public KeyedHasher(byte[] key, HashKeySource source = HashKeySource.Configuration)
    {
        if (key.Length < MinKeyBytes) throw new ArgumentException($"The hash key needs at least {MinKeyBytes} bytes.", nameof(key));
        _subjectKey = Derive(key, "prikboard/v1/subject");
        _emailKey = Derive(key, "prikboard/v1/email");
        Key = key;
        Source = source;
    }

    /// <summary>The key itself, for the recovery kit.</summary>
    public byte[] Key { get; }

    /// <summary>Where the key came from.</summary>
    public HashKeySource Source { get; }

    /// <summary>Identifies a provider identity. The provider is part of the input, so equal ids at two providers differ.</summary>
    public byte[] Subject(string provider, string subject) =>
        HMACSHA256.HashData(_subjectKey, Encoding.UTF8.GetBytes($"{provider}\n{subject}"));

    /// <summary>Matches an exact email address, ignoring case and surrounding spaces.</summary>
    public byte[] Email(string email) =>
        HMACSHA256.HashData(_emailKey, Encoding.UTF8.GetBytes(email.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant()));

    /// <summary>
    /// Invite tokens are 256 random bits, so an unkeyed hash is enough to keep the stored value useless on its own,
    /// and invites keep working if the hash key ever changes.
    /// </summary>
    public static byte[] InviteToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// Reads <c>Auth:HashKey</c> (base64). Without it, Development and Testing use a fixed key, and everywhere else the key
    /// comes from <see cref="KeyFileName"/> in the data folder, generated there on the first start.
    /// </summary>
    public static KeyedHasher FromConfiguration(IConfiguration configuration, IHostEnvironment environment, string dataFolder, ILogger logger)
    {
        var file = Path.Combine(dataFolder, KeyFileName);
        var configured = configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var key = Parse(configured, ConfigKey);
            if (File.Exists(file) && File.ReadAllText(file).Trim() != configured.Trim())
            {
                logger.LogWarning("{File} holds a different hash key than {ConfigKey}; using {ConfigKey}", file, ConfigKey, ConfigKey);
            }
            return new KeyedHasher(key);
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) return new KeyedHasher(DevelopmentKey, HashKeySource.Development);

        if (File.Exists(file)) return new KeyedHasher(Parse(File.ReadAllText(file), file), HashKeySource.File);

        var generated = RandomNumberGenerator.GetBytes(MinKeyBytes);
        // CreateNew, so two instances starting on one volume can't each write their own key.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(file, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(Convert.ToBase64String(generated));
        }
        logger.LogWarning(
            "Generated a hash key in {File}. Back it up with the database: without it nobody can sign in to their account again",
            file);
        return new KeyedHasher(generated, HashKeySource.File);
    }

    private static byte[] Parse(string base64, string source)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{source} must be base64, for example the output of `openssl rand -base64 32`.");
        }
        if (key.Length < MinKeyBytes)
        {
            throw new InvalidOperationException($"{source} must decode to at least {MinKeyBytes} bytes.");
        }
        return key;
    }

    private static byte[] Derive(byte[] key, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt: [], info: Encoding.UTF8.GetBytes(purpose));
}

/// <summary>Where <see cref="KeyedHasher"/>'s key came from.</summary>
public enum HashKeySource
{
    /// <summary><c>Auth:HashKey</c>.</summary>
    Configuration,

    /// <summary>Generated into the data folder on the first start.</summary>
    File,

    /// <summary>The fixed key of Development and Testing.</summary>
    Development,
}
