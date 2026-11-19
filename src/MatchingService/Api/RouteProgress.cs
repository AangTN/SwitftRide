namespace SwiftRide.Matching.Api;

public static class RouteProgress
{
    public static double? RemainingFraction(string encoded, double latitude, double longitude)
    {
        var points = new List<(double X, double Y)>();
        var index = 0; var lat = 0; var lon = 0;
        bool Read(out int delta)
        {
            var value = 0;
            for (var shift = 0; shift < 30 && index < encoded.Length; shift += 5)
            {
                var c = encoded[index++] - 63;
                if (c is < 0 or > 63) break;
                value |= (c & 31) << shift;
                if (c < 32) { delta = (value & 1) == 0 ? value >> 1 : ~(value >> 1); return true; }
            }
            delta = 0; return false;
        }
        var scale = 111195 * Math.Cos(latitude * Math.PI / 180);
        while (index < encoded.Length)
        {
            if (!Read(out var dlat) || !Read(out var dlon)) return null;
            lat += dlat; lon += dlon;
            points.Add(((lon / 100000d - longitude) * scale, (lat / 100000d - latitude) * 111195));
        }
        if (points.Count < 2) return null;
        double total = 0, before = 0, nearest = double.MaxValue, at = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i]; var dx = b.X - a.X; var dy = b.Y - a.Y;
            var squared = dx * dx + dy * dy; var length = Math.Sqrt(squared);
            var fraction = squared == 0 ? 0 : Math.Clamp(-(a.X * dx + a.Y * dy) / squared, 0, 1);
            var x = a.X + fraction * dx; var y = a.Y + fraction * dy; var distance = x * x + y * y;
            if (distance < nearest) { nearest = distance; at = before + length * fraction; }
            before += length; total += length;
        }
        return total > 0 && nearest <= 60 * 60 ? Math.Clamp((total - at) / total, 0, 1) : null;
    }
}
