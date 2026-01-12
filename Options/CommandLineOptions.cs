using CommandLine;

namespace GPhotosMetaFixer.Options;

public class ApplicationOptions
{
    [Option('s', "source", Required = true, HelpText = "Source folder path containing Google Photos takeout.")]
    public string SourceFolder { get; set; }

    [Option('d', "dest", Required = false, HelpText = "Destination folder for fixed files. Defaults to 'dst' folder next to source.")]
    public string? DestinationFolder { get; set; }

    [Option('r', "dry-run", Required = false, HelpText = "Perform a dry run. Use 'Y' for yes (default) or 'N' for no (real run).", Default = "Y")]
    public string? DryRunString { get; set; }

    [Option('l', "log", Required = false, HelpText = "Path to log file.")]
    public string? LogFile { get; set; }

    /// <summary>
    /// Gets the DryRun boolean value, parsing Y/N string
    /// </summary>
    public bool DryRun => DryRunString?.ToUpperInvariant() == "Y" || string.IsNullOrWhiteSpace(DryRunString);
}
