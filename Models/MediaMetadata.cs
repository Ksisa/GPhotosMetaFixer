namespace GPhotosMetaFixer.Models;

/// <summary>
/// Represents metadata extracted from both media files and their corresponding JSON files
/// </summary>
public class MediaMetadata
{
    /// <summary>
    /// Timestamp extracted from the media file (EXIF data for images, file system for videos)
    /// </summary>
    public DateTime? MediaTimestamp { get; set; }

    /// <summary>
    /// Geolocation data extracted from the media file (EXIF GPS data)
    /// </summary>
    public GeoLocation? MediaGeolocation { get; set; }

    /// <summary>
    /// Timestamp extracted from the Google Photos JSON metadata file
    /// </summary>
    public DateTime? JsonTimestamp { get; set; }

    /// <summary>
    /// Geolocation data extracted from the Google Photos JSON metadata file
    /// </summary>
    public GeoLocation? JsonGeolocation { get; set; }

    /// <summary>
    /// The file path of the media file
    /// </summary>
    public string MediaFilePath { get; set; } = string.Empty;

    /// <summary>
    /// The file path of the JSON metadata file
    /// </summary>
    public string JsonFilePath { get; set; } = string.Empty;
}

/// <summary>
/// Represents geolocation data with latitude and longitude
/// </summary>
public class GeoLocation
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Altitude { get; set; }
}
