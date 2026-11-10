using System.Threading.Channels;
using CodexGate.Data;

namespace CodexGate.Remote;

/// <summary>A tmux session that is alive on the host.</summary>
public sealed record RemoteLive(string Name, long CreatedAt, int Attached);

/// <summary>A live connection to one session's screen.</summary>
public interface ITerminalLink : IAsyncDisposable
{
    /// <summary>Bytes to draw. Completes when the session ends or the link drops.</summary>
    ChannelReader<byte[]> Output { get; }

    Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    Task ResizeAsync(int cols, int rows, CancellationToken ct);
}

/// <summary>
/// Runs Codex sessions on the server, each under the user's own system account so one teammate's
/// Codex can read neither another's files nor the gateway's database, keys or environment.
/// </summary>
public interface IRemoteHost
{
    /// <summary>
    /// Creates the user's account if needed and writes its Codex config: the gateway's API key mode, or
    /// (ownLogin) the ChatGPT relay with the user's own sign-in, which the config never touches.
    /// </summary>
    Task ProvisionAsync(long userId, string gatewayUrl, string model, bool ownLogin, CancellationToken ct);

    /// <summary>Starts the session with its own gateway key, readable only by the user's account.</summary>
    Task StartAsync(long userId, RemoteSessionRow session, int cols, int rows, int memoryMb, string key, CancellationToken ct);

    Task<IReadOnlyList<RemoteLive>> ListAsync(long userId, CancellationToken ct);

    Task KillAsync(long userId, string tmuxName, CancellationToken ct);

    Task<ITerminalLink> AttachAsync(long userId, string tmuxName, int cols, int rows, CancellationToken ct);
}

public sealed class RemoteHostException(string message) : Exception(message);
