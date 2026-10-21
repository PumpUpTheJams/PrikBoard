using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace PrikBoard.Discord;

/// <summary>
/// Each connected server's PrikBoard token, encrypted at rest with Data Protection, in one JSON file under
/// <c>Storage:Path</c>. Keep the Data Protection keys (<c>DataProtection:KeysPath</c>) on another volume than the file
/// for the encryption to mean much: then a copy of one alone reveals no token.
/// </summary>
public sealed class GuildTokens
{
    private readonly IDataProtector _protector;
    private readonly string _file;
    private readonly Lock _writing = new();
    private readonly ConcurrentDictionary<string, string> _tokens;

    public GuildTokens(IDataProtectionProvider protection, string folder)
    {
        _protector = protection.CreateProtector("PrikBoard.Discord.GuildTokens");
        _file = Path.Combine(folder, "guild-tokens.json");
        var stored = File.Exists(_file)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? []
            : [];
        _tokens = new ConcurrentDictionary<string, string>(stored);
    }

    /// <summary>The server's token, or null when it isn't connected.</summary>
    public string? Get(ulong guildId) => _tokens.TryGetValue(guildId.ToString(), out var sealedToken) ? _protector.Unprotect(sealedToken) : null;

    public void Set(ulong guildId, string token)
    {
        _tokens[guildId.ToString()] = _protector.Protect(token);
        Save();
    }

    public void Remove(ulong guildId)
    {
        if (_tokens.TryRemove(guildId.ToString(), out _)) Save();
    }

    private void Save()
    {
        lock (_writing)
        {
            // Written next to the file and moved over it, so a crash never leaves half a file.
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_tokens.ToDictionary()));
            File.Move(temp, _file, overwrite: true);
        }
    }
}
