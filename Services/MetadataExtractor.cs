using GPhotosMetaFixer.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using GPhotosGeoLocation = GPhotosMetaFixer.Models.GeoLocation;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// Extracts metadata from media files and their corresponding Google Photos JSON files
/// </summary>
public class MetadataExtractor(ILogger logger)
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mkv", ".m4v"];

    /// <summary>
    /// Extracts metadata from both the media file and its corresponding JSON file
    /// </summary>
    public MediaMetadata ExtractMetadata(string mediaFilePath, string jsonFilePath)
    {
        var metadata = new MediaMetadata
        {
            MediaFilePath = mediaFilePath,
            JsonFilePath = jsonFilePath
        };

        SafeExecute(() => ExtractMediaFileMetadata(metadata), "media file", mediaFilePath);
        SafeExecute(() => ExtractJsonMetadata(metadata), "JSON file", jsonFilePath);

        return metadata;
    }

    /// <summary>
    /// Helper method to safely execute operations with error handling
    /// </summary>
    private void SafeExecute(Action action, string fileType, string filePath)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to extract metadata from {FileType} {FilePath}: {Error}", 
                fileType, filePath, ex.Message);
        }
    }

    /// <summary>
    /// Extracts metadata from the media file (EXIF for images, QuickTime for videos)
    /// </summary>
    private void ExtractMediaFileMetadata(MediaMetadata metadata)
    {
        var extension = Path.GetExtension(metadata.MediaFilePath).ToLowerInvariant();
        
        if (ImageExtensions.Contains(extension))
            ExtractImageMetadata(metadata);
        else if (VideoExtensions.Contains(extension))
            ExtractVideoMetadata(metadata);
        else
            logger.LogWarning("Unsupported file type: {FilePath}", metadata.MediaFilePath);
    }

    /// <summary>
    /// Extracts EXIF metadata from image files
    /// </summary>
    private void ExtractImageMetadata(MediaMetadata metadata)
    {
        var directories = ImageMetadataReader.ReadMetadata(metadata.MediaFilePath);
        
        // Extract timestamp from EXIF
        var exifDir = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (exifDir?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var timestamp) == true ||
            exifDir?.TryGetDateTime(ExifDirectoryBase.TagDateTime, out timestamp) == true)
        {
            if (IsValidTimestamp(timestamp))
            {
                metadata.MediaTimestamp = timestamp;
            }
        }

        // Extract GPS data from EXIF
        var gpsDir = directories.OfType<GpsDirectory>().FirstOrDefault();
        var location = gpsDir?.GetGeoLocation();
        if (location != null)
        {
            metadata.MediaGeolocation = new GPhotosGeoLocation
            {
                Latitude = location.Latitude,
                Longitude = location.Longitude
            };
        }
    }

    /// <summary>
    /// Extracts metadata from video files (QuickTime metadata for MP4 files)
    /// </summary>
    private void ExtractVideoMetadata(MediaMetadata metadata)
    {
        var directories = ImageMetadataReader.ReadMetadata(metadata.MediaFilePath);
        
        // Extract timestamp from QuickTime metadata
        var header = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        if (header?.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var timestamp) == true ||
            header?.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagModified, out timestamp) == true)
        {
            if (IsValidTimestamp(timestamp))
            {
                metadata.MediaTimestamp = timestamp;
            }
        }
        
        // Extract GPS data from QuickTime metadata
        var metaDir = directories.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();
        var gpsLocation = metaDir?.GetString(QuickTimeMetadataHeaderDirectory.TagGpsLocation);
        if (!string.IsNullOrEmpty(gpsLocation))
        {
            metadata.MediaGeolocation = ParseQuickTimeGpsLocation(gpsLocation);
        }
        
        // Fallback to file creation time (only if it's valid)
        if (!metadata.MediaTimestamp.HasValue)
        {
            var creationTime = new FileInfo(metadata.MediaFilePath).CreationTimeUtc;
            if (IsValidTimestamp(creationTime))
            {
                metadata.MediaTimestamp = creationTime;
            }
        }
    }

    /// <summary>
    /// Extracts metadata from the Google Photos JSON file
    /// </summary>
    private void ExtractJsonMetadata(MediaMetadata metadata)
    {
        var jsonContent = File.ReadAllText(metadata.JsonFilePath);
        var googlePhotosData = JsonSerializer.Deserialize<GooglePhotosMetadata>(jsonContent);

        if (googlePhotosData == null)
        {
            logger.LogWarning("Failed to deserialize JSON file: {FilePath}", metadata.JsonFilePath);
            return;
        }

        // Extract timestamp from JSON
        metadata.JsonTimestamp = googlePhotosData.PhotoTakenTime?.ToDateTime() ?? 
                                googlePhotosData.CreationTime?.ToDateTime();

        // Extract geolocation from JSON
        var geoData = googlePhotosData.GeoData;
        if (geoData != null && IsValidGeoLocation(geoData.Latitude, geoData.Longitude))
        {
            metadata.JsonGeolocation = new GPhotosGeoLocation
            {
                Latitude = geoData.Latitude!.Value,
                Longitude = geoData.Longitude!.Value,
                Altitude = geoData.Altitude
            };
        }
    }

    /// <summary>
    /// Validates if a timestamp is valid (not a default/invalid value)
    /// </summary>
    private static bool IsValidTimestamp(DateTime timestamp)
    {
        // Exclude common default/invalid timestamps
        var invalidTimestamps = new[]
        {
            new DateTime(1904, 1, 1), // Common default timestamp
            new DateTime(1970, 1, 1), // Unix epoch (often used as default)
            new DateTime(1980, 1, 1), // Another common default
            new DateTime(2000, 1, 1), // Y2K default
            DateTime.MinValue,
            DateTime.MaxValue
        };

        return !invalidTimestamps.Contains(timestamp.Date) && 
               timestamp.Year >= 1990 && // Reasonable minimum year for digital photos
               timestamp.Year <= DateTime.Now.Year + 1; // Allow up to next year (timezone issues)
    }

    /// <summary>
    /// Validates if geolocation coordinates are within valid ranges
    /// </summary>
    private static bool IsValidGeoLocation(double? latitude, double? longitude)
    {
        return latitude.HasValue && longitude.HasValue &&
               latitude >= -90 && latitude <= 90 &&
               longitude >= -180 && longitude <= 180 &&
               latitude != 0 && longitude != 0; // Exclude 0,0 as it's often a default/invalid value
    }

    /// <summary>
    /// Parses QuickTime GPS location string format: "+51.4858-0.0514/"
    /// </summary>
    private static GPhotosGeoLocation? ParseQuickTimeGpsLocation(string gpsLocation)
    {
        try
        {
            var cleanLocation = gpsLocation.TrimEnd('/');
            var matches = Regex.Matches(cleanLocation, @"([+-]?\d+\.?\d*)");
            
            if (matches.Count >= 2 && 
                double.TryParse(matches[0].Value, out var latitude) && 
                double.TryParse(matches[1].Value, out var longitude))
            {
                return new GPhotosGeoLocation { Latitude = latitude, Longitude = longitude };
            }
        }
        catch { }
        
        return null;
    }
}


