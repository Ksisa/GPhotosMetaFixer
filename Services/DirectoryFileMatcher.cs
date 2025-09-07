using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// This just makes passing it as an argument easier
/// </summary>
public record FileMatchHelpers(
    string filePath,
    string fileNameWithExt,
    string fileNameWithoutExt,
    string directory)
{ }

public class DirectoryFileMatcher(ILogger logger)
{
    /// <summary>
    /// Media-Json file mapping
    /// </summary>
    private Dictionary<string, string> MediaToJsonMapping { get; } = new();
    public HashSet<string> FilesWithoutMatches { get; } = new();

    /// <summary>
    /// Set of .json files in a directory
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _directoryJsonCache = new();
    private Action<int>? _progressCallback;
    private int _processedCount;

    /// <summary>
    /// As of writing this, it seems the metadata files have a 51 length limit
    /// <br/>(except for duplicates, which have 54 due to the (1) suffix)
    /// </summary>
    private const int MaxJsonFileNameLength = 46; // 51 - 5 for ".json"

    /// <summary>
    /// Recursively scans a directory and all its subdirectories for media files
    /// </summary>
    /// <param name="directoryPath">The directory to scan</param>
    public Dictionary<string, string> ScanDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory {directoryPath} doesn't exist");
        }

        var subdirectories = Directory.GetDirectories(directoryPath);
        foreach (var subdirectory in subdirectories)
        {
            ScanDirectory(subdirectory);
        }

        _directoryJsonCache.TryAdd(directoryPath, new HashSet<string>());

        var allFiles = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
        var mediaFiles = new List<string>();

        // Compile all .json files into a hashset first for faster lookups
        foreach (var file in allFiles)
        {
            if (file.EndsWith(".json")) { _directoryJsonCache[directoryPath].Add(file); }
            else mediaFiles.Add(file);
        }

        // All non-JSON files are considered media files
        // It will throw an error if that's not the case
        foreach (var mediaFile in mediaFiles)
        {
            CheckForJsonFile(mediaFile);
            // Report progress after each media file checked
            if (_progressCallback != null)
            {
                _processedCount++;
                _progressCallback(_processedCount);
            }
        }

        return MediaToJsonMapping;
    }

    /// <summary>
    /// Progress-enabled scan. Reports processed media count via callback.
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

    public void CheckForJsonFile(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (String.IsNullOrWhiteSpace(directory))
            return; 

        var helper = new FileMatchHelpers(filePath, Path.GetFileName(filePath), Path.GetFileNameWithoutExtension(filePath), directory);

        // Check if this is a motion image (MP4) that has a corresponding image file
        if (IsMotionImageWithCorrespondingImage(helper))
        {
            logger.LogInformation("Skipping motion image {FileName} - corresponding image file exists", helper.fileNameWithExt);
            return;
        }

        // -- UNMODIFIED JSON NAMES -- 
        if (TryHappyPathMatch(helper))
            return;

        if (TryDuplicateMatch(helper))
            return;

        if (TryEditedPhotoMatch(helper))
            return;

        // -- MODIFIED JSON NAMES -- 

        if (TryCharacterLimitMatch(helper))
            return;

        if (TryDuplicateCharacterLimitMatch(helper))
            return;

        if (TryEditedCharacterLimitMatch(helper))
            return;

        logger.LogError("No JSON file found for {FileName}, copying to destination as-is", helper.fileNameWithExt);

        // Track files without matches
        FilesWithoutMatches.Add(filePath);
    }

    #region Non-truncated algorithms

    /// <summary>
    /// Checks if a JSON file exists in the directory and adds it to mapping if found
    /// </summary>
    private bool TryMatchJsonFile(FileMatchHelpers file, string expectedJsonPath)
    {
        if (_directoryJsonCache[file.directory].Contains(expectedJsonPath))
        {
            MediaToJsonMapping[file.filePath] = expectedJsonPath;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Truncates a filename to fit within the JSON file name length limit
    /// </summary>
    private string TruncateForJsonLimit(string fileName)
    {
        if (fileName.Length <= MaxJsonFileNameLength)
            return fileName;
        
        return fileName[..MaxJsonFileNameLength];
    }

    

    private bool TryHappyPathMatch(FileMatchHelpers file)
    {
        // Happy path: IMG_1234.jpeg -> IMG_1234.jpeg.supplemental-metadata.json
        var jsonPath = Path.Combine(file.directory, $"{file.fileNameWithExt}.supplemental-metadata.json");
        return TryMatchJsonFile(file, jsonPath);
    }

    /// <summary>
    /// Checks if the given file is a motion image (MP4) that has a corresponding image file
    /// <br/>
    /// <b>WILL SKIP:</b> IMG_123.mp4 if IMG_123.jpg, IMG_123.jpeg, IMG_123.png, etc. exists
    /// </summary>
    /// <param name="file">The file helper containing file information</param>
    /// <returns>True if this is a motion image with a corresponding image file</returns>
    private bool IsMotionImageWithCorrespondingImage(FileMatchHelpers file)
    {
        // Only check MP4 files
        if (!file.fileNameWithExt.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return false;
        
        // Common image extensions to check for
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp" };
        
        foreach (var extension in imageExtensions)
        {
            var correspondingImagePath = Path.Combine(file.directory, $"{file.fileNameWithoutExt}{extension}");
            if (File.Exists(correspondingImagePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches duplicate files with (1) suffix pattern
    /// <br/>
    /// <b>WILL MATCH:</b> IMG_123(1).jpeg to IMG_123.jpeg.supplemental-metadata(1).json
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    private bool TryDuplicateMatch(FileMatchHelpers file)
    {
        // Check if this is a duplicate file with (1) suffix
        if (!file.fileNameWithoutExt.EndsWith("(1)"))
            return false;
        
        // Create the expected JSON filename with (1) suffix on the metadata part
        var expectedJsonFile = $"{file.fileNameWithExt.Replace("(1)", "")}.supplemental-metadata(1).json";
        var expectedJsonPath = Path.Combine(file.directory, expectedJsonFile);

        return TryMatchJsonFile(file, expectedJsonPath);
    }

    /// <summary>
    /// Matches edited photos by removing the -edited suffix and looking for the original JSON file
    /// <br/>
    /// <b>WILL MATCH:</b> IMG_123-edited.jpg to IMG_123.jpg.supplemental-metadata.json
    /// <br/>
    /// <param name="file"></param>
    /// <returns></returns>
    private bool TryEditedPhotoMatch(FileMatchHelpers file)
    {
        // Check if this is an edited file with -edited suffix
        if (!file.fileNameWithExt.Contains("-edited"))
            return false;

        // Remove the -edited suffix to get the original filename
        var originalFileName = file.fileNameWithExt.Replace("-edited", "");
        var expectedJsonFile = $"{originalFileName}.supplemental-metadata.json";
        var expectedJsonPath = Path.Combine(file.directory, expectedJsonFile);

        return TryMatchJsonFile(file, expectedJsonPath);
    }

    #endregion

    #region Truncated algorithms

    /// <summary>
    /// Matches files by truncating to 46 characters (51 char limit minus ".json")
    /// <br/>
    /// <b>WILL MATCH:</b> IMG_123456789012345678901234567890.jpeg to IMG_123456789012345678901234567890.jpeg.supplemental-metadata.json
    /// <br/>
    /// <b>WILL MATCH:</b> VeryLongFileNameThatExceedsTheCharacterLimit.jpeg to VeryLongFileNameThatExceedsTheCharacterLimit.jpeg.supplemental-metadata.json
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    private bool TryCharacterLimitMatch(FileMatchHelpers file)
    {
        // Create the expected JSON filename with full name first
        var fullJsonFile = $"{file.fileNameWithExt}.supplemental-metadata";
        var truncatedFileName = TruncateForJsonLimit(fullJsonFile);
        var expectedJsonFile = $"{truncatedFileName}.json";
        var expectedJsonPath = Path.Combine(file.directory, expectedJsonFile);

        return TryMatchJsonFile(file, expectedJsonPath);
    }

    /// <summary>
    /// Same as <see cref="TryCharacterLimitMatch(FileMatchHelpers)"/> except it handles the weird duplicate behavior
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    private bool TryDuplicateCharacterLimitMatch(FileMatchHelpers file)
    {
        // Remove (1) from fileNameWithoutExt
        var cleanFileNameWithExt = file.fileNameWithExt.Replace("(1)", "");
        var fullJsonFile = $"{cleanFileNameWithExt}.supplemental-metadata";
        var truncatedFileName = TruncateForJsonLimit(fullJsonFile);
        var expectedJsonFile = $"{truncatedFileName}(1).json";
        var expectedJsonPath = Path.Combine(file.directory, expectedJsonFile);

        return TryMatchJsonFile(file, expectedJsonPath);
    }

    /// <summary>
    /// Same as <see cref="TryCharacterLimitMatch(FileMatchHelpers)"/> except it handles the edited behavior
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    private bool TryEditedCharacterLimitMatch(FileMatchHelpers file)
    {
        // Check if this is an edited file with -edited suffix
        if (!file.fileNameWithExt.Contains("-edited"))
            return false;

        // Remove the -edited suffix to get the original filename
        var originalFileName = file.fileNameWithExt.Replace("-edited", "");
        var fullJsonFile = $"{originalFileName}.supplemental-metadata";
        var truncatedFileName = TruncateForJsonLimit(fullJsonFile);
        var expectedJsonFile = $"{truncatedFileName}.json";
        var expectedJsonPath = Path.Combine(file.directory, expectedJsonFile);

        return TryMatchJsonFile(file, expectedJsonPath);
    }
}
#endregion
