using GPhotosMetaFixer.Options;
using GPhotosMetaFixer.Services;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer;

internal class Program
{
    static void Main(string[] args)
    {
        var logger = new AppLogger(nameof(Program));
        
        // Initialize options with defaults
        var options = new ApplicationOptions
        {
            SourceFolder = @"C:\Users\Kris\Desktop\Prod\src",
            DestinationFolder = string.Empty, // Will be auto-generated if empty
            DryRun = false,
            LogFile = string.Empty
        };
        
        var progress = new ProgressDisplay();
        
        logger.LogInformation("Starting metadata extraction for path: {SourcePath}", options.SourceFolder);
        if (options.DryRun)
        {
            logger.LogInformation("DRY RUN MODE: No files will be modified");
        }
        
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

    private static Dictionary<string, string> MatchMediaFiles(string sourcePath, ILogger logger, ProgressDisplay progress)
    {
        var matcher = new DirectoryFileMatcher(logger);
        var mediaCandidates = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
            
        progress.StartStep("Matching", mediaCandidates.Length);
        progress.AttachAsActive();
        
        var mediaToJsonMap = matcher.ScanDirectory(sourcePath, progress.Report);
        progress.CompleteStep();
        
        logger.LogInformation("Found {FileCount} media files to process", mediaToJsonMap.Count);
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