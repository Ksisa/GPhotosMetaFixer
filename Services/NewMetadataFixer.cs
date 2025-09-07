using GPhotosMetaFixer.Models;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// A clean, focused metadata fixer that handles file operations and metadata corrections with batch processing
/// </summary>
public class NewMetadataFixer
{
    private readonly string sourceRoot;
    private readonly ILogger logger;
    private readonly List<MetadataUpdate> pendingUpdates = new();

    public NewMetadataFixer(string sourceRoot, ILogger logger)
    {
        this.sourceRoot = sourceRoot;
        this.logger = logger;
        PrepareDestinationDirectory();
    }

    /// <summary>
    /// Fixes metadata by copying files to destination directory with proper structure
    /// </summary>
    /// <param name="metadata">The media metadata containing source file path</param>
    public void FixMetadata(MediaMetadata metadata)
    {
        // Always copy the file first
        //CopyFileWithDirectoryStructure(metadata);

        // Handle motion files - just copy them without any metadata processing
        if (metadata.JsonFilePath == "motion")
        {
            logger.LogDebug("Motion file copied without metadata processing: {FileName}", Path.GetFileName(metadata.MediaFilePath));
            return;
        }

        // Rule 2.1: If both media and JSON time taken metadata is missing, output error log, skip
        if (RuleBothTimestampsMissing(metadata))
        {
            return;
        }

        // Rule 2.2: If media time taken metadata is missing and JSON time taken metadata is in the last 1 days, output error log, skip
        if (RuleMediaMissingJsonRecent(metadata))
        {
            return;
        }

        // Rule 2.3: If media time taken metadata is within 12h of JSON time taken metadata, output info log, skip
        if (RuleTimestampsWithin12Hours(metadata))
        {
            return;
        }

        // Rule 2.4: Otherwise, overwrite the media time taken metadata with JSON time taken metadata
        if (RuleOverwriteMediaTimestamp(metadata))
        {
            // Add to batch processing queue instead of processing immediately
            var destinationRoot = GetDestinationRoot();
            var relativePath = Path.GetRelativePath(sourceRoot, metadata.MediaFilePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            
            pendingUpdates.Add(new MetadataUpdate
            {
                FilePath = destinationPath,
                NewTimestamp = metadata.JsonTimestamp!.Value,
                IsImage = IsImageFile(Path.GetExtension(destinationPath).ToLowerInvariant())
            });
            return;
        }
    }

    /// <summary>
    /// Processes all pending metadata updates in batches for optimal performance
    /// </summary>
    public void ProcessPendingUpdates()
    {
        if (pendingUpdates.Count == 0)
        {
            logger.LogDebug("No pending metadata updates to process");
            return;
        }

        logger.LogInformation("Processing {Count} pending metadata updates in batches", pendingUpdates.Count);

        // Group by file type for batch processing
        var imageUpdates = pendingUpdates.Where(u => u.IsImage).ToList();
        var videoUpdates = pendingUpdates.Where(u => !u.IsImage).ToList();

        // Process images in batches
        if (imageUpdates.Count > 0)
        {
            ProcessBatchUpdates(imageUpdates, "image");
        }

        // Process videos in batches
        if (videoUpdates.Count > 0)
        {
            ProcessBatchUpdates(videoUpdates, "video");
        }

        // Clear the pending updates
        pendingUpdates.Clear();
    }

    /// <summary>
    /// Processes a batch of metadata updates using exiftool
    /// </summary>
    private void ProcessBatchUpdates(List<MetadataUpdate> updates, string fileType)
    {
        const int batchSize = 10; // Process 10 files at a time
        
        for (int i = 0; i < updates.Count; i += batchSize)
        {
            var batch = updates.Skip(i).Take(batchSize).ToList();
            ProcessSingleBatch(batch, fileType);
        }
    }

    /// <summary>
    /// Processes a single batch of files with exiftool
    /// </summary>
    private void ProcessSingleBatch(List<MetadataUpdate> batch, string fileType)
    {
        try
        {
            if (fileType == "image")
            {
                ProcessImageBatch(batch);
            }
            else
            {
                ProcessVideoBatch(batch);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process batch of {FileType} files", fileType);
        }
    }

    /// <summary>
    /// Processes a batch of image files with optimized exiftool command
    /// If batch fails, retries each file individually
    /// </summary>
    private void ProcessImageBatch(List<MetadataUpdate> batch)
    {
        var filePaths = batch.Select(u => $"\"{u.FilePath}\"").ToArray();
        var filesArg = string.Join(" ", filePaths);
        
        // Use the first file's timestamp as a template (they should all be similar for batch processing)
        var firstUpdate = batch.First();
        var dtLocal = firstUpdate.NewTimestamp.ToLocalTime();
        var exifDate = dtLocal.ToString("yyyy:MM:dd HH:mm:ss");

        // Optimized exiftool command with performance flags
        // -overwrite_original: modify files in place (faster than creating backups)
        // -q: quiet mode (less output processing)
        // -n: don't print tag names (faster)
        // -P: preserve file modification date (faster)
        var args = $"-overwrite_original -q -n -P -DateTimeOriginal=\"{exifDate}\" -CreateDate=\"{exifDate}\" -ModifyDate=\"{exifDate}\" {filesArg}";
        
        if (RunExifTool(args, out var stdOut, out var stdErr))
        {
            logger.LogDebug("Updated EXIF metadata for batch of {Count} images", batch.Count);
        }
        else
        {
            logger.LogWarning("Failed to update EXIF metadata for batch of {Count} images. Retrying individual files. Batch error: {StdErr}", 
                batch.Count, stdErr);
            
            // Retry each file individually
            foreach (var update in batch)
            {
                ProcessIndividualImage(update);
            }
        }
    }

    /// <summary>
    /// Processes a batch of video files with optimized exiftool command
    /// If batch fails, retries each file individually
    /// </summary>
    private void ProcessVideoBatch(List<MetadataUpdate> batch)
    {
        var filePaths = batch.Select(u => $"\"{u.FilePath}\"").ToArray();
        var filesArg = string.Join(" ", filePaths);
        
        // Use the first file's timestamp as a template
        var firstUpdate = batch.First();
        var dtLocal = firstUpdate.NewTimestamp.ToLocalTime();
        var qtDate = dtLocal.ToString("yyyy:MM:dd HH:mm:ss");

        // Optimized exiftool command with performance flags
        var args = $"-overwrite_original -q -n -P -MediaCreateDate=\"{qtDate}\" -CreateDate=\"{qtDate}\" -ModifyDate=\"{qtDate}\" -TrackCreateDate=\"{qtDate}\" -TrackModifyDate=\"{qtDate}\" -MediaModifyDate=\"{qtDate}\" {filesArg}";
        
        if (RunExifTool(args, out var stdOut, out var stdErr))
        {
            logger.LogDebug("Updated QuickTime metadata for batch of {Count} videos", batch.Count);
        }
        else
        {
            logger.LogWarning("Failed to update QuickTime metadata for batch of {Count} videos. Retrying individual files. Batch error: {StdErr}", 
                batch.Count, stdErr);
            
            // Retry each file individually
            foreach (var update in batch)
            {
                ProcessIndividualVideo(update);
            }
        }
    }

    /// <summary>
    /// Processes a single image file with exiftool
    /// </summary>
    private void ProcessIndividualImage(MetadataUpdate update)
    {
        var dtLocal = update.NewTimestamp.ToLocalTime();
        var exifDate = dtLocal.ToString("yyyy:MM:dd HH:mm:ss");
        var fileName = Path.GetFileName(update.FilePath);

        var args = $"-overwrite_original -q -n -P -DateTimeOriginal=\"{exifDate}\" -CreateDate=\"{exifDate}\" -ModifyDate=\"{exifDate}\" \"{update.FilePath}\"";
        
        if (RunExifTool(args, out var stdOut, out var stdErr))
        {
            logger.LogDebug("Updated EXIF metadata for individual image: {FileName}", fileName);
        }
        else
        {
            logger.LogWarning("Failed to update EXIF metadata for individual image: {FileName}. Error: {StdErr}", 
                fileName, stdErr);
        }
    }

    /// <summary>
    /// Processes a single video file with exiftool
    /// </summary>
    private void ProcessIndividualVideo(MetadataUpdate update)
    {
        var dtLocal = update.NewTimestamp.ToLocalTime();
        var qtDate = dtLocal.ToString("yyyy:MM:dd HH:mm:ss");
        var fileName = Path.GetFileName(update.FilePath);

        var args = $"-overwrite_original -q -n -P -MediaCreateDate=\"{qtDate}\" -CreateDate=\"{qtDate}\" -ModifyDate=\"{qtDate}\" -TrackCreateDate=\"{qtDate}\" -TrackModifyDate=\"{qtDate}\" -MediaModifyDate=\"{qtDate}\" \"{update.FilePath}\"";
        
        if (RunExifTool(args, out var stdOut, out var stdErr))
        {
            logger.LogDebug("Updated QuickTime metadata for individual video: {FileName}", fileName);
        }
        else
        {
            logger.LogWarning("Failed to update QuickTime metadata for individual video: {FileName}. Error: {StdErr}", 
                fileName, stdErr);
        }
    }

    /// <summary>
    /// Rule 2.1: If both media and JSON time taken metadata is missing, output error log, skip
    /// </summary>
    /// <param name="metadata">The media metadata to check</param>
    /// <returns>True if both timestamps are missing (should skip), false otherwise</returns>
    public bool RuleBothTimestampsMissing(MediaMetadata metadata)
    {
        if (!metadata.MediaTimestamp.HasValue && !metadata.JsonTimestamp.HasValue)
        {
            var fileName = Path.GetFileName(metadata.MediaFilePath);
            logger.LogError("Both media and JSON timestamp metadata missing for {FileName}", fileName);
            return true; // Should skip
        }
        return false; // Should not skip
    }

    /// <summary>
    /// Rule 2.2: If media time taken metadata is missing and JSON time taken metadata is in the last 1 days, output error log, skip
    /// </summary>
    /// <param name="metadata">The media metadata to check</param>
    /// <returns>True if should skip (media missing and JSON recent), false otherwise</returns>
    public bool RuleMediaMissingJsonRecent(MediaMetadata metadata)
    {
        if (!metadata.MediaTimestamp.HasValue && metadata.JsonTimestamp.HasValue)
        {
            var sevenDaysAgo = DateTime.UtcNow.AddDays(-1);
            if (metadata.JsonTimestamp.Value > sevenDaysAgo)
            {
                var fileName = Path.GetFileName(metadata.MediaFilePath);
                logger.LogError("Media timestamp missing and JSON timestamp is recent (within 7 days) for {FileName}. JSON timestamp: {JsonTimestamp}", 
                    fileName, metadata.JsonTimestamp.Value);
                return true; // Should skip
            }
        }
        return false; // Should not skip
    }

    /// <summary>
    /// Rule 2.3: If media time taken metadata is within 12h of JSON time taken metadata, output info log, skip
    /// </summary>
    /// <param name="metadata">The media metadata to check</param>
    /// <returns>True if should skip (timestamps within 12h), false otherwise</returns>
    public bool RuleTimestampsWithin12Hours(MediaMetadata metadata)
    {
        if (metadata.MediaTimestamp.HasValue && metadata.JsonTimestamp.HasValue)
        {
            var timeDifference = Math.Abs((metadata.MediaTimestamp.Value - metadata.JsonTimestamp.Value).TotalHours);
            if (timeDifference <= 12)
            {
                var fileName = Path.GetFileName(metadata.MediaFilePath);
                logger.LogDebug("Media and JSON timestamps are within 12 hours for {FileName}. Media: {MediaTimestamp}, JSON: {JsonTimestamp}, Difference: {DifferenceHours:F1}h", 
                    fileName, metadata.MediaTimestamp.Value, metadata.JsonTimestamp.Value, timeDifference);
                return true; // Should skip
            }
        }
        return false; // Should not skip
    }

    /// <summary>
    /// Rule 2.4: Otherwise, overwrite the media time taken metadata with JSON time taken metadata
    /// </summary>
    /// <param name="metadata">The media metadata to process</param>
    /// <returns>True if timestamp was updated, false otherwise</returns>
    public bool RuleOverwriteMediaTimestamp(MediaMetadata metadata)
    {
        if (metadata.JsonTimestamp.HasValue)
        {
            // First, ensure the file is copied to destination
            CopyFileWithDirectoryStructure(metadata);
            
            // Update the file creation date to match JSON timestamp
            UpdateFileCreationDate(metadata);
            
            return true; // Will be added to batch processing queue for EXIF metadata
        }
        return false; // No timestamp to update
    }

    #region files

    /// <summary>
    /// Prepares the destination directory by recreating the entire directory structure from the source
    /// </summary>
    private void PrepareDestinationDirectory()
    {
        try
        {
            var destinationRoot = GetDestinationRoot();
            
            // Get all directories in the source folder
            var sourceDirectories = Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories);
            
            // Create the destination root first
            EnsureDirectoryExists(destinationRoot);
            
            // Create each subdirectory in the destination
            foreach (var sourceDir in sourceDirectories)
            {
                var relativePath = Path.GetRelativePath(sourceRoot, sourceDir);
                var destinationDir = Path.Combine(destinationRoot, relativePath);
                EnsureDirectoryExists(destinationDir);
            }
            
            logger.LogDebug("Prepared destination directory structure with {DirectoryCount} directories", sourceDirectories.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare destination directory structure");
        }
    }

    /// <summary>
    /// Copies a media file to the destination directory while preserving the directory structure
    /// </summary>
    /// <param name="metadata">The media metadata containing source file path</param>
    /// <returns>The destination file path if successful, null if failed</returns>
    private void CopyFileWithDirectoryStructure(MediaMetadata metadata)
    {
        try
        {
            var sourcePath = metadata.MediaFilePath;
            
            if (!File.Exists(sourcePath))
            {
                logger.LogWarning("Source file does not exist: {SourcePath}", sourcePath);
                return;
            }

            var destinationRoot = GetDestinationRoot();
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);

            File.Copy(sourcePath, destinationPath, overwrite: true);
            
            logger.LogDebug("Copied file: {SourcePath} -> {DestinationPath}", sourcePath, destinationPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to copy file: {SourcePath}", metadata.MediaFilePath);
        }
    }

    /// <summary>
    /// Updates the file creation date to match the JSON timestamp
    /// </summary>
    /// <param name="metadata">The media metadata containing the JSON timestamp</param>
    private void UpdateFileCreationDate(MediaMetadata metadata)
    {
        try
        {
            if (!metadata.JsonTimestamp.HasValue)
            {
                return;
            }

            var destinationRoot = GetDestinationRoot();
            var relativePath = Path.GetRelativePath(sourceRoot, metadata.MediaFilePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);

            if (!File.Exists(destinationPath))
            {
                logger.LogWarning("Destination file does not exist for creation date update: {DestinationPath}", destinationPath);
                return;
            }

            var jsonTimestamp = metadata.JsonTimestamp.Value;
            
            // Update file creation time, last write time, and last access time to match JSON timestamp
            File.SetCreationTime(destinationPath, jsonTimestamp);
            File.SetLastWriteTime(destinationPath, jsonTimestamp);
            File.SetLastAccessTime(destinationPath, jsonTimestamp);
            
            logger.LogDebug("Updated file timestamps for: {DestinationPath} to {Timestamp}", destinationPath, jsonTimestamp);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update file creation date for: {FilePath}", metadata.MediaFilePath);
        }
    }

    /// <summary>
    /// Gets the destination root directory path (parent of source root + "dst")
    /// </summary>
    private string GetDestinationRoot()
    {
        var parentDir = Path.GetDirectoryName(sourceRoot);
        return Path.Combine(parentDir!, "dst");
    }

    /// <summary>
    /// Ensures the specified directory exists, creating it only if it doesn't already exist
    /// </summary>
    private void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    /// <summary>
    /// Runs exiftool with the provided arguments
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
    /// Checks if the file extension is an image file
    /// </summary>
    private static bool IsImageFile(string extension)
    {
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp" };
        return imageExtensions.Contains(extension);
    }

    /// <summary>
    /// Checks if the file extension is a video file
    /// </summary>
    private static bool IsVideoFile(string extension)
    {
        var videoExtensions = new[] { ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mkv", ".m4v" };
        return videoExtensions.Contains(extension);
    }

    #endregion files
}

/// <summary>
/// Represents a metadata update that needs to be applied to a file
/// </summary>
public class MetadataUpdate
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime NewTimestamp { get; set; }
    public bool IsImage { get; set; }
}