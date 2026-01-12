using CommandLine;
using GPhotosMetaFixer.Options;
using GPhotosMetaFixer.Services;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer;

internal class Program
{
    static void Main(string[] args)
    {
        var logger = new AppLogger(nameof(Program));

        Parser.Default.ParseArguments<ApplicationOptions>(args)
            .WithParsed(options => Run(options, logger))
            .WithNotParsed(errors => HandleParseErrors(errors, logger));
    }

    private static void Run(ApplicationOptions options, ILogger logger)
    {
        logger.LogInformation("Starting GPhotosMetaFixer...");
        logger.LogInformation("Source Path: {SourcePath}", options.SourceFolder);
        logger.LogInformation("Dry Run: {DryRun}", options.DryRun);

        if (options.DryRun)
        {
            logger.LogWarning("DRY RUN ENABLED. No changes will be made to files.");
        }
        
        var progress = new ProgressDisplay();
        
        // Step 1: Match media files with JSON metadata
        var mediaToJsonMap = MatchMediaFiles(options.SourceFolder, logger, progress);
        
        // Step 2: Extract metadata from matched files
        var metadataList = ExtractMetadata(mediaToJsonMap, logger, progress);
        
        // Step 3: Copy files to destination and fix metadata
        var fileManager = new FileManager(options, logger);
        CopyFiles(fileManager, metadataList, logger, progress, options);
        
        // Step 4: Fix metadata
        var fixer = FixMetadata(fileManager, metadataList, logger, progress, options);
        
        // Step 5: Process pending EXIF updates
        ProcessExifUpdates(fixer, logger, progress, options);
        
        logger.LogInformation("All steps completed.");
    }

    private static void HandleParseErrors(IEnumerable<Error> errors, ILogger logger)
    {
        logger.LogError("Failed to parse command line arguments.");
        foreach (var error in errors)
        {
            logger.LogError("{Error}", error.ToString());
        }
    }

    private static Dictionary<string, string> MatchMediaFiles(string sourcePath, ILogger logger, ProgressDisplay progress)
    {
        var matcher = new DirectoryFileMatcher(logger);
        
        // Count JSON files for progress reporting since the matcher iterates over JSON files
        // Use try-catch for directory access in case path is invalid
        int jsonFileCount = 0;
        try 
        {
            jsonFileCount = Directory.GetFiles(sourcePath, "*.json", SearchOption.AllDirectories).Length;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error accessing source directory: {SourcePath}", sourcePath);
            return new Dictionary<string, string>();
        }
            
        progress.StartStep("Matching", jsonFileCount);
        progress.AttachAsActive();
        
        var mediaToJsonMap = matcher.ScanDirectory(sourcePath, progress.Report);
        progress.CompleteStep();
        
        logger.LogInformation("Found {FileCount} media files with metadata", mediaToJsonMap.Count);
        return mediaToJsonMap;
    }

    private static List<Models.MediaMetadata> ExtractMetadata(Dictionary<string, string> mediaToJsonMap, ILogger logger, ProgressDisplay progress)
    {
        var metadataExtractor = new Services.MetadataExtractor(logger);
        var metadataList = new List<Models.MediaMetadata>();
        
        progress.StartStep("Extracting", mediaToJsonMap.Count);
        progress.AttachAsActive();
        
        foreach (var kvp in mediaToJsonMap)
        {
            try
            {
                var metadata = metadataExtractor.ExtractMetadata(kvp.Key, kvp.Value);
                metadataList.Add(metadata);
                progress.Report(metadataList.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process file: {FilePath}", kvp.Key);
            }
        }
        
        progress.CompleteStep();
        logger.LogInformation("Metadata extraction completed. Processed {ProcessedCount} files.", metadataList.Count);
        return metadataList;
    }

    private static void CopyFiles(FileManager fileManager, List<Models.MediaMetadata> metadataList, ILogger logger, ProgressDisplay progress, ApplicationOptions options)
    {
        progress.StartStep("Copying Files", metadataList.Count);
        progress.AttachAsActive();
        
        var processedCount = 0;
        foreach (var metadata in metadataList)
        {
            try
            {
                fileManager.CopyFileToDestination(metadata.MediaFilePath);
                processedCount++;
                progress.Report(processedCount);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy file: {FilePath}", metadata.MediaFilePath);
            }
        }
        
        progress.CompleteStep();
        progress.Detach();
    }

    private static NewMetadataFixer FixMetadata(FileManager fileManager, List<Models.MediaMetadata> metadataList, ILogger logger, ProgressDisplay progress, ApplicationOptions options)
    {
        var fixer = new NewMetadataFixer(logger, fileManager, options);
        
        progress.StartStep("Processing Metadata", metadataList.Count);
        progress.AttachAsActive();
        
        var processedCount = 0;
        var lockObject = new object();
        
        // Process metadata in parallel for better performance
        Parallel.ForEach(metadataList, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, metadata => {
            try
            {
                fixer.FixMetadata(metadata);
                
                lock (lockObject)
                {
                    processedCount++;
                    progress.Report(processedCount);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fix metadata for file: {FilePath}", metadata.MediaFilePath);
            }
        });
        
        progress.CompleteStep();
        progress.Detach();
        return fixer;
    }

    private static void ProcessExifUpdates(NewMetadataFixer fixer, ILogger logger, ProgressDisplay progress, ApplicationOptions options)
    {
        if (fixer.PendingUpdatesCount > 0)
        {
            logger.LogInformation("Found {Count} files requiring EXIF metadata updates", fixer.PendingUpdatesCount);
            progress.AttachAsActive();
            fixer.ProcessPendingUpdates(progress);
            progress.Detach();
        }
        else
        {
            logger.LogInformation("No files require EXIF metadata updates");
        }
    }
}
