using CodexGate.Data;
using CodexGate.Security;
using Microsoft.Extensions.Options;

namespace CodexGate;

public static class Bootstrap
{
    public const string InitialPasswordFile = "initial-admin-password.txt";

    /// <summary>
    /// Creates the first admin while there is none. The password comes from Gate:AdminPassword, or is
    /// generated and written to a file in the data folder; it is never written to the log.
    /// </summary>
    public static async Task EnsureAdminAsync(IServiceProvider services)
    {
        var users = services.GetRequiredService<UserStore>();
        var settings = services.GetRequiredService<SettingsStore>();
        if (!(await settings.GetAllAsync()).ContainsKey(SettingsStore.InstanceSecret))
        {
            await settings.SetAsync(SettingsStore.InstanceSecret, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        }
        if (await users.CountAdminsAsync(enabledOnly: true) > 0)
        {
            return;
        }
        var options = services.GetRequiredService<IOptions<GateOptions>>().Value;
        if (options.Managed.Enabled && string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            return; // installed with install.sh: the setup page creates the admin
        }
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("CodexGate.Bootstrap");
        var time = services.GetRequiredService<TimeProvider>();

        var username = Validation.Username(options.AdminUsername) ?? "admin";
        var password = options.AdminPassword;
        var generated = string.IsNullOrWhiteSpace(password);

        // No enabled admin but the account exists: restore it, only from a password the operator set.
        var existing = await users.GetByUsernameAsync(username);
        if (existing is not null)
        {
            if (generated)
            {
                log.LogWarning("No enabled admin. Set Gate:AdminPassword and restart to restore \"{User}\" as admin.", username);
                return;
            }
            existing.Role = Roles.Admin;
            existing.Enabled = true;
            existing.PasswordHash = PasswordHasher.Hash(password!);
            existing.SecurityStamp = ApiKeys.NewStamp();
            await users.UpdateAsync(existing);
            log.LogWarning("No enabled admin was left; restored \"{User}\" as admin with Gate:AdminPassword", username);
            return;
        }
        if (generated)
        {
            password = PasswordHasher.Generate(16);
        }

        await users.InsertAsync(new UserRow
        {
            Username = username,
            DisplayName = "Admin",
            PasswordHash = PasswordHasher.Hash(password!),
            Role = Roles.Admin,
            Enabled = true,
            SecurityStamp = ApiKeys.NewStamp(),
            CreatedAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        if (generated)
        {
            var file = Path.Combine(DataDir(options, services), InitialPasswordFile);
            File.Delete(file);
            var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // never readable by others, not even briefly
            }
            await using (var writer = new StreamWriter(new FileStream(file, create)))
            {
                await writer.WriteAsync($"username: {username}{Environment.NewLine}password: {password}{Environment.NewLine}");
            }
            log.LogWarning("Created admin \"{User}\"; its password is in {File}. Delete the file after signing in.", username, file);
        }
        else
        {
            log.LogInformation("Created admin \"{User}\" from Gate:AdminPassword", username);
        }
    }

    /// <summary>The data folder; Program makes it absolute against the content root at startup.</summary>
    public static string DataDirOf(GateOptions options)
    {
        var dir = Path.GetFullPath(options.DataDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DataDir(GateOptions options, IServiceProvider services)
    {
        var env = services.GetRequiredService<IWebHostEnvironment>();
        var dir = Path.GetFullPath(options.DataDir, env.ContentRootPath);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class Validation
{
    /// <summary>3–32 characters: letters, digits, dot, dash, underscore. Returns the trimmed name or null.</summary>
    public static string? Username(string? value)
    {
        var v = value?.Trim() ?? "";
        return v.Length is >= 3 and <= 32 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? v : null;
    }

    public static bool Password(string? value) => value is { Length: >= 8 and <= 200 };
}
