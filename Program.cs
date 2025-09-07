using GPhotosMetaFixer.Services;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer;

internal class Program
{
    static void Main(string[] args)
    {
        ILogger logger = new AppLogger(nameof(Program));
        
        // Use actual source path instead of Example folder
        var sourcePath = @"C:\Users\Kris\Desktop\Prod\src";
        
        logger.LogInformation("Starting metadata extraction for path: {SourcePath}", sourcePath);
        
        var matcher = new DirectoryFileMatcher(logger);

        var progress = new ProgressDisplay();

        // Matching step
        // Estimate media file count by quick scan to set a total for the bar
        var allFiles = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
        var mediaCandidates = allFiles.Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
        progress.StartStep("Matching", mediaCandidates.Length);
        progress.AttachAsActive();
        var mediaToJsonMap = matcher.ScanDirectory(sourcePath, processed => progress.Report(processed));
        progress.CompleteStep();
        
        logger.LogInformation("Found {FileCount} media files to process", mediaToJsonMap.Count);
        
        var metadataExtractor = new Services.MetadataExtractor(logger);
        
        // Process each file and collect metadata
        var metadataList = new List<Models.MediaMetadata>();
        int processedCount = 0;
        
        // Extracting step
        progress.StartStep("Extracting", mediaToJsonMap.Count);
        progress.AttachAsActive();
        foreach (var kvp in mediaToJsonMap)
        {
            try
            {
                var metadata = metadataExtractor.ExtractMetadata(kvp.Key, kvp.Value);
                metadataList.Add(metadata);
                processedCount++;
                progress.Report(processedCount);
                
                // Periodic progress logs removed; progress bar handles visual feedback
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process file: {FilePath}", kvp.Key);
            }
        }
        progress.CompleteStep();
        
        logger.LogInformation("Metadata extraction completed. Processed {ProcessedCount} files.", processedCount);

        // Geo report: media missing geo but JSON has geo
        var missingMediaGeoButJsonHas = metadataList.Where(m => m.MediaGeolocation == null && m.JsonGeolocation != null).ToList();
        logger.LogInformation("Geo report: {Count} files where media lacks geo but JSON has it (out of {Total}).", missingMediaGeoButJsonHas.Count, metadataList.Count);
        
        foreach (var item in missingMediaGeoButJsonHas)
        {
            logger.LogInformation("Missing geo in media but present in JSON: {FilePath}", Path.GetFileName(item.MediaFilePath));
        }

        // Writing step (copying files with new metadata fixer)
        var fixer = new NewMetadataFixer(sourcePath, logger);
        
        progress.StartStep("Writing", metadataList.Count);
        progress.AttachAsActive();
        
        int copiedCount = 0;
        foreach (var metadata in metadataList)
        {
            try
            {
                fixer.FixMetadata(metadata);
                copiedCount++;
                
                progress.Report(copiedCount);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy file: {FilePath}", metadata.MediaFilePath);
            }
        }
        
        progress.CompleteStep();
        progress.Detach();

        // Process all pending metadata updates in batches for optimal performance
        fixer.ProcessPendingUpdates();

        logger.LogInformation("All steps completed.");
    }
}