using System.Text.Json;
using System.Text.RegularExpressions;
using GPhotosMetaFixer.Models;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// Matches media files to their corresponding JSON metadata files by reading the JSON content.
/// Uses a four-pass algorithm:
/// 1. Standard Pass - processes standard JSONs (direct title match)
/// 2. Edited Pass - matches unmatched -edited media to their original's JSON
/// 3. Duplicate Pass - processes (1), (2) JSONs (handles suffix logic)
/// 4. Truncation Fallback Pass - prefix matching for truncated filenames
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

    /// <summary>
    /// JSONs that couldn't find their media files (for truncation fallback)
    /// </summary>
    private HashSet<string> UnmatchedJsons { get; } = new();

    private Action<int>? _progressCallback;
    private int _processedCount;

    // Regex to detect duplicate JSON suffix: (1).json, (2).json, etc. at the END of filename
    private static readonly Regex DuplicateJsonSuffixRegex = new(@"(\(\d+\))\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    // Regex to detect (N) suffix in any filename
    private static readonly Regex DuplicateSuffixRegex = new(@"\((\d+)\)$", RegexOptions.Compiled);

    /// <summary>
    /// Recursively scans a directory and matches media files with JSON metadata.
    /// </summary>
    public Dictionary<string, string> ScanDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory {directoryPath} doesn't exist");
        }

        // Reset state
        MediaToJsonMapping.Clear();
        FilesWithoutMatches.Clear();
        UnmatchedJsons.Clear();

        logger.LogInformation("Scanning directory: {Directory}", directoryPath);

        // Collect all files
        var allFiles = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
        
        // Categorize JSONs only (not media)
        var standardJsons = new List<string>();
        var duplicateJsons = new List<string>();
        var allMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var skippedNoExtension = 0;
        foreach (var file in allFiles)
        {
            var extension = Path.GetExtension(file);
            
            // Skip files without extensions (e.g., auxiliary files alongside motion images)
            if (string.IsNullOrEmpty(extension))
            {
                skippedNoExtension++;
                continue;
            }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                // Only check for duplicate suffix at the end: (1).json, (2).json, etc.
                if (DuplicateJsonSuffixRegex.IsMatch(fileName))
                    duplicateJsons.Add(file);
                else
                    standardJsons.Add(file);
            }
            else
            {
                allMedia.Add(file);
            }
        }
        
        if (skippedNoExtension > 0)
        {
            logger.LogDebug("Skipped {Count} files without extensions", skippedNoExtension);
        }

        logger.LogInformation("Found {JsonCount} JSON files ({Standard} standard, {Duplicate} duplicate) and {MediaCount} media files", 
            standardJsons.Count + duplicateJsons.Count, standardJsons.Count, duplicateJsons.Count, allMedia.Count);

        // === PASS 1: Standard JSONs (direct title match) ===
        logger.LogDebug("Pass 1: Processing {Count} standard JSONs...", standardJsons.Count);
        foreach (var jsonPath in standardJsons)
        {
            ProcessStandardJson(jsonPath, allMedia);
            ReportProgress();
        }

        // === PASS 2: Edited media fallback ===
        logger.LogDebug("Pass 2: Matching unmatched edited files to original's JSON...");
        MatchEditedMediaToOriginalJson(allMedia);

        // === PASS 3: Duplicate JSONs (suffix handling) ===
        logger.LogDebug("Pass 3: Processing {Count} duplicate JSONs...", duplicateJsons.Count);
        foreach (var jsonPath in duplicateJsons)
        {
            ProcessDuplicateJson(jsonPath, allMedia);
            ReportProgress();
        }

        // === PASS 4: Truncation Fallback (prefix matching) ===
        logger.LogDebug("Pass 4: Attempting prefix matching for {Count} unmatched JSONs...", UnmatchedJsons.Count);
        ProcessTruncationFallback(allMedia);

        // Identify orphan media files
        foreach (var mediaPath in allMedia)
        {
            if (!MediaToJsonMapping.ContainsKey(mediaPath))
            {
                FilesWithoutMatches.Add(mediaPath);
                logger.LogWarning("ORPHAN MEDIA: No .json file pointed to this media file:\n{File}", mediaPath);
            }
        }

        logger.LogInformation("Scan complete. Matched {MatchCount} files. Found {OrphanCount} orphan media files.", 
            MediaToJsonMapping.Count, FilesWithoutMatches.Count);

        return MediaToJsonMapping;
    }

    /// <summary>
    /// Progress-enabled scan.
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

    private void ReportProgress()
    {
        if (_progressCallback != null)
        {
            _processedCount++;
            _progressCallback(_processedCount);
        }
    }

    #region Pass 1: Standard JSONs

    /// <summary>
    /// Processes a standard JSON file (no duplicate suffix).
    /// Matches the title directly to a media file.
    /// </summary>
    private void ProcessStandardJson(string jsonPath, HashSet<string> allMedia)
    {
        var title = ReadJsonTitle(jsonPath);
        if (title == null) return;

        var jsonDirectory = Path.GetDirectoryName(jsonPath);
        if (string.IsNullOrEmpty(jsonDirectory)) return;

        var expectedMediaPath = Path.GetFullPath(Path.Combine(jsonDirectory, title));

        if (allMedia.Contains(expectedMediaPath))
        {
            RegisterMatch(expectedMediaPath, jsonPath);
        }
        else
        {
            // Track for truncation fallback pass
            UnmatchedJsons.Add(jsonPath);
        }
    }

    #endregion

    #region Pass 2: Edited Media Fallback

    /// <summary>
    /// For unmatched media files that contain "-edited", try to match them
    /// to their original file's JSON.
    /// </summary>
    private void MatchEditedMediaToOriginalJson(HashSet<string> allMedia)
    {
        foreach (var mediaPath in allMedia)
        {
            if (MediaToJsonMapping.ContainsKey(mediaPath))
                continue; // Already matched

            var fileName = Path.GetFileName(mediaPath);
            
            // Check if this file has "-edited" in the name (before extension)
            if (!fileName.Contains("-edited", StringComparison.OrdinalIgnoreCase))
                continue;

            var directory = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrEmpty(directory)) continue;

            // Try to find the original file path by removing "-edited"
            var originalPath = GetOriginalPathFromEdited(mediaPath);
            if (originalPath == null) continue;

            // Check if the original file has a JSON match
            if (MediaToJsonMapping.TryGetValue(originalPath, out var originalJsonPath))
            {
                logger.LogInformation("Matched edited file using original's JSON:\n" +
                                      "Edited: {EditedFile}\n" +
                                      "Original JSON: {JsonFile}", 
                                      mediaPath, originalJsonPath);
                MediaToJsonMapping[mediaPath] = originalJsonPath;
            }
        }
    }

    /// <summary>
    /// Given an edited file path, returns the presumed original file path.
    /// </summary>
    private static string? GetOriginalPathFromEdited(string editedPath)
    {
        var directory = Path.GetDirectoryName(editedPath);
        var fileName = Path.GetFileName(editedPath);
        
        if (string.IsNullOrEmpty(directory)) return null;

        var editedIndex = fileName.IndexOf("-edited", StringComparison.OrdinalIgnoreCase);
        if (editedIndex < 0) return null;

        var originalFileName = fileName.Remove(editedIndex, 7);
        return Path.GetFullPath(Path.Combine(directory, originalFileName));
    }

    #endregion

    #region Pass 3: Duplicate JSONs

    /// <summary>
    /// Processes a duplicate JSON file (has (1), (2), etc. suffix before .json).
    /// Handles multiple strategies:
    /// 1. If title already ends with the same suffix, match directly
    /// 2. Otherwise, insert suffix into title
    /// 3. Fallback: try matching by JSON filename pattern
    /// </summary>
    private void ProcessDuplicateJson(string jsonPath, HashSet<string> allMedia)
    {
        var title = ReadJsonTitle(jsonPath);
        if (title == null) return;

        var jsonDirectory = Path.GetDirectoryName(jsonPath);
        if (string.IsNullOrEmpty(jsonDirectory)) return;

        // Extract the duplicate suffix from the JSON filename
        var jsonFileName = Path.GetFileName(jsonPath);
        var suffixMatch = DuplicateJsonSuffixRegex.Match(jsonFileName);
        if (!suffixMatch.Success)
        {
            logger.LogWarning("Could not extract duplicate suffix from JSON: {JsonFile}", jsonPath);
            return;
        }
        var duplicateSuffix = suffixMatch.Groups[1].Value; // e.g., "(1)"

        var titleExtension = Path.GetExtension(title);
        var titleWithoutExt = Path.GetFileNameWithoutExtension(title);

        // Strategy 1: Check if title already ends with this suffix
        // e.g., title = "Photo(1).jpg" and JSON has (1) suffix - don't add another (1)
        if (titleWithoutExt.EndsWith(duplicateSuffix))
        {
            var directMediaPath = Path.GetFullPath(Path.Combine(jsonDirectory, title));
            if (allMedia.Contains(directMediaPath) && !MediaToJsonMapping.ContainsKey(directMediaPath))
            {
                RegisterMatch(directMediaPath, jsonPath);
                return;
            }
        }

        // Strategy 2: Insert suffix into title before extension
        var expectedMediaName = $"{titleWithoutExt}{duplicateSuffix}{titleExtension}";
        var expectedMediaPath = Path.GetFullPath(Path.Combine(jsonDirectory, expectedMediaName));

        if (allMedia.Contains(expectedMediaPath) && !MediaToJsonMapping.ContainsKey(expectedMediaPath))
        {
            RegisterMatch(expectedMediaPath, jsonPath);
            return;
        }

        // Strategy 3: Fallback - try matching by JSON filename pattern
        // JSON: "Photo(1).json" -> try "Photo(1).jpg" (using title's extension)
        var jsonBaseName = Path.GetFileNameWithoutExtension(jsonPath); // e.g., "Photo(1)"
        var jsonBasedMediaName = jsonBaseName + titleExtension;
        var jsonBasedMediaPath = Path.GetFullPath(Path.Combine(jsonDirectory, jsonBasedMediaName));

        if (allMedia.Contains(jsonBasedMediaPath) && !MediaToJsonMapping.ContainsKey(jsonBasedMediaPath))
        {
            logger.LogInformation("Matched duplicate JSON using filename pattern:\n" +
                                  "JSON: {JsonFile}\n" +
                                  "Media: {MediaFile}",
                                  jsonPath, jsonBasedMediaPath);
            RegisterMatch(jsonBasedMediaPath, jsonPath);
            return;
        }

        // Track for truncation fallback pass
        UnmatchedJsons.Add(jsonPath);
    }

    #endregion

    #region Pass 4: Truncation Fallback

    /// <summary>
    /// For JSONs that couldn't find their media via exact match, try prefix matching.
    /// This handles cases where Google Photos truncated the media filename differently than the JSON.
    /// </summary>
    private void ProcessTruncationFallback(HashSet<string> allMedia)
    {
        foreach (var jsonPath in UnmatchedJsons.ToList())
        {
            var jsonDirectory = Path.GetDirectoryName(jsonPath);
            if (string.IsNullOrEmpty(jsonDirectory)) continue;

            // Get the JSON filename without extension and without any (N) suffix
            var jsonFileName = Path.GetFileName(jsonPath);
            var prefix = GetJsonPrefix(jsonFileName);
            
            if (string.IsNullOrEmpty(prefix)) continue;

            // Find all media files in the same directory that start with this prefix
            var candidates = allMedia
                .Where(m => 
                    Path.GetDirectoryName(m)?.Equals(jsonDirectory, StringComparison.OrdinalIgnoreCase) == true &&
                    Path.GetFileName(m).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !MediaToJsonMapping.ContainsKey(m))
                .ToList();

            if (candidates.Count == 1)
            {
                // Exactly one match - use it
                logger.LogInformation("Matched via prefix fallback:\n" +
                                      "JSON: {JsonFile}\n" +
                                      "Media: {MediaFile}\n" +
                                      "Prefix: {Prefix}",
                                      jsonPath, candidates[0], prefix);
                RegisterMatch(candidates[0], jsonPath);
                UnmatchedJsons.Remove(jsonPath);
            }
            else if (candidates.Count > 1)
            {
                // Multiple matches - log for manual review but try to match all unmatched ones
                logger.LogWarning("PREFIX MATCH: Multiple media files match JSON prefix.\n" +
                                  "JSON: {JsonFile}\n" +
                                  "Prefix: {Prefix}\n" +
                                  "Candidates: {Candidates}",
                                  jsonPath, prefix, string.Join(", ", candidates.Select(Path.GetFileName)));
                
                // Match all unmatched candidates to this JSON
                foreach (var candidate in candidates)
                {
                    if (!MediaToJsonMapping.ContainsKey(candidate))
                    {
                        RegisterMatch(candidate, jsonPath);
                    }
                }
                UnmatchedJsons.Remove(jsonPath);
            }
            else
            {
                // No matches found
                var title = ReadJsonTitle(jsonPath);
                logger.LogWarning("MISSING MEDIA: No media file found for JSON (even with prefix matching).\n" +
                                  "JSON: {JsonFile}\n" +
                                  "Title: {Title}\n" +
                                  "Prefix tried: {Prefix}",
                                  jsonPath, title ?? "(could not read)", prefix);
            }
        }
    }

    /// <summary>
    /// Extracts the prefix from a JSON filename for truncation matching.
    /// Removes .json extension and any (N) suffix.
    /// </summary>
    private static string GetJsonPrefix(string jsonFileName)
    {
        // Remove .json extension
        var withoutExt = jsonFileName;
        if (withoutExt.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            withoutExt = withoutExt[..^5];
        }

        // Remove any (N) suffix at the end
        var suffixMatch = DuplicateSuffixRegex.Match(withoutExt);
        if (suffixMatch.Success)
        {
            withoutExt = withoutExt[..^suffixMatch.Length];
        }

        return withoutExt;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Reads and returns the title from a JSON metadata file.
    /// </summary>
    private string? ReadJsonTitle(string jsonPath)
    {
        try
        {
            var jsonContent = File.ReadAllText(jsonPath);
            var metadata = JsonSerializer.Deserialize<GooglePhotosMetadata>(jsonContent);

            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Title))
            {
                logger.LogWarning("Invalid or missing metadata/title in JSON file: {JsonPath}", jsonPath);
                return null;
            }

            return metadata.Title;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse JSON file: {JsonPath}", jsonPath);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading JSON file: {JsonPath}", jsonPath);
            return null;
        }
    }

    /// <summary>
    /// Registers a match between a media file and a JSON file.
    /// </summary>
    private void RegisterMatch(string mediaPath, string jsonPath)
    {
        if (MediaToJsonMapping.TryGetValue(mediaPath, out var existingJsonPath))
        {
            logger.LogWarning("CONFLICT: Two .json files point to the exact same media file.\n" +
                              "Media File: {MediaFile}\n" +
                              "JSON 1: {Json1}\n" +
                              "JSON 2: {Json2}", 
                              mediaPath, existingJsonPath, jsonPath);
        }
        else
        {
            MediaToJsonMapping[mediaPath] = jsonPath;
        }
    }

    #endregion
}
