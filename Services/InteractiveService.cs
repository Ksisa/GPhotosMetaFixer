using GPhotosMetaFixer.Options;
using Microsoft.Extensions.Logging;

namespace GPhotosMetaFixer.Services;

public class InteractiveService
{
    private readonly ILogger _logger;

    public InteractiveService(ILogger logger)
    {
        _logger = logger;
    }
    public ApplicationOptions GatherUserOptions()
    {
        var options = new ApplicationOptions();
        
        _logger.LogInformation("=== Google Photos Metadata Fixer ===");
        _logger.LogInformation("This tool will help you fix metadata issues in your Google Photos export.");
        _logger.LogInformation("");

        // 1. Source folder
        options.SourceFolder = GetSourceFolder();
        _logger.LogInformation("");

        // 2. Destination folder
        options.DestinationFolder = GetDestinationFolder();
        _logger.LogInformation("");

        // 3. Timestamp approximation
        options.KeepExistingTimestamp = GetTimestampPreference();
        _logger.LogInformation("");

        // 4. Dry run confirmation
        options.DryRun = GetDryRunPreference();
        _logger.LogInformation("");

        return options;
    }

    private string GetSourceFolder()
    {
        while (true)
        {
            _logger.LogInformation("1. Source folder");
            _logger.LogInformation("   Provide the path of the source \"Takeout\" folder:");
            Console.Write("   > ");
            
            var input = Console.ReadLine()?.Trim();
            
            if (string.IsNullOrEmpty(input))
            {
                _logger.LogWarning("   ❌ Please provide a valid path.");
                continue;
            }

            if (!Directory.Exists(input))
            {
                _logger.LogWarning("   ❌ Directory does not exist: {Input}", input);
                continue;
            }

            _logger.LogInformation("   ✅ Source folder: {Input}", input);
            return input;
        }
    }

    private string GetDestinationFolder()
    {
        while (true)
        {
            _logger.LogInformation("2. Destination folder");
            _logger.LogInformation("   Provide the path of the destination folder:");
            _logger.LogInformation("   (This is where the fixed files will be saved)");
            Console.Write("   > ");
            
            var input = Console.ReadLine()?.Trim();
            
            if (string.IsNullOrEmpty(input))
            {
                _logger.LogWarning("   ❌ Please provide a valid path.");
                continue;
            }

            // Create directory if it doesn't exist
            try
            {
                Directory.CreateDirectory(input);
                _logger.LogInformation("   ✅ Destination folder: {Input}", input);
                return input;
            }
            catch (Exception ex)
            {
                _logger.LogError("Cannot create directory: {ErrorMessage}", ex.Message);
                continue;
            }
        }
    }

    private bool GetTimestampPreference()
    {
        while (true)
        {
            _logger.LogInformation("3. Timestamp approximation");
            _logger.LogInformation("   If the timestamp in the media file and the metadata file are within 2h of each other,");
            _logger.LogInformation("   do you want to:");
            _logger.LogInformation("   A) Keep it as it was (recommended for phone photos)");
            _logger.LogInformation("   B) Overwrite it with the metadata file (better for DSLR uploads)");
            _logger.LogInformation("");
            _logger.LogInformation("   Choose A or B (default: A):");
            Console.Write("   > ");
            
            var input = Console.ReadLine()?.Trim().ToUpper();
            
            if (string.IsNullOrEmpty(input) || input == "A")
            {
                _logger.LogInformation("   ✅ Will keep existing timestamps when within 2 hours");
                return true;
            }
            else if (input == "B")
            {
                _logger.LogInformation("   ✅ Will overwrite with metadata timestamps");
                return false;
            }
            else
            {
                _logger.LogWarning("   ❌ Please enter A or B");
                continue;
            }
        }
    }

    private bool GetDryRunPreference()
    {
        while (true)
        {
            _logger.LogInformation("4. Safety check");
            _logger.LogInformation("   Do you want to run in 'dry-run' mode first?");
            _logger.LogInformation("   (This will analyze files without making changes - RECOMMENDED)");
            _logger.LogInformation("   Y) Yes, dry-run first (recommended)");
            _logger.LogInformation("   N) No, make changes directly");
            _logger.LogInformation("");
            _logger.LogInformation("   Choose Y or N (default: Y):");
            Console.Write("   > ");
            
            var input = Console.ReadLine()?.Trim().ToUpper();
            
            if (string.IsNullOrEmpty(input) || input == "Y")
            {
                _logger.LogInformation("   ✅ Will run in dry-run mode (safe)");
                return true;
            }
            else if (input == "N")
            {
                _logger.LogWarning("   ⚠️  Will make changes directly to files");
                return false;
            }
            else
            {
                _logger.LogWarning("   ❌ Please enter Y or N");
                continue;
            }
        }
    }


    public void ShowSummary(ApplicationOptions options)
    {
        _logger.LogInformation("");
        _logger.LogInformation("=== Configuration Summary ===");
        _logger.LogInformation("Source folder: {SourceFolder}", options.SourceFolder);
        _logger.LogInformation("Destination folder: {DestinationFolder}", options.DestinationFolder);
        _logger.LogInformation("Timestamp preference: {TimestampPreference}", options.KeepExistingTimestamp ? "Keep existing (A)" : "Overwrite with metadata (B)");
        _logger.LogInformation("Mode: {Mode}", options.DryRun ? "Dry-run (safe)" : "Direct changes");
        _logger.LogInformation("");
        
        if (!options.DryRun)
        {
            _logger.LogWarning("⚠️  WARNING: You are about to make changes to your files!");
            _logger.LogWarning("   Make sure you have backups before proceeding.");
            _logger.LogInformation("");
        }
        
        _logger.LogInformation("Press Enter to continue or Ctrl+C to cancel...");
        Console.ReadLine();
    }
}
