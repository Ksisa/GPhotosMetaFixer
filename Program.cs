using GPhotosMetaFixer.Services;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer;

internal class Program
{
    static void Main(string[] args)
    {
        ILogger logger = new AppLogger(nameof(Program));
        
        // Use actual source path instead of Example folder
        var sourcePath = @"C:\Users\Kris\Desktop\New folder\src";
        
        logger.LogInformation("Starting metadata extraction for path: {SourcePath}", sourcePath);
        
        var matcher = new DirectoryFileMatcher(logger);
        var mediaToJsonMap = matcher.ScanDirectory(sourcePath);
        
        logger.LogInformation("Found {FileCount} media files to process", mediaToJsonMap.Count);
        
        var metadataExtractor = new Services.MetadataExtractor(logger);
        
        // Process each file
        int processedCount = 0;
        foreach (var kvp in mediaToJsonMap)
        {
            try
            {
                var metadata = metadataExtractor.ExtractMetadata(kvp.Key, kvp.Value);
                processedCount++;
                
                if (processedCount % 100 == 0)
                {
                    logger.LogInformation("Processed {ProcessedCount} of {TotalCount} files", processedCount, mediaToJsonMap.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process file: {FilePath}", kvp.Key);
            }
        }
        
        logger.LogInformation("Metadata extraction completed. Processed {ProcessedCount} files.", processedCount);
    }
    
}
