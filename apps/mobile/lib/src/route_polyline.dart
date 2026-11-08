/// Decode Goong's precision-five polyline without unsigned bitwise negation.
/// Dart compiled to JavaScript treats bitwise results as unsigned 32-bit values.
List<(double, double)> decodeRoutePolyline(String encoded) {
  var index = 0, latitude = 0, longitude = 0;
  int nextDelta() {
    var result = 0, shift = 0;
    while (index < encoded.length) {
      final value = encoded.codeUnitAt(index++) - 63;
      if (value < 0 || value > 63 || shift > 30) {
        throw const FormatException('Invalid route polyline.');
      }
      result |= (value & 31) << shift;
      shift += 5;
      if (value < 32) return result.isOdd ? -((result >> 1) + 1) : result >> 1;
    }
    throw const FormatException('Incomplete route polyline.');
  }

  final points = <(double, double)>[];
  while (index < encoded.length) {
    latitude += nextDelta();
    longitude += nextDelta();
    final lat = latitude / 1e5, lon = longitude / 1e5;
    if (!lat.isFinite ||
        !lon.isFinite ||
        lat < -90 ||
        lat > 90 ||
        lon < -180 ||
        lon > 180) {
      throw const FormatException('Invalid route coordinate.');
    }
    points.add((lat, lon));
  }
  return points;
}
