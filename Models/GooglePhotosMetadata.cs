using System.Text.Json.Serialization;

namespace GPhotosMetaFixer.Models;

public class GooglePhotosMetadata
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("imageViews")]
    public string? ImageViews { get; set; }

    [JsonPropertyName("creationTime")]
    public GooglePhotosTimestamp? CreationTime { get; set; }

    [JsonPropertyName("photoTakenTime")]
    public GooglePhotosTimestamp? PhotoTakenTime { get; set; }

    [JsonPropertyName("geoData")]
    public GooglePhotosGeoData? GeoData { get; set; }

    [JsonPropertyName("geoDataExif")]
    public GooglePhotosGeoData? GeoDataExif { get; set; }
}

public class GooglePhotosTimestamp
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("formatted")]
    public string? Formatted { get; set; }

    public DateTime ToDateTime()
    {
        if (string.IsNullOrEmpty(Timestamp))
            throw new InvalidOperationException("Timestamp is null or empty");

        if (long.TryParse(Timestamp, out long unixTimestamp))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).DateTime;
        }

        throw new InvalidOperationException($"Unable to parse timestamp: {Timestamp}");
    }
}

public class GooglePhotosGeoData
{
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("altitude")]
    public double? Altitude { get; set; }

    [JsonPropertyName("latitudeSpan")]
    public double? LatitudeSpan { get; set; }

    [JsonPropertyName("longitudeSpan")]
    public double? LongitudeSpan { get; set; }

    public bool IsValidLocation()
    {
        return Latitude.HasValue && Longitude.HasValue &&
               Latitude != 0 && Longitude != 0 &&
               Latitude >= -90 && Latitude <= 90 &&
               Longitude >= -180 && Longitude <= 180;
    }
}
