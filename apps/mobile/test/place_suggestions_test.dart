import 'package:flutter_test/flutter_test.dart';
import 'package:maplibre_gl/maplibre_gl.dart';
import 'package:swiftride_mobile/src/ride_locations.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'trip_api_test.dart' show snapshot;

void main() {
  test(
    'recent locations take priority and API predictions fill unique slots',
    () {
      const recent = RidePlace('Nhà', 'Nhà, Quận 1', LatLng(10.77, 106.69));
      const duplicate = RidePlace('Nhà', 'Nhà, Quận 1', null, 'duplicate');
      const first = RidePlace('Chợ', 'Chợ, Quận 1', null, 'market');
      const second = RidePlace('Trường', 'Trường, Quận 3', null, 'school');
      const extra = RidePlace('Công viên', 'Công viên, Quận 1', null, 'park');
      final result = suggestedPlaces(
        [recent],
        [duplicate, first, second, extra],
      );
      expect(result.map((place) => place.name), ['Nhà', 'Chợ', 'Trường']);
      expect(result.first.isResolved, isTrue);
      expect(result[1].placeId, 'market');
    },
  );
  test(
    'ranks completed trips in the last seven days by frequency then recency',
    () {
      final now = DateTime.utc(2026, 11, 10, 12);
      TripSnapshot trip(
        String address,
        DateTime at, {
        String status = 'Paid',
        double lat = 10.1,
      }) {
        final data = snapshot(status: status)
          ..['createdAt'] = at.toIso8601String()
          ..['pickupAddress'] = address
          ..['pickupLatitude'] = lat;
        return TripSnapshot.fromJson(data);
      }

      final trips = [
        trip('Phổ biến', now.subtract(const Duration(days: 6))),
        trip('Gần đây', now.subtract(const Duration(hours: 1)), lat: 10.2),
        trip('Phổ biến', now.subtract(const Duration(days: 2))),
        trip('Quá cũ', now.subtract(const Duration(days: 8)), lat: 10.3),
        trip(
          'Đã hủy',
          now.subtract(const Duration(hours: 2)),
          status: 'Cancelled',
          lat: 10.4,
        ),
      ];
      final result = rankRecentTripPlaces(trips, pickup: true, now: now);
      expect(result.map((place) => place.name), ['Phổ biến', 'Gần đây']);
      expect(
        suggestedPlaces(result, const [
          RidePlace('Gợi ý', 'Gợi ý', null, 'id'),
        ]).length,
        3,
      );
    },
  );
}
