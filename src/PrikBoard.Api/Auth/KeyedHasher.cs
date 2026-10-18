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
    private const int MinKeyBytes = 32;

    /// <summary>Only for Development and Testing, where no real accounts live.</summary>
    private static readonly byte[] DevelopmentKey = Encoding.UTF8.GetBytes("prikboard-development-only-hash-key-0000");

    private readonly byte[] _subjectKey;
    private readonly byte[] _emailKey;

    public KeyedHasher(byte[] key)
    {
        if (key.Length < MinKeyBytes) throw new ArgumentException($"The hash key needs at least {MinKeyBytes} bytes.", nameof(key));
        _subjectKey = Derive(key, "prikboard/v1/subject");
        _emailKey = Derive(key, "prikboard/v1/email");
    }

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

    /// <summary>Reads <c>Auth:HashKey</c> (base64). Outside Development and Testing a missing or short key stops startup.</summary>
    public static KeyedHasher FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(configured);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"{ConfigKey} must be base64, for example the output of `openssl rand -base64 32`.");
            }
            if (key.Length < MinKeyBytes)
            {
                throw new InvalidOperationException($"{ConfigKey} must decode to at least {MinKeyBytes} bytes.");
            }
            return new KeyedHasher(key);
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) return new KeyedHasher(DevelopmentKey);

        throw new InvalidOperationException(
            $"{ConfigKey} is not set. Generate one with `openssl rand -base64 32` and keep it safe; see docs/authentication.md#before-deploying.");
    }

    private static byte[] Derive(byte[] key, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt: [], info: Encoding.UTF8.GetBytes(purpose));
}
