namespace SwiftRide.Matching.Api;

internal static class RouteDeviation
{
    public static bool IsOffRoute(string encoded, double latitude, double longitude)
    {
        const double metersPerDegree = 111_195;
        const double limit = 60;
        var longitudeScale = metersPerDegree * Math.Cos(latitude * Math.PI / 180);
        var lat = 0;
        var lon = 0;
        var index = 0;
        var previousX = 0d;
        var previousY = 0d;
        var hasPrevious = false;
        while (index < encoded.Length)
        {
            if (!ReadDelta(encoded, ref index, out var dLat) || !ReadDelta(encoded, ref index, out var dLon))
                return true;
            lat += dLat;
            lon += dLon;
            var x = (lon / 100_000d - longitude) * longitudeScale;
            var y = (lat / 100_000d - latitude) * metersPerDegree;
            var dx = x - previousX;
            var dy = y - previousY;
            var length = dx * dx + dy * dy;
            var fraction = hasPrevious && length > 0 ? Math.Clamp(-(previousX * dx + previousY * dy) / length, 0, 1) : 1;
            var nearestX = previousX + dx * fraction;
            var nearestY = previousY + dy * fraction;
            if (nearestX * nearestX + nearestY * nearestY <= limit * limit) return false;
            previousX = x;
            previousY = y;
            hasPrevious = true;
        }
        return true;
    }

    private static bool ReadDelta(string encoded, ref int index, out int delta)
    {
        delta = 0;
        var value = 0;
        for (var shift = 0; shift < 30 && index < encoded.Length; shift += 5)
        {
            var chunk = encoded[index++] - 63;
            if (chunk is < 0 or > 63) return false;
            value |= (chunk & 31) << shift;
            if (chunk < 32)
            {
                delta = (value & 1) == 0 ? value >> 1 : ~(value >> 1);
                return true;
            }
        }
        return false;
    }
}
