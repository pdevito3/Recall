namespace Recall.Server.Domain.Sessions;

public sealed record CapturedLocation
{
    private const double MaxLatitude = 90;
    private const double MaxLongitude = 180;

    private CapturedLocation(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    public static CapturedLocation? Admit(bool captureEnabled, double? latitude, double? longitude)
    {
        if (!captureEnabled || latitude is not { } lat || longitude is not { } lng)
            return null;
        if (double.IsNaN(lat) || double.IsNaN(lng) || Math.Abs(lat) > MaxLatitude || Math.Abs(lng) > MaxLongitude)
            return null;

        return new CapturedLocation(lat, lng);
    }
}
