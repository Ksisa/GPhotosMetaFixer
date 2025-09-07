namespace GPhotosMetaFixer.Options;

public class ApplicationOptions
{
    public string SourceFolder { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public bool KeepExistingTimestamp { get; set; } = true; // Default to A) keep as it was
    public bool DryRun { get; set; } = true; // Default to dry run for safety
    public bool Verbose { get; set; } = false;
    public string LogDirectory { get; set; } = "logs";
    public string? OutputFile { get; set; }
}
