using GPhotosMetaFixer.Models;
using GPhotosMetaFixer.Options;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// A clean, focused metadata fixer that handles file operations and metadata corrections with batch processing
/// </summary>
public class NewMetadataFixer(ILogger logger, FileManager fileManager, ApplicationOptions options)
{
    private readonly ILogger logger = logger;
    private readonly List<MetadataUpdate> pendingUpdates = new();
    private readonly FileManager fileManager = fileManager;
    private readonly ApplicationOptions options = options;

    /// <summary>
    /// Gets the count of pending metadata updates
    /// </summary>
    public int PendingUpdatesCount => pendingUpdates.Count;

    /// <summary>
    /// Fixes metadata by copying files to destination directory with proper structure
    /// </summary>
    /// <param name="metadata">The media metadata containing source file path</param>
    public void FixMetadata(MediaMetadata metadata)
    {
        // Determine which JSON timestamp to use
        // Prioritize PhotoTakenTime, fallback to CreationTime
        metadata.JsonTimestamp = metadata.JsonPhotoTakenTime ?? metadata.JsonCreationTime;

        // Apply business rules to determine if metadata should be updated
        if (ShouldSkipMetadataUpdate(metadata))
        {
            return;
        }

        // Add to batch processing queue for EXIF metadata update
        QueueMetadataUpdate(metadata);
    }

    /// <summary>
    /// Processes all pending metadata updates in batches for optimal performance
    /// </summary>
    public void ProcessPendingUpdates(ProgressDisplay? progress = null)
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

        progress?.StartStep("EXIF Processing", pendingUpdates.Count);

        // Process images and videos in parallel batches
        ProcessFileTypeBatch(imageUpdates, "image", progress);
        ProcessFileTypeBatch(videoUpdates, "video", progress);

