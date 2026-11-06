using System.Globalization;
using System.Text;

namespace CodexGate.Remote;

/// <summary>
/// The parts of tmux control mode (tmux -C) the web terminal needs: decoding "%output" lines and
/// turning typed bytes into send-keys commands. Pure functions, so they are tested without tmux.
/// </summary>
public static class TmuxProtocol
{
    private const int MaxKeysPerCommand = 256;

    /// <summary>Decodes a %output payload: tmux escapes bytes below 32 and the backslash as \ooo (octal).</summary>
    public static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        var n = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\\' && i + 3 < data.Length
                && IsOctal(data[i + 1]) && IsOctal(data[i + 2]) && IsOctal(data[i + 3]))
            {
                result[n++] = (byte)(((data[i + 1] - '0') << 6) | ((data[i + 2] - '0') << 3) | (data[i + 3] - '0'));
                i += 3;
            }
            else
            {
                result[n++] = data[i];
            }
        }
        return result.AsSpan(0, n).ToArray();
    }

    private static bool IsOctal(byte b) => b is >= (byte)'0' and <= (byte)'7';

    /// <summary>
    /// Splits a "%output %12 data" line into pane id and payload; false for other lines.
    /// </summary>
    public static bool TryParseOutput(ReadOnlySpan<byte> line, out string pane, out ReadOnlySpan<byte> payload)
    {
        pane = "";
        payload = default;
        var prefix = "%output "u8;
        if (!line.StartsWith(prefix))
        {
            return false;
        }
        var rest = line[prefix.Length..];
        var space = rest.IndexOf((byte)' ');
        if (space <= 0)
        {
            return false;
        }
        pane = Encoding.ASCII.GetString(rest[..space]);
        payload = rest[(space + 1)..];
        return true;
    }

    /// <summary>
    /// Commands that type the given bytes into a pane. ASCII (including control keys and escape
    /// sequences) goes as hex with send-keys -H, which tmux re-encodes for the pane's keyboard mode;
    /// non-ASCII text (Persian, emoji) goes literally with -l inside single quotes, which it can
    /// never break out of because a quote is ASCII.
    /// </summary>
    public static List<string> SendKeys(string pane, ReadOnlySpan<byte> input)
    {
        var commands = new List<string>();
        var hex = new List<string>();
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] < 0x80)
            {
                hex.Add(input[i].ToString("x2", CultureInfo.InvariantCulture));
                if (hex.Count == MaxKeysPerCommand)
                {
                    commands.Add($"send-keys -t {pane} -H {string.Join(' ', hex)}");
                    hex.Clear();
                }
                i++;
                continue;
            }
            if (hex.Count > 0)
            {
                commands.Add($"send-keys -t {pane} -H {string.Join(' ', hex)}");
                hex.Clear();
            }
            var start = i;
            while (i < input.Length && input[i] >= 0x80)
            {
                i++;
            }
            var text = Encoding.UTF8.GetString(input[start..i]);
            // Invalid UTF-8 decodes to U+FFFD; drop it rather than send something the user never typed.
            text = text.Replace("�", "", StringComparison.Ordinal);
            if (text.Length > 0)
            {
                commands.Add($"send-keys -t {pane} -l '{text}'");
            }
        }
        if (hex.Count > 0)
        {
            commands.Add($"send-keys -t {pane} -H {string.Join(' ', hex)}");
        }
        return commands;
    }

    /// <summary>What to draw first on attach: clear, the captured screen, then the cursor where tmux has it.</summary>
    public static byte[] InitialScreen(IReadOnlyList<string> lines, int cursorX, int cursorY)
    {
        var sb = new StringBuilder("\u001b[H\u001b[2J");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("\r\n");
            }
            sb.Append(lines[i]);
        }
        sb.Append(CultureInfo.InvariantCulture, $"\u001b[{cursorY + 1};{cursorX + 1}H");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
