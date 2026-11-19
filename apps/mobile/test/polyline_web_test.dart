import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/trip_models.dart';

void main() {
  test('Goong polyline negative deltas retain signed coordinates', () {
    const leg = TripRouteLeg(
      polyline: r'{xw`AyagjSHFz@p@zBjBfEjDtBfB^n@hApBNXv@vA|@~ArBeA',
      distanceMeters: 700,
      durationSeconds: 180,
    );
    final points = leg.decode();
    expect(points.length, 12);
    expect(points.first.latitude, closeTo(10.77662, 0.000001));
    expect(points.first.longitude, closeTo(106.70125, 0.000001));
    expect(points.last.latitude, closeTo(10.77228, 0.000001));
    expect(points.last.longitude, closeTo(106.69753, 0.000001));
  });
}
