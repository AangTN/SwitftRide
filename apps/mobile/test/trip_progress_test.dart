import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'driver_home_test.dart' show driverTrip;

void main() {
  test('progress matches the active driver, proposal, stage and timestamp', () {
    final trip = driverTrip('PickedUp', 6);
    final data = <String, dynamic>{
      'tripId': trip.id,
      'driverId': trip.driverId,
      'proposalId': trip.currentProposal!.id,
      'stageVersion': 2,
      'recordedAt': '2026-01-01T12:00:00Z',
      'remainingMeters': 5000,
      'remainingSeconds': 600,
    };
    final progress = TripProgress.parse(data, trip, null)!;
    expect(progress.label, 'Còn khoảng 5.0 km · 10 phút');
    expect(TripProgress.parse(data, trip, progress), isNull);
    expect(
      TripProgress.parse({...data, 'stageVersion': 1}, trip, null),
      isNull,
    );
    expect(
      TripProgress.parse({...data, 'driverId': 'wrong'}, trip, null),
      isNull,
    );
    expect(
      TripProgress.parse({...data, 'remainingMeters': null}, trip, null)!.label,
      'Đang cập nhật tuyến đường',
    );
  });
}
