using System.Security.Cryptography;
using System.Text.Json;

public sealed record UserAccount(string Name, string Role, string Hash);

public sealed class UserStore
{
    public static readonly string[] Roles = ["viewer", "reviewer", "admin"];
    private const int Iterations = 600_000;
    private static readonly Lazy<string> Dummy = new(() => Hash("not-a-real-password"));
    private readonly Dictionary<string, UserAccount> users = new(StringComparer.OrdinalIgnoreCase);

    public UserStore(string path)
    {
        if (!File.Exists(path)) return;
        List<UserAccount> list;
        try { list = JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException($"{path} is not valid JSON ({ex.Message}). Fix or remove it.", ex); }
        if (list.Count == 0) throw new InvalidOperationException($"{path} has no users. Add one, or remove the file.");
        foreach (var user in list)
        {
            var name = user.Name?.Trim();
            if (string.IsNullOrEmpty(name) || !Roles.Contains(user.Role) || !ValidHash(user.Hash))
                throw new InvalidOperationException($"{path}: user '{user.Name}' needs a name, a role (viewer, reviewer or admin) and a hash made by 'hash-password'.");
            if (!users.TryAdd(name, user with { Name = name }))
                throw new InvalidOperationException($"{path}: user '{name}' appears twice.");
        }
    }

    public static string DefaultPath(string root) =>
        Environment.GetEnvironmentVariable("CCTV_USERS_FILE") is { Length: > 0 } path ? path : Path.Combine(root, "users.json");

    public bool Enabled => users.Count > 0;

    public UserAccount? Find(string? name) => name is not null && users.TryGetValue(name.Trim(), out var user) ? user : null;

    public UserAccount? Check(string? name, string? password)
    {
        var account = Find(name);
        var ok = Verify(account?.Hash ?? Dummy.Value, password);
        return ok && account is not null ? account : null;
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    private static bool ValidHash(string? hash) => hash?.Split('$') is ["pbkdf2-sha256", var n, _, _] && int.TryParse(n, out var iterations) && iterations > 0;

    public static bool Verify(string? hash, string? password)
    {
        if (password is null || hash?.Split('$') is not ["pbkdf2-sha256", var n, var salt, var key] || !int.TryParse(n, out var iterations) || iterations <= 0) return false;
        try
        {
            var expected = Convert.FromBase64String(key);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}