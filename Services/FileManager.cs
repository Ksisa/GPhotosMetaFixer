using GPhotosMetaFixer.Options;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// Handles file operations including copying files and preparing destination directory structure
/// </summary>
public class FileManager
{
    private readonly ILogger logger;
    private readonly ApplicationOptions options;

    public FileManager(ApplicationOptions options, ILogger logger)
    {
        this.options = options;
        this.logger = logger;
        PrepareDestinationDirectory();
    }

    /// <summary>
    /// Gets the destination root directory path
    /// </summary>
    public string DestinationRoot => GetDestinationRoot();

    /// <summary>
    /// Copies a file to the destination directory while preserving the directory structure
    /// </summary>
    /// <param name="sourceFilePath">The source file path</param>
    /// <returns>The destination file path if successful, null if failed</returns>
    public string? CopyFileToDestination(string sourceFilePath)
    {
        try
        {
            if (!File.Exists(sourceFilePath))
            {
                logger.LogWarning("Source file does not exist: {SourcePath}", sourceFilePath);
                return null;
            }

            var relativePath = Path.GetRelativePath(options.SourceFolder, sourceFilePath);
            var destinationPath = Path.Combine(DestinationRoot, relativePath);

            if (options.DryRun)
            {
                logger.LogDebug("[DRY RUN] Would copy file: {SourcePath} -> {DestinationPath}", sourceFilePath, destinationPath);
            }
            else
            {
                File.Copy(sourceFilePath, destinationPath, overwrite: true);
                logger.LogDebug("Copied file: {SourcePath} -> {DestinationPath}", sourceFilePath, destinationPath);
            }
            
            return destinationPath;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to copy file: {SourcePath}", sourceFilePath);
            return null;
        }
    }

    /// <summary>
    /// Updates the file creation date to match the specified timestamp
    /// </summary>
    /// <param name="filePath">The file path to update</param>
    /// <param name="timestamp">The new timestamp</param>
    public void UpdateFileCreationDate(string filePath, DateTime timestamp)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                logger.LogWarning("Destination file does not exist for creation date update: {FilePath}", filePath);
                return;
            }

            if (options.DryRun)
            {
                logger.LogDebug("[DRY RUN] Would update file timestamps for: {FilePath} to {Timestamp}", filePath, timestamp);
            }
            else
            {
                File.SetCreationTime(filePath, timestamp);
                File.SetLastWriteTime(filePath, timestamp);
                File.SetLastAccessTime(filePath, timestamp);
                logger.LogDebug("Updated file timestamps for: {FilePath} to {Timestamp}", filePath, timestamp);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update file creation date for: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Gets the destination path for a given source file path
    /// </summary>
    /// <param name="sourceFilePath">The source file path</param>
    /// <returns>The corresponding destination file path</returns>
    public string GetDestinationPath(string sourceFilePath)
    {
        var relativePath = Path.GetRelativePath(options.SourceFolder, sourceFilePath);
        return Path.Combine(DestinationRoot, relativePath);
    }

    /// <summary>
    /// Prepares the destination directory by recreating the entire directory structure from the source
    /// </summary>
    private void PrepareDestinationDirectory()
    {
        try
        {
            EnsureDirectoryExists(DestinationRoot);
            
            var sourceDirectories = Directory.GetDirectories(options.SourceFolder, "*", SearchOption.AllDirectories);
            foreach (var sourceDir in sourceDirectories)
            {
                var relativePath = Path.GetRelativePath(options.SourceFolder, sourceDir);
                var destinationDir = Path.Combine(DestinationRoot, relativePath);
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
    /// Gets the destination root directory path
    /// </summary>
    private string GetDestinationRoot()
    {
        // If destination folder is specified, use it; otherwise auto-generate
        if (!string.IsNullOrWhiteSpace(options.DestinationFolder))
        {
            return options.DestinationFolder;
        }
        
        // Auto-generate: parent of source root + "dst"
        var parentDir = Path.GetDirectoryName(options.SourceFolder);
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
}
