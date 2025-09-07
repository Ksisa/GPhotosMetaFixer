using GPhotosMetaFixer.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using Microsoft.Extensions.Logging;
using System.Text;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// Fixes metadata in media files by copying them to a destination folder and updating timestamps
/// </summary>
public class MetadataFixer(ILogger logger)
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mkv", ".m4v"];

    /// <summary>
    /// Processes all media files in the source directory and fixes their metadata
    /// </summary>
    /// <param name="sourceDirectory">Source directory containing media files and JSON metadata</param>
    /// <param name="destinationDirectory">Destination directory where fixed files will be copied</param>
    /// <param name="mediaToJsonMapping">Mapping of media files to their JSON metadata files</param>
    public void ProcessFiles(string sourceDirectory, string destinationDirectory, Dictionary<string, string> mediaToJsonMapping, Action<int>? progressCallback = null)
    {
        logger.LogInformation("Starting metadata fixing process");
        logger.LogInformation("Source: {SourceDirectory}", sourceDirectory);
        logger.LogInformation("Destination: {DestinationDirectory}", destinationDirectory);
        logger.LogInformation("Files to process: {FileCount}", mediaToJsonMapping.Count);

        // Ensure destination directory exists
        System.IO.Directory.CreateDirectory(destinationDirectory);

        int processedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        foreach (var kvp in mediaToJsonMapping)
        {
            try
            {
                var result = ProcessSingleFile(kvp.Key, kvp.Value, sourceDirectory, destinationDirectory);
                
                switch (result)
                {
                    case ProcessResult.Processed:
                        processedCount++;
                        break;
                    case ProcessResult.Skipped:
                        skippedCount++;
                        break;
                    case ProcessResult.Error:
                        errorCount++;
                        break;
                }

                // Progress callback after each file handled
                progressCallback?.Invoke(processedCount + skippedCount + errorCount);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error processing file: {FilePath}", kvp.Key);
                errorCount++;
            }
        }

        logger.LogInformation("Metadata fixing completed. Fixed: {Fixed}, Skipped: {Skipped}, Errors: {Errors}", 
            processedCount, skippedCount, errorCount);
    }

    /// <summary>
    /// Processes a single media file
    /// </summary>
    private ProcessResult ProcessSingleFile(string mediaFilePath, string jsonFilePath, string sourceDirectory, string destinationDirectory)
    {
        var fileName = Path.GetFileName(mediaFilePath);
        var relativePath = Path.GetRelativePath(sourceDirectory, mediaFilePath);
        var destinationFilePath = Path.Combine(destinationDirectory, relativePath);
        var destinationDir = Path.GetDirectoryName(destinationFilePath);

        // Ensure destination directory exists
        if (!string.IsNullOrEmpty(destinationDir))
        {
            System.IO.Directory.CreateDirectory(destinationDir);
        }

        // Extract metadata from both files
        var metadataExtractor = new Services.MetadataExtractor(logger);
        var metadata = metadataExtractor.ExtractMetadata(mediaFilePath, jsonFilePath);

        // Apply timestamp fixing logic
        var timestampResult = ProcessTimestampLogic(metadata, fileName);
        
        if (timestampResult == ProcessResult.Error)
        {
            // Copy file as-is without metadata changes
            File.Copy(mediaFilePath, destinationFilePath, overwrite: true);
            return ProcessResult.Error;
        }
        else if (timestampResult == ProcessResult.Skipped)
        {
            // Copy file as-is without metadata changes
            File.Copy(mediaFilePath, destinationFilePath, overwrite: true);
            return ProcessResult.Skipped;
        }

        // Check if we need to fix geo data
        var shouldFixGeoData = ShouldFixGeoData(metadata, fileName);

        // Copy file and update metadata
        File.Copy(mediaFilePath, destinationFilePath, overwrite: true);
        
        if (metadata.JsonTimestamp.HasValue)
        {
            UpdateFileTimestamp(destinationFilePath, metadata.JsonTimestamp.Value);
        }

        if (shouldFixGeoData && metadata.JsonGeolocation != null)
        {
            UpdateFileGeoData(destinationFilePath, metadata.JsonGeolocation);
        }

        return ProcessResult.Processed;
    }

    /// <summary>
    /// Applies the timestamp fixing logic according to the specified rules
    /// </summary>
    private ProcessResult ProcessTimestampLogic(MediaMetadata metadata, string fileName)
    {
        // Rule 2.1: If both media and JSON time taken metadata is missing, output error log, skip
        if (!metadata.MediaTimestamp.HasValue && !metadata.JsonTimestamp.HasValue)
        {
            logger.LogError("Both media and JSON timestamp metadata missing for {FileName}", fileName);
            return ProcessResult.Error;
        }

        // Rule 2.2: If media time taken metadata is missing and JSON time taken metadata is in the last 7 days, output error log, skip
        if (!metadata.MediaTimestamp.HasValue && metadata.JsonTimestamp.HasValue)
        {
            var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);
            if (metadata.JsonTimestamp.Value > sevenDaysAgo)
            {
                logger.LogError("Media timestamp missing and JSON timestamp is recent (within 7 days) for {FileName}. JSON timestamp: {JsonTimestamp}", 
                    fileName, metadata.JsonTimestamp.Value);
                return ProcessResult.Error;
            }
        }

        // Rule 2.3: If media time taken metadata is within 12h of JSON time taken metadata, output info log, skip
        if (metadata.MediaTimestamp.HasValue && metadata.JsonTimestamp.HasValue)
        {
            var timeDifference = Math.Abs((metadata.MediaTimestamp.Value - metadata.JsonTimestamp.Value).TotalHours);
            if (timeDifference <= 12)
            {
                logger.LogDebug("Media and JSON timestamps are within 12 hours for {FileName}. Media: {MediaTimestamp}, JSON: {JsonTimestamp}, Difference: {DifferenceHours:F1}h", 
                    fileName, metadata.MediaTimestamp.Value, metadata.JsonTimestamp.Value, timeDifference);
                return ProcessResult.Skipped;
            }
        }

        // Rule 2.4: Otherwise, overwrite the media time taken metadata with JSON time taken metadata
        if (metadata.JsonTimestamp.HasValue)
        {
            logger.LogInformation("Fixed timestamp for {FileName}. Old: {OldTimestamp}, New: {NewTimestamp}", 
                fileName, metadata.MediaTimestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "None", metadata.JsonTimestamp.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        return ProcessResult.Processed;
    }

    /// <summary>
    /// Applies the geo data fixing logic - adds geo data from JSON if media file doesn't have it
    /// </summary>
    private bool ShouldFixGeoData(MediaMetadata metadata, string fileName)
    {
        // If media file already has geo data, don't overwrite it
        if (metadata.MediaGeolocation != null)
        {
            logger.LogDebug("Media file already has geo data for {FileName}, skipping geo data fix", fileName);
            return false;
        }

        // If JSON doesn't have geo data, nothing to fix
        if (metadata.JsonGeolocation == null)
        {
            logger.LogDebug("JSON file has no geo data for {FileName}, skipping geo data fix", fileName);
            return false;
        }

        // Media file doesn't have geo data but JSON does - we should fix it
        logger.LogInformation("Will add geo data to {FileName}. Location: {Latitude:F6}, {Longitude:F6}", 
            fileName, metadata.JsonGeolocation.Latitude, metadata.JsonGeolocation.Longitude);
        return true;
    }

    /// <summary>
    /// Updates the timestamp metadata in the file
    /// </summary>
    private void UpdateFileTimestamp(string filePath, DateTime newTimestamp)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        
        try
        {
            if (ImageExtensions.Contains(extension))
            {
                UpdateImageTimestamp(filePath, newTimestamp);
            }
            else if (VideoExtensions.Contains(extension))
            {
                UpdateVideoTimestamp(filePath, newTimestamp);
            }
            else
            {
                logger.LogWarning("Unsupported file type for timestamp update: {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update timestamp for {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Updates timestamp in image files using EXIF data
    /// </summary>
    private void UpdateImageTimestamp(string filePath, DateTime newTimestamp)
    {
        // First, try to write EXIF Date Taken using exiftool (DateTimeOriginal, CreateDate, ModifyDate)
        TryUpdateImageDateTakenWithExifTool(filePath, newTimestamp);

        // Always also update file system timestamps as a convenience
        var fileInfo = new FileInfo(filePath);
        fileInfo.CreationTimeUtc = newTimestamp;
        fileInfo.LastWriteTimeUtc = newTimestamp;
        
        logger.LogDebug("Updated file system timestamps for image: {FilePath}", filePath);
    }

    /// <summary>
    /// Updates timestamp in video files
    /// </summary>
    private void UpdateVideoTimestamp(string filePath, DateTime newTimestamp)
    {
        // First, try to write QuickTime/MP4 creation dates using exiftool
        TryUpdateVideoDateTakenWithExifTool(filePath, newTimestamp);

        // Always also update file system timestamps as a convenience
        var fileInfo = new FileInfo(filePath);
        fileInfo.CreationTimeUtc = newTimestamp;
        fileInfo.LastWriteTimeUtc = newTimestamp;
        
        logger.LogDebug("Updated file system timestamps for video: {FilePath}", filePath);
    }

    /// <summary>
    /// Attempts to write EXIF Date Taken for images using exiftool. Safe to call even if exiftool is missing.
    /// </summary>
    private void TryUpdateImageDateTakenWithExifTool(string filePath, DateTime newTimestamp)
    {
        try
        {
            // EXIF expects format yyyy:MM:dd HH:mm:ss (generally local time without TZ)
            var dtLocal = newTimestamp.ToLocalTime();
            var exifDate = dtLocal.ToString("yyyy:MM:dd HH:mm:ss");

            // Set common EXIF date fields
            var args = $"-overwrite_original -DateTimeOriginal=\"{exifDate}\" -CreateDate=\"{exifDate}\" -ModifyDate=\"{exifDate}\" \"{filePath}\"";
            if (RunExifTool(args, out var stdOut, out var stdErr))
            {
                logger.LogDebug("Updated EXIF Date Taken via exiftool for image: {FilePath}", filePath);
            }
            else
            {
                logger.LogWarning("Failed to update EXIF Date Taken via exiftool for image: {FilePath}. Output: {StdOut}. Error: {StdErr}", 
                    filePath, stdOut, stdErr);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception while updating EXIF Date Taken via exiftool for image: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Attempts to write QuickTime/MP4 date fields for videos using exiftool. Safe to call even if exiftool is missing.
    /// </summary>
    private void TryUpdateVideoDateTakenWithExifTool(string filePath, DateTime newTimestamp)
    {
        try
        {
            // QuickTime dates are often stored in UTC; exiftool manages conversions. We'll provide local formatted time similar to images.
            var dtLocal = newTimestamp.ToLocalTime();
            var qtDate = dtLocal.ToString("yyyy:MM:dd HH:mm:ss");

            // Update a set of common QuickTime date fields to maximize compatibility
            var args = $"-overwrite_original -CreateDate=\"{qtDate}\" -ModifyDate=\"{qtDate}\" -TrackCreateDate=\"{qtDate}\" -TrackModifyDate=\"{qtDate}\" -MediaCreateDate=\"{qtDate}\" -MediaModifyDate=\"{qtDate}\" \"{filePath}\"";
            if (RunExifTool(args, out var stdOut, out var stdErr))
            {
                logger.LogDebug("Updated QuickTime date fields via exiftool for video: {FilePath}", filePath);
            }
            else
            {
                logger.LogWarning("Failed to update QuickTime date fields via exiftool for video: {FilePath}. Output: {StdOut}. Error: {StdErr}", 
                    filePath, stdOut, stdErr);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception while updating QuickTime date fields via exiftool for video: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Updates the geo data metadata in the file
    /// </summary>
    private void UpdateFileGeoData(string filePath, Models.GeoLocation geoLocation)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        
        try
        {
            if (ImageExtensions.Contains(extension))
            {
                UpdateImageGeoData(filePath, geoLocation);
            }
            else if (VideoExtensions.Contains(extension))
            {
                UpdateVideoGeoData(filePath, geoLocation);
            }
            else
            {
                logger.LogWarning("Unsupported file type for geo data update: {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update geo data for {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Updates geo data in image files using EXIF data
    /// </summary>
    private void UpdateImageGeoData(string filePath, Models.GeoLocation geoLocation)
    {
        // For now, we'll log that geo data would be updated
        // In a more complete implementation, you would use a library like ExifLib
        // to actually modify the EXIF GPS data in the image file
        
        logger.LogInformation("Would update EXIF GPS data for image: {FilePath}. Location: {Latitude:F6}, {Longitude:F6}", 
            filePath, geoLocation.Latitude, geoLocation.Longitude);
        
        // TODO: Implement actual EXIF GPS data writing
        // This would require a library that can write EXIF data, such as:
        // - ExifLib.NET
        // - MetadataExtractor (write capabilities)
        // - ImageSharp with EXIF support
    }

    /// <summary>
    /// Updates geo data in video files
    /// </summary>
    private void UpdateVideoGeoData(string filePath, Models.GeoLocation geoLocation)
    {
        // For now, we'll log that geo data would be updated
        // In a more complete implementation, you would use a library to modify
        // the QuickTime metadata in the video file
        
        logger.LogInformation("Would update QuickTime GPS data for video: {FilePath}. Location: {Latitude:F6}, {Longitude:F6}", 
            filePath, geoLocation.Latitude, geoLocation.Longitude);
        
        // TODO: Implement actual QuickTime GPS data writing
        // This would require a library that can write QuickTime metadata, such as:
        // - FFmpeg.NET
        // - MediaInfo.NET
        // - A custom QuickTime metadata writer
    }

    /// <summary>
    /// Runs exiftool with the provided arguments. Returns true if exit code is 0.
    /// </summary>
    private static bool RunExifTool(string arguments, out string stdOut, out string stdErr)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "exiftool",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            stdOut = process.StandardOutput.ReadToEnd();
            stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            stdOut = string.Empty;
            stdErr = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Result of processing a single file
    /// </summary>
    private enum ProcessResult
    {
        Processed,
        Skipped,
        Error
    }
}