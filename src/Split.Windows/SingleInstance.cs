using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Split.Windows;

/// <summary>
/// One Split per user. The first to start owns the mutex and listens on a pipe only this user can open;
/// a second start sends its files through the pipe and quits. The Explorer may start the program once
/// per selected file, so arrivals within <see cref="Window"/> are handed over together.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);

    private readonly string pipeName;
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly List<string> arrived = [];
    private Timer? pending;
    private Action<IReadOnlyList<string>>? received;

    /// <param name="name">Tests pass their own, so they never meet a running Split.</param>
    public SingleInstance(string name = "Split")
    {
        pipeName = $"{name}.{WindowsIdentity.GetCurrent().User?.Value}";
        mutex = new Mutex(true, @"Local\" + name, out var created);
        IsPrimary = created;
    }

    public bool IsPrimary { get; }

    /// <summary>Starts taking files from later starts; <paramref name="onReceived"/> runs on a worker thread (an empty list is a start without files).</summary>
    public void Listen(Action<IReadOnlyList<string>> onReceived)
    {
        received = onReceived;
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte,
                                                                       PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var paths = new List<string>();
                while (await reader.ReadLineAsync(stop.Token) is { } line) if (line.Length > 0) paths.Add(line);
                Arrived(paths);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
        }
    }

    private void Arrived(List<string> paths)
    {
        lock (gate)
        {
            arrived.AddRange(paths);
            pending ??= new Timer(_ =>
            {
                List<string> batch;
                lock (gate) { batch = [.. arrived.Distinct(StringComparer.OrdinalIgnoreCase)]; arrived.Clear(); pending?.Dispose(); pending = null; }
                received?.Invoke(batch);
            }, null, Window, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Hands the files to the Split that is running. False when none answers.</summary>
    public static bool Send(IReadOnlyList<string> paths, string name = "Split")
    {
        try
        {
            using var client = new NamedPipeClientStream(".", $"{name}.{WindowsIdentity.GetCurrent().User?.Value}", PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var path in paths) writer.WriteLine(Path.GetFullPath(path));
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException) { return false; }
    }

    public void Dispose()
    {
        stop.Cancel();
        lock (gate) { pending?.Dispose(); pending = null; }
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
