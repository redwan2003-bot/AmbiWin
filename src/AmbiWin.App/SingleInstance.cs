using System.IO.Pipes;
using AmbiWin.Core.Diagnostics;

namespace AmbiWin.App;

/// <summary>
/// Keeps one AmbiWin per user session. A second launch asks the running instance to open its settings
/// (via a named pipe) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\AmbiWin.Instance";
    private const string PipeName = "AmbiWin.Activate";
    private const string ShowSettingsMessage = "show-settings";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Returns the instance guard, or null when another AmbiWin is already running (it was asked to show settings).</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);

        mutex.Dispose();
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(ShowSettingsMessage);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // The running instance is starting up or shutting down; nothing more to do.
        }
        return null;
    }

    /// <summary>Starts listening for later launches; <paramref name="onShowSettings"/> runs on a background thread.</summary>
    public void Listen(Action onShowSettings) => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) == ShowSettingsMessage)
                    onShowSettings();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                Log.Warn($"Activation pipe: {ex.Message}");
            }
        }
    });

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
