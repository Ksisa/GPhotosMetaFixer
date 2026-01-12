using CommandLine;

namespace GPhotosMetaFixer.Options;

public class ApplicationOptions
{
    [Option('s', "source", Required = false, HelpText = "Source folder path containing Google Photos takeout.", Default = @"C:\Users\Kris\source\repos\GPhotosMetaFixer\tstsrc")]
    public string SourceFolder { get; set; }

    [Option('d', "dest", Required = false, HelpText = "Destination folder for fixed files. Defaults to 'dst' folder next to source.")]
    public string DestinationFolder { get; set; }

    [Option('r', "dry-run", Required = false, HelpText = "Perform a dry run without modifying any files.", Default = true)]
    public bool DryRun { get; set; }

    [Option('l', "log", Required = false, HelpText = "Path to log file.")]
    public string LogFile { get; set; }
}
