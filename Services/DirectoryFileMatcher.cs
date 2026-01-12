using System.Text.Json;
using System.Text.RegularExpressions;
using GPhotosMetaFixer.Models;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// Matches media files to their corresponding JSON metadata files by reading the JSON content.
/// </summary>
public class DirectoryFileMatcher(ILogger logger)
{
    /// <summary>
    /// Media-Json file mapping (Key: MediaPath, Value: JsonPath)
    /// </summary>
    private Dictionary<string, string> MediaToJsonMapping { get; } = new();
    
    /// <summary>
    /// Files that were not matched to any JSON file
    /// </summary>
    public HashSet<string> FilesWithoutMatches { get; } = new();

    private Action<int>? _progressCallback;
    private int _processedCount;

    /// <summary>
    /// Recursively scans a directory and all its subdirectories for media files
    /// and matches them with JSON files based on the "title" field inside the JSON.
    /// </summary>
    /// <param name="directoryPath">The directory to scan</param>
    public Dictionary<string, string> ScanDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory {directoryPath} doesn't exist");
        }

        // Reset state for new scan
        MediaToJsonMapping.Clear();
        FilesWithoutMatches.Clear();

        logger.LogInformation("Scanning directory: {Directory}", directoryPath);

        // 1. Collect all files recursively
        var allFiles = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
        
        var jsonFiles = new List<string>();
        var mediaFileLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mediaFilesList = new List<string>();

        foreach (var file in allFiles)
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                jsonFiles.Add(file);
            }
            else
            {
                mediaFileLookup.Add(file);
                mediaFilesList.Add(file);
            }
        }

        logger.LogInformation("Found {JsonCount} JSON files and {MediaCount} media files", jsonFiles.Count, mediaFilesList.Count);

        // 2. Iterate over all JSON files
        foreach (var jsonPath in jsonFiles)
        {
            ProcessJsonFile(jsonPath, mediaFileLookup);
            
            // Report progress
            if (_progressCallback != null)
            {
                _processedCount++;
                _progressCallback(_processedCount);
            }
        }

        // 3. Identify orphan media files (files that exist but no JSON pointed to them)
        foreach (var mediaPath in mediaFilesList)
        {
            if (!MediaToJsonMapping.ContainsKey(mediaPath))
            {
                FilesWithoutMatches.Add(mediaPath);
                logger.LogWarning("Media file exists but no .json file pointed to it: {File}", Path.GetFileName(mediaPath));
            }
        }

        logger.LogInformation("Scan complete. Matched {MatchCount} files. Found {OrphanCount} orphan media files.", 
            MediaToJsonMapping.Count, FilesWithoutMatches.Count);

        return MediaToJsonMapping;
    }

    /// <summary>
    /// Progress-enabled scan. Reports processed JSON count via callback.
    /// </summary>
    public Dictionary<string, string> ScanDirectory(string directoryPath, Action<int> progressCallback)
    {
        _progressCallback = progressCallback;
        _processedCount = 0;
        try
        {
            return ScanDirectory(directoryPath);
        }
        finally
        {
            _progressCallback = null;
            _processedCount = 0;
        }
    }

    private void ProcessJsonFile(string jsonPath, HashSet<string> availableMediaFiles)
    {
        try
        {
            var jsonContent = File.ReadAllText(jsonPath);
            var metadata = JsonSerializer.Deserialize<GooglePhotosMetadata>(jsonContent);

            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Title))
            {
                logger.LogWarning("Invalid or missing metadata/title in JSON file: {JsonPath}", jsonPath);
                return;
            }

            var title = metadata.Title;
            var jsonDirectory = Path.GetDirectoryName(jsonPath);
            
            if (string.IsNullOrEmpty(jsonDirectory)) return;

            // Handle potential duplicates: check if json filename has a (1), (2) etc suffix
            // e.g. PXL_20250505_171634665.jpg.supplemental-metada(1).json
            var duplicateMatch = Regex.Match(Path.GetFileName(jsonPath), @"\.supplemental-metada(\(\d+\))\.json$", RegexOptions.IgnoreCase);
            var duplicateSuffix = duplicateMatch.Success ? duplicateMatch.Groups[1].Value : string.Empty;

            var targetMediaName = title;

            // If we found a duplicate suffix in the JSON filename, try to apply it to the title
            if (!string.IsNullOrEmpty(duplicateSuffix))
            {
                // Logic: Insert the suffix before the extension
                var extension = Path.GetExtension(title);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(title);
                var titleWithSuffix = $"{nameWithoutExt}{duplicateSuffix}{extension}";

                // Check if this suffixed file exists
                var expectedSuffixedPath = Path.Combine(jsonDirectory, titleWithSuffix);
                if (availableMediaFiles.Contains(expectedSuffixedPath))
                {
                    targetMediaName = titleWithSuffix;
                    logger.LogInformation("Matched duplicate JSON {JsonFile} to duplicate media {MediaFile}", 
                        Path.GetFileName(jsonPath), titleWithSuffix);
                }
            }

            // Construct the expected media file path.
            var expectedMediaPath = Path.Combine(jsonDirectory, targetMediaName);
            expectedMediaPath = Path.GetFullPath(expectedMediaPath); // Normalize

            if (availableMediaFiles.Contains(expectedMediaPath))
            {
                RegisterMatch(expectedMediaPath, jsonPath);

                // Handle Edited Files (e.g., IMG_123.jpg exists, check for IMG_123-edited.jpg)
                // This applies the SAME metadata to the edited version
                var extension = Path.GetExtension(targetMediaName);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(targetMediaName);
                var editedFileName = $"{nameWithoutExt}-edited{extension}";
                var expectedEditedPath = Path.Combine(jsonDirectory, editedFileName);
                expectedEditedPath = Path.GetFullPath(expectedEditedPath);

                if (availableMediaFiles.Contains(expectedEditedPath))
                {
                    logger.LogInformation("Found edited version of media: {EditedFile}. Applying same metadata.", editedFileName);
                    RegisterMatch(expectedEditedPath, jsonPath);
                }
            }
            else
            {
                // Can't find the media file .json is pointing to
                logger.LogWarning("MISSING MEDIA: JSON points to missing media file.\n" +
                                  "JSON: {JsonFile}\n" +
                                  "Target Media: {MediaTitle}", 
                                  Path.GetFileName(jsonPath), 
                                  targetMediaName);
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse JSON file: {JsonPath}", jsonPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing JSON file: {JsonPath}", jsonPath);
        }
    }

    private void RegisterMatch(string mediaPath, string jsonPath)
    {
        if (MediaToJsonMapping.TryGetValue(mediaPath, out var existingJsonPath))
        {
            logger.LogWarning("CONFLICT: Two .json files point to the exact same media file.\n" +
                              "Media File: {MediaFile}\n" +
                              "JSON 1: {Json1}\n" +
                              "JSON 2: {Json2}", 
                              Path.GetFileName(mediaPath), 
                              Path.GetFileName(existingJsonPath), 
                              Path.GetFileName(jsonPath));
        }
        else
        {
            MediaToJsonMapping[mediaPath] = jsonPath;
        }
    }
}