        progress?.CompleteStep();
        pendingUpdates.Clear();
    }

    /// <summary>
    /// Processes a batch of files of a specific type (image or video)
    /// </summary>
    private void ProcessFileTypeBatch(List<MetadataUpdate> updates, string fileType, ProgressDisplay? progress)
    {
        if (updates.Count == 0) return;

        logger.LogDebug("Processing {Count} {FileType} files in parallel", updates.Count, fileType);

        var processedCount = 0;
        var lockObject = new object();

        Parallel.ForEach(updates, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, update => {
            ProcessFileMetadata(update, fileType);
            
            // Simple thread-safe progress reporting
            if (progress != null)
            {
                lock (lockObject)
                {
                    processedCount++;
                    progress.Report(processedCount);
                }
            }
        });

        logger.LogDebug("Completed processing {Count} {FileType} files", updates.Count, fileType);
    }

    /// <summary>
    /// Processes a single file's metadata using exiftool
    /// </summary>
    private void ProcessFileMetadata(MetadataUpdate update, string fileType)
    {
        try
        {
            var timestamp = update.NewTimestamp.ToLocalTime().ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            var fileName = Path.GetFileName(update.FilePath);
            
            if (options.DryRun)
            {
                logger.LogDebug("[DRY RUN] Would update {FileType} metadata for: {FileName} to {Timestamp} (Geo: {HasGeo})", 
                    fileType, fileName, timestamp, update.Geolocation != null);
                return;
            }
            
            var args = BuildExifToolArguments(update.FilePath, timestamp, fileType, update.Geolocation);
            
            if (RunExifTool(args, out var stdOut, out var stdErr))
            {
                logger.LogDebug("Updated {FileType} metadata for: {FileName}", fileType, fileName);
            }
            else
            {
                logger.LogWarning("Failed to update {FileType} metadata for: {FileName}. Error: {StdErr}. Output: {StdOut}", 
                    fileType, fileName, stdErr, stdOut);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing {FileType} file: {FilePath}", fileType, update.FilePath);
        }
    }

    /// <summary>
    /// Determines if metadata update should be skipped based on business rules
    /// </summary>
    private bool ShouldSkipMetadataUpdate(MediaMetadata metadata)
    {
        // Rule 1: Both timestamps missing
        if (!metadata.MediaTimestamp.HasValue && !metadata.JsonTimestamp.HasValue)
        {
            logger.LogError("Both media and JSON timestamp metadata missing for {FileName}", Path.GetFileName(metadata.MediaFilePath));
            return true;
        }

        // Rule 2: Media missing and JSON is recent (within 12 hours)
        if (!metadata.MediaTimestamp.HasValue && metadata.JsonTimestamp.HasValue)
        {
            var twelveHoursAgo = DateTime.UtcNow.AddHours(-12);
            if (metadata.JsonTimestamp.Value > twelveHoursAgo)
            {
                logger.LogError("Media timestamp missing and JSON timestamp is recent for {FileName}. JSON timestamp: {JsonTimestamp}", 
                    Path.GetFileName(metadata.MediaFilePath), metadata.JsonTimestamp.Value);
                return true;
            }
        }

        return false; // Should proceed with update
    }

    /// <summary>
    /// Queues a metadata update for batch processing
    /// </summary>
    private void QueueMetadataUpdate(MediaMetadata metadata)
    {
        if (!metadata.JsonTimestamp.HasValue) return;

        var destinationPath = fileManager.GetDestinationPath(metadata.MediaFilePath);
        var extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        
        // Determine if we should update geolocation
        // Logic: Update if JSON has geo data AND media file does NOT have geo data
        GeoLocation? geoToUpdate = null;
        if (metadata.JsonGeolocation != null && metadata.MediaGeolocation == null)
        {
            geoToUpdate = metadata.JsonGeolocation;
        }

        pendingUpdates.Add(new MetadataUpdate
        {
            FilePath = destinationPath,
            NewTimestamp = metadata.JsonTimestamp.Value,
            IsImage = IsImageFile(extension),
            Geolocation = geoToUpdate
        });

        // Update file creation date to match JSON timestamp
        fileManager.UpdateFileCreationDate(destinationPath, metadata.JsonTimestamp.Value);
    }

    #region ExifTool Operations

    /// <summary>
    /// Builds exiftool arguments for the specified file type and timestamp
    /// </summary>
    private static string BuildExifToolArguments(string filePath, string timestamp, string fileType, GeoLocation? location)
    {
        var baseArgs = $"-overwrite_original -q -n -P";
        var dateArgs = "";
        var geoArgs = "";

        if (fileType == "image")
        {
            dateArgs = $"-DateTimeOriginal=\"{timestamp}\" -CreateDate=\"{timestamp}\" -ModifyDate=\"{timestamp}\"";
        }
        else
        {
            dateArgs = $"-MediaCreateDate=\"{timestamp}\" -CreateDate=\"{timestamp}\" -ModifyDate=\"{timestamp}\" -TrackCreateDate=\"{timestamp}\" -TrackModifyDate=\"{timestamp}\" -MediaModifyDate=\"{timestamp}\"";
        }

        if (location != null)
        {
            // Note: ExifTool handles negative values correctly for lat/long ref if passed as numbers
            // But strict standard requires Ref tags (N/S, E/W). ExifTool's smart handling usually works with simple assignment.
            // For robustness, we assign to both the value and the Ref tag logic implicitly by just setting the main tag with the signed value.
            // ExifTool is smart enough to set the Ref tags automatically when provided with signed coordinates.
            geoArgs = $"-GPSLatitude={location.Latitude} -GPSLongitude={location.Longitude}";
            if (location.Altitude.HasValue)
            {
                geoArgs += $" -GPSAltitude={location.Altitude.Value}";
            }
        }

        return $"{baseArgs} {dateArgs} {geoArgs} \"{filePath}\"";
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

    #endregion ExifTool Operations
}

/// <summary>
/// Represents a metadata update that needs to be applied to a file
/// </summary>
public class MetadataUpdate
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime NewTimestamp { get; set; }
    public GeoLocation? Geolocation { get; set; }
    public bool IsImage { get; set; }
}
