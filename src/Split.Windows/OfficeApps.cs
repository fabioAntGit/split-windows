using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Split.Windows;

/// <summary>
/// Word, Excel and PowerPoint driven through COM, late bound, on one thread of their own (COM objects live in the apartment
/// that made them). Documents open read-only with macros off and nothing added to the recent files. An app the user already had
/// open is used as it is and left open; an app this job started is closed by <see cref="QuitLaunched"/>.
/// </summary>
internal static class OfficeApps
{
    private static readonly string[] ProgIds = ["Word.Application", "Excel.Application", "PowerPoint.Application"];

    /// <summary>The apps this program started, still running. Only the Office thread touches it.</summary>
    private static readonly Dictionary<OfficeApp, object> Launched = [];

    private static readonly BlockingCollection<Action> Queue = [];
    private static readonly object Gate = new();
    private static Thread? thread;

    private static void OnOfficeThread(Action work, bool cancellable = true)
    {
        lock (Gate)
        {
            thread ??= StartThread();
        }
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        Queue.Add(() =>
        {
            try { work(); }
            catch (Exception e) { failure = e; }
            finally { done.Set(); }
        });
        // A job cancelled while the app works returns at once; the app finishes its file in the background.
        while (!done.Wait(200))
            if (cancellable && JobProgress.IsCancelled) throw Problem.Cancelled;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Thread StartThread()
    {
        var office = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "Office" };
        office.SetApartmentState(ApartmentState.STA);
        office.Start();
        return office;
    }

    // MARK: Running apps

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    // Marshal.GetActiveObject no longer exists: the running-object table is asked directly.
    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    private static object? Running(OfficeApp app)
    {
        if (CLSIDFromProgID(ProgIds[(int)app], out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var instance) == 0 ? instance : null;
    }

    // MARK: Export

    public static void Export(OfficeApp app, string source, string output, OfficeExport export)
    {
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        var name = app.ToString();
        OnOfficeThread(() =>
        {
            try { Run(app, source, output, export); }
            catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
            {
                var details = $"{e.Message} 0x{e.HResult:X8}";
                // An app that refuses a file downloaded from the Internet is usually protecting the user; say so.
                if (HasWebMark(source)) details += " (Protected View: the file has the mark of the web)";
                throw Explain.ToolFailure(L("Exporting with %@", name), name, details, 1, name);
            }
        });
    }

    private static bool HasWebMark(string path)
    {
        try { return Regex.IsMatch(File.ReadAllText(path + ":Zone.Identifier"), @"ZoneId\s*=\s*[34]"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private static void Run(OfficeApp app, string source, string output, OfficeExport export)
    {
        dynamic application;
        var started = Launched.TryGetValue(app, out var held);
        if (started) application = held!;
        else if (Running(app) is { } running) application = running;
        else
        {
            application = Activator.CreateInstance(Type.GetTypeFromProgID(ProgIds[(int)app], throwOnError: true)!)!;
            Launched[app] = application;
            started = true;
        }
        // The user's own session gets its settings back; an app we started is only ours.
        object? alerts = null, security = null;
        try
        {
            if (!started)
            {
                alerts = app == OfficeApp.PowerPoint ? null : application.DisplayAlerts;
                security = application.AutomationSecurity;
            }
            if (app != OfficeApp.PowerPoint)
            {
                if (started) application.Visible = false;
                if (app == OfficeApp.Word) application.DisplayAlerts = 0;
                else application.DisplayAlerts = false;
            }
            // msoAutomationSecurityForceDisable: no macro runs, whatever the file holds.
            application.AutomationSecurity = 3;
            switch (app)
            {
                case OfficeApp.Word: Word(application, source, output, export); break;
                case OfficeApp.Excel: Excel(application, source, output, export); break;
                default: PowerPoint(application, source, output); break;
            }
        }
        catch (COMException) when (started && Launched.ContainsKey(app) && !IsAlive(application))
        {
            // The user closed the app we had started: forget it, and the next file starts a new one.
            Launched.Remove(app);
            throw;
        }
        finally
        {
            if (!started)
            {
                try
                {
                    if (alerts is not null) application.DisplayAlerts = alerts;
                    if (security is not null) application.AutomationSecurity = security;
                }
                catch (COMException) { }
                Marshal.FinalReleaseComObject(application);
            }
        }
    }

    private static bool IsAlive(dynamic application)
    {
        try
        {
            _ = application.Name;
            return true;
        }
        catch (COMException) { return false; }
    }

    private static void Word(dynamic word, string source, string output, OfficeExport export)
    {
        dynamic documents = word.Documents;
        dynamic document = documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: false);
        try
        {
            if (export == OfficeExport.Pdf) document.ExportAsFixedFormat(output, 17);
            else document.SaveAs2(FileName: output, FileFormat: 16);
        }
        finally
        {
            document.Close(false);
            Marshal.FinalReleaseComObject(document);
            Marshal.FinalReleaseComObject(documents);
        }
    }

    private static void Excel(dynamic excel, string source, string output, OfficeExport export)
    {
        dynamic books = excel.Workbooks;
        dynamic book = books.Open(Filename: source, UpdateLinks: 0, ReadOnly: true);
        try
        {
            if (export == OfficeExport.Pdf) book.ExportAsFixedFormat(0, output);
            else book.SaveAs(Filename: output, FileFormat: 51);
        }
        finally
        {
            book.Close(false);
            Marshal.FinalReleaseComObject(book);
            Marshal.FinalReleaseComObject(books);
        }
    }

    private static void PowerPoint(dynamic powerPoint, string source, string output)
    {
        dynamic presentations = powerPoint.Presentations;
        // Read-only, not untitled, without a window.
        dynamic presentation = presentations.Open(source, -1, 0, 0);
        try { presentation.SaveAs(output, 32); }
        finally
        {
            presentation.Close();
            Marshal.FinalReleaseComObject(presentation);
            Marshal.FinalReleaseComObject(presentations);
        }
    }

    // MARK: Closing

    public static void QuitLaunched()
    {
        lock (Gate)
        {
            if (thread is null) return;
        }
        OnOfficeThread(() =>
        {
            foreach (var (app, instance) in Launched.ToList())
            {
                dynamic application = instance;
                try
                {
                    if (app == OfficeApp.Word) application.Quit(false);
                    else application.Quit();
                }
                catch (COMException) { }
                Marshal.FinalReleaseComObject(instance);
            }
            Launched.Clear();
        }, cancellable: false);
    }
}
