using GPhotosMetaFixer.Models;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// A clean, focused metadata fixer that handles file operations and metadata corrections
/// </summary>
public class NewMetadataFixer(string sourceRoot, ILogger logger)
{

    /// <summary>
    /// Copies a media file to the destination directory while preserving the directory structure
    /// </summary>
    /// <param name="metadata">The media metadata containing source file path</param>
    /// <returns>The destination file path if successful, null if failed</returns>
    public string? CopyFileWithDirectoryStructure(MediaMetadata metadata)
    {
        try
        {
            var sourcePath = metadata.MediaFilePath;
            
            // Validate source file exists
            if (!File.Exists(sourcePath))
            {
                logger.LogWarning("Source file does not exist: {SourcePath}", sourcePath);
                return null;
            }

            // Go up one level from source root and create dst folder
            var parentDir = Path.GetDirectoryName(sourceRoot);
            var destinationRoot = Path.Combine(parentDir!, "dst");
            Directory.CreateDirectory(destinationRoot);

            // Calculate relative path from source root
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);

            // Ensure destination directory exists
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // Copy the file
            File.Copy(sourcePath, destinationPath, overwrite: true);
            
            logger.LogDebug("Copied file: {SourcePath} -> {DestinationPath}", sourcePath, destinationPath);
            return destinationPath;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to copy file: {SourcePath}", metadata.MediaFilePath);
            return null;
        }
    }

    /// <summary>
    /// Copies both the media file and its corresponding JSON file while preserving directory structure
    /// </summary>
    /// <param name="metadata">The media metadata containing both file paths</param>
    /// <returns>The destination path of the media file, or null if copy failed</returns>
    public string? CopyMediaAndJsonFiles(MediaMetadata metadata)
    {
        // Copy media file
        var mediaResult = CopyFileWithDirectoryStructure(metadata);

        // Copy JSON file if it exists
        if (!string.IsNullOrEmpty(metadata.JsonFilePath) && File.Exists(metadata.JsonFilePath))
        {
            var jsonMetadata = new MediaMetadata { MediaFilePath = metadata.JsonFilePath };
            CopyFileWithDirectoryStructure(jsonMetadata);
        }

        return mediaResult;
    }
}
