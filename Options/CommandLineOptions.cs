namespace GPhotosMetaFixer.Options;

public class ApplicationOptions
{
    public string SourceFolder { get; set; }
    public string DestinationFolder { get; set; }
    public bool DryRun { get; set; }
    public string LogFile { get; set; }
}
