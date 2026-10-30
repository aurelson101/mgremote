// MGRemote serial console. GPL-2.0; see COPYING.txt.
using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
namespace mRemoteNG.UI.Controls;
internal sealed class SerialSession : IDisposable
{
    private readonly Func<byte[], int> _read;
    private readonly Action<byte[]> _write;
    private readonly Action _close;
    private readonly Channel<string> _input = Channel.CreateBounded<string>(128);
    private Task _reader = Task.CompletedTask, _writer = Task.CompletedTask;
    private int _stopped, _started, _disposed;
    private string _fault;
    public Task ShutdownCompletion { get; private set; } = Task.CompletedTask;
    public bool IsRunning => Volatile.Read(ref _stopped) == 0;
    internal SerialSession(Func<byte[], int> read, Action<byte[]> write, Action close) { _read = read; _write = write; _close = close; }
    internal static void Validate(string port, int baud, int bits)
    {
        if (!Regex.IsMatch(port ?? "", @"\ACOM[1-9][0-9]{0,3}\z", RegexOptions.IgnoreCase)) throw new ArgumentException("Port COM invalide (exemple : COM3).");
        if (baud < 50 || baud > 4000000 || bits < 5 || bits > 8) throw new ArgumentException("Débit ou nombre de bits invalide.");
    }
    internal static SerialSession Open(string name, int baud, int bits, Parity parity, StopBits stop, Handshake flow, bool dtr)
    {
        Validate(name, baud, bits);
        if (!Enum.IsDefined(parity) || stop is not (StopBits.One or StopBits.Two) || !Enum.IsDefined(flow)) throw new ArgumentException("Paramètres série invalides.");
        var port = new SerialPort(name, baud, parity, bits, stop) { Handshake = flow, DtrEnable = dtr, ReadTimeout = 250, WriteTimeout = 1000 };
        try { port.Open(); return new SerialSession(b => port.Read(b, 0, b.Length), b => port.Write(b, 0, b.Length), port.Dispose); }
        catch { port.Dispose(); throw; }
    }
    internal void Start(Func<string, Task> display)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || !IsRunning) throw new InvalidOperationException("Session série déjà démarrée ou fermée.");
        _reader = Task.Run(async () => {
            byte[] buffer = new byte[2048]; char[] chars = new char[2048]; var decoder = Encoding.UTF8.GetDecoder();
            try { while (IsRunning) {
                int size; try { size = _read(buffer); } catch (TimeoutException) { continue; }
                if (size == 0) break;
                int length = decoder.GetChars(buffer, 0, size, chars, 0);
                if (length > 0 && IsRunning) await display(new string(chars, 0, length)).ConfigureAwait(false);
            } } catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) {
                if (IsRunning) await display("\r\n[Port série déconnecté ou inaccessible.]\r\n").ConfigureAwait(false);
            } finally {
                if (_fault != null) await display(_fault).ConfigureAwait(false);
                Interlocked.Exchange(ref _stopped, 1); _input.Writer.TryComplete(); Dispose();
            }
        });
        _writer = Task.Run(async () => {
            try { await foreach (var value in _input.Reader.ReadAllAsync()) { if (!IsRunning) break; _write(Encoding.UTF8.GetBytes(value)); } }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) {
                _fault = "\r\n[Échec d’écriture série ; vérifiez le contrôle de flux.]\r\n";
                Interlocked.Exchange(ref _stopped, 1);
            }
        });
    }
    internal bool Send(string text) => IsRunning && text.Length <= 65536 && _input.Writer.TryWrite(text);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _stopped, 1); _input.Writer.TryComplete();
        ShutdownCompletion = Task.Run(async () => { try { await Task.WhenAll(_reader, _writer).ConfigureAwait(false); } finally { _close(); } });
    }
}
