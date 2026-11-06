namespace CodexGate.Security;

/// <summary>
/// Slows password guessing against one account from many addresses (the per-address limit in
/// Program.cs cannot see that). After too many failures the account refuses sign-in for a while.
/// Only existing accounts are tracked, so the table cannot be grown with made-up names.
/// </summary>
public sealed class LoginGuard(TimeProvider time)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Dictionary<long, (int Failures, DateTimeOffset Since)> _failures = [];
    private readonly Lock _lock = new();

    public bool IsLocked(long userId)
    {
        lock (_lock)
        {
            if (!_failures.TryGetValue(userId, out var f))
            {
                return false;
            }
            if (time.GetUtcNow() - f.Since > Window)
            {
                _failures.Remove(userId);
                return false;
            }
            return f.Failures >= MaxFailures;
        }
    }

    public void Failed(long userId)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            _failures[userId] = _failures.TryGetValue(userId, out var f) && now - f.Since <= Window
                ? (f.Failures + 1, f.Since)
                : (1, now);
        }
    }

    public void Succeeded(long userId)
    {
        lock (_lock)
        {
            _failures.Remove(userId);
        }
    }
}
