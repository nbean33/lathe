using System.IO.Ports;
using System.Text;

namespace Lathe.Machine;

/// <summary>USB/serial link to a grblHAL board.</summary>
public sealed class SerialTransport : IMachineTransport
{
    readonly string _portName;
    readonly int _baud;
    readonly object _writeLock = new();
    SerialPort? _port;
    Thread? _reader;
    volatile bool _open;

    public SerialTransport(string portName, int baud = 115200)
    {
        _portName = portName;
        _baud = baud;
    }

    public static string[] ListPorts()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return []; }
    }

    public bool IsOpen => _open && _port?.IsOpen == true;

    public event Action<string>? LineReceived;
    public event Action<Exception>? Faulted;

    public void Open()
    {
        if (IsOpen) return;
        var port = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.Latin1,
            NewLine = "\n",
            DtrEnable = true,     // many USB CDC stacks only send data when DTR is asserted
            RtsEnable = true,
            ReadTimeout = 250,
            WriteTimeout = 1000,
        };
        port.Open();
        _port = port;
        _open = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"serial-{_portName}" };
        _reader.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();
        while (_open)
        {
            int n;
            try { n = _port!.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch (Exception ex)
            {
                if (_open) { _open = false; Faulted?.Invoke(ex); }
                return;
            }
            for (int i = 0; i < n; i++)
            {
                char c = (char)buf[i];
                if (c == '\n')
                {
                    var line = sb.ToString();
                    sb.Clear();
                    if (line.Length > 0)
                    {
                        try { LineReceived?.Invoke(line); } catch { /* handler bugs must not kill the reader */ }
                    }
                }
                else if (c != '\r') sb.Append(c);
            }
            if (sb.Length > 8192) sb.Clear(); // garbage on the line (wrong baud) – don't grow forever
        }
    }

    public void WriteLine(string line) => Write(Encoding.ASCII.GetBytes(line + "\n"));

    public void WriteRealtime(byte b) => Write([b]);

    void Write(byte[] data)
    {
        lock (_writeLock)
        {
            if (!IsOpen) throw new InvalidOperationException("Serial port is not open.");
            try { _port!.Write(data, 0, data.Length); }
            catch (Exception ex)
            {
                _open = false;
                Faulted?.Invoke(ex);
                throw new IOException($"Write to {_portName} failed: {ex.Message}", ex);
            }
        }
    }

    public void Close()
    {
        _open = false;
        try { _port?.Close(); } catch { }
        var r = _reader;
        if (r != null && r != Thread.CurrentThread) r.Join(1000);
        _reader = null;
        _port?.Dispose();
        _port = null;
    }

    public void Dispose() => Close();
}
