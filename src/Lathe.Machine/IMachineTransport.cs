namespace Lathe.Machine;

/// <summary>A byte/line pipe to a grbl-style controller (real serial port or the built-in simulator).</summary>
public interface IMachineTransport : IDisposable
{
    bool IsOpen { get; }
    void Open();
    void Close();

    /// <summary>Sends one line; "\n" is appended.</summary>
    void WriteLine(string line);

    /// <summary>Sends a single real-time byte (?, !, ~, 0x18, 0x85, overrides). Bypasses the line buffer.</summary>
    void WriteRealtime(byte b);

    /// <summary>Raised on a background thread for every received line (CR/LF stripped).</summary>
    event Action<string>? LineReceived;

    /// <summary>Raised when the link fails (cable unplugged, port error).</summary>
    event Action<Exception>? Faulted;
}
