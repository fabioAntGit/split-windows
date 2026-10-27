namespace Split;

/// <summary>
/// Office formats are converted by the apps that own them (Word, Excel, PowerPoint, driven by the platform) or, when
/// those are off or missing, by LibreOffice without a window. Each route writes into a scratch folder and the result
/// is moved to its final place only when complete.
/// </summary>
public static class OfficeBridge
{
    private static string ShortName(OfficeApp app) => app.ToString();

    private static string Extension(OfficeExport export) => export switch { OfficeExport.Pdf => "pdf", OfficeExport.Docx => "docx", _ => "xlsx" };

    /// <summary>Opens <paramref name="source"/> in <paramref name="app"/> and saves the result at <paramref name="destination"/>.</summary>
    private static void Export(OfficeApp app, string source, string destination, OfficeExport export)
    {
        var name = ShortName(app);
        if (!Platform.Current.IsOfficeInstalled(app)) throw new Problem(L("%@ isn’t installed.", name));
        var scratch = Output.ScratchDirectory();
        try
        {
            var staged = Path.Combine(scratch, "result." + Extension(export));
            Platform.Current.ExportWithOffice(app, source, staged, export);
            if (!File.Exists(staged))
            {
                throw new Problem(L("%@ opened the file but didn’t save the result.", name),
                                  L("Open “%@” in %@: if a warning appears (protected document, compatibility mode, external links), deal with it and try again.",
                                    Path.GetFileName(source), name));
            }
            var temp = Output.TempSibling(destination);
            File.Move(staged, temp);
            Output.Commit(temp, destination);
        }
        finally
        {
            Output.Discard(scratch);
        }
    }

    // MARK: LibreOffice

    /// <summary>The file converted by LibreOffice into <paramref name="directory"/>.</summary>
    public static string SofficeConvert(string path, string ext, string directory)
    {
        var soffice = Engines.Soffice ?? throw Explain.NoTargets([path], tools: false);
        var profile = Path.Combine(directory, "profile");
        var folder = Path.Combine(directory, "converted");
        Directory.CreateDirectory(folder);
        // Its own profile folder, so a LibreOffice the user already has open doesn't swallow the job.
        ProcessRunner.RunChecked(soffice, [$"-env:UserInstallation={new Uri(profile).AbsoluteUri}", "--headless", "--norestore",
                                           "--convert-to", ext, "--outdir", folder, path], L("Converting with LibreOffice"), app: "LibreOffice");
        var converted = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "." + ext);
        if (!File.Exists(converted))
        {
            throw new Problem(L("LibreOffice finished but didn’t create the %@.", ext.ToUpperInvariant()),
                              L("Quit LibreOffice if it is open (its windowless mode doesn’t work while it is open) and try again."));
        }
        return converted;
    }

    /// <summary>LibreOffice converts into a scratch folder; the result is renamed to <paramref name="destination"/>.</summary>
    private static string SofficeTo(string path, string ext, string destination)
    {
        var scratch = Output.ScratchDirectory();
        try
        {
            var temp = Output.TempSibling(destination);
            File.Move(SofficeConvert(path, ext, scratch), temp);
            Output.Commit(temp, destination);
        }
        finally
        {
            Output.Discard(scratch);
        }
        return destination;
    }

    /// <summary>What to say when a format can only be read by an app that is missing or switched off.</summary>
    private static Problem Unavailable(string path, Format format, OfficeApp app)
    {
        if (Platform.Current.IsOfficeInstalled(app))
        {
            return new Problem(L("Reading %@ needs %@ or LibreOffice, and Split isn’t set to use %@.", format.Label(), ShortName(app), ShortName(app)),
                               L("Turn on “Use Word, Excel and PowerPoint” in Settings, or install LibreOffice: winget install -e --id TheDocumentFoundation.LibreOffice"));
        }
        return app == OfficeApp.Word
            ? new Problem(L("Reading %@ needs Word or LibreOffice, and neither is installed.", format.Label()),
                          L("Install LibreOffice: winget install -e --id TheDocumentFoundation.LibreOffice"))
            : Explain.NoTargets([path], tools: false);
    }

    // MARK: Word documents

    public static bool CanUseWord => Settings.UseOfficeApps && Engines.Word;

    public static string WordToPdf(string path)
    {
        var output = Output.Url(path, "pdf");
        Export(OfficeApp.Word, path, output, OfficeExport.Pdf);
        return output;
    }

    /// <summary>An older Word file as DOCX inside <paramref name="directory"/>, which the document reader can open.</summary>
    public static string DocToDocx(string path, string directory)
    {
        if (CanUseWord)
        {
            var output = Path.Combine(directory, Output.BaseName(path) + ".docx");
            Export(OfficeApp.Word, path, output, OfficeExport.Docx);
            return output;
        }
        if (Engines.Soffice is null) throw Unavailable(path, Format.Doc, OfficeApp.Word);
        return SofficeConvert(path, "docx", directory);
    }

    // MARK: Spreadsheets

    public static bool CanUseExcel => Settings.UseOfficeApps && Engines.Excel;

    /// <summary>Formats the own XLSX reader can't parse become .xlsx first, inside <paramref name="directory"/>.</summary>
    public static string SpreadsheetToXlsx(string path, Format source, string directory)
    {
        if (CanUseExcel)
        {
            var output = Path.Combine(directory, Output.BaseName(path) + ".xlsx");
            Export(OfficeApp.Excel, path, output, OfficeExport.Xlsx);
            return output;
        }
        if (Engines.Soffice is null) throw Unavailable(path, source, OfficeApp.Excel);
        return SofficeConvert(path, "xlsx", directory);
    }

    public static string SpreadsheetToPdf(string path, Format source)
    {
        var output = Output.Url(path, "pdf");
        if (CanUseExcel)
        {
            Export(OfficeApp.Excel, path, output, OfficeExport.Pdf);
            return output;
        }
        if (Engines.Soffice is null) throw Unavailable(path, source, OfficeApp.Excel);
        return SofficeTo(path, "pdf", output);
    }

    // MARK: Presentations

    /// <summary>PowerPoint is used when it is installed and either the setting asks for Office apps or LibreOffice is not there to do it.</summary>
    private static bool UsePowerPoint(Format source) =>
        Engines.PowerPoint && source != Format.Odp && (Settings.UseOfficeApps || Engines.Soffice is null);

    public static IReadOnlyList<string> PresentationToPdf(string path, Format source)
    {
        var output = Output.Url(path, "pdf");
        if (UsePowerPoint(source)) Export(OfficeApp.PowerPoint, path, output, OfficeExport.Pdf);
        else if (Engines.Soffice is not null) SofficeTo(path, "pdf", output);
        else throw Explain.NoTargets([path], tools: false);
        return [output];
    }
}
