import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'package:swiftride_mobile/src/trip_api.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'auth_test.dart' show MemoryStore, tokens;

void main() {
  test('create trip sends its idempotency key and parses the snapshot', () async {
    late http.Request sent;
    final api = createApi((request) async {
      sent = request;
      return response(snapshot());
    });
    addTearDown(api.auth.dispose);

    final trip = await api.createTrip(
      request: const {
        'pickupAddress': 'Pickup',
        'pickupLatitude': 10.1,
        'pickupLongitude': 106.1,
        'destinationAddress': 'Destination',
        'destinationLatitude': 10.2,
        'destinationLongitude': 106.2,
        'vehicleType': 'Motorbike',
      },
      idempotencyKey: 'same-request-key',
    );

    expect(sent.method, 'POST');
    expect(sent.url.path, '/api/trips');
    expect(sent.headers['Idempotency-Key'], 'same-request-key');
    expect(jsonDecode(sent.body)['pickupAddress'], 'Pickup');
    expect(trip.id, 'trip-1');
    expect(trip.status, TripStatus.awaitingRiderApproval);
    expect(trip.currentProposal?.driverName, 'Minh Anh');
    expect(trip.currentProposal?.amount, 35000);
  });

  test('current and route treat 204 as no content', () async {
    final api = createApi((_) async => http.Response('', 204));
    addTearDown(api.auth.dispose);

    expect(await api.current(), isNull);
    expect(await api.route('trip-1'), isNull);
  });

  test('401 refresh retries a create with the same idempotency key', () async {
    var creates = 0;
    var refreshes = 0;
    final api = createApi((request) async {
      if (request.url.path == '/api/auth/refresh') {
        refreshes++;
        return response(tokens());
      }
      creates++;
      expect(request.headers['Idempotency-Key'], 'stable-key');
      return creates == 1
          ? response({'title': 'unauthorized'}, 401)
          : response(snapshot());
    });
    addTearDown(api.auth.dispose);

    await api.createTrip(
      request: const {'pickupAddress': 'Pickup'},
      idempotencyKey: 'stable-key',
    );

    expect(creates, 2);
    expect(refreshes, 1);
  });

  test('history reads the server page and preserves terminal statuses', () async {
    final api = createApi(
      (_) async => response({
        'page': 1,
        'pageSize': 20,
        'total': 1,
        'items': [snapshot(status: 'Paid')],
      }),
    );
    addTearDown(api.auth.dispose);

    final history = await api.history();

    expect(history.total, 1);
    expect(history.items.single.status, TripStatus.paid);
  });

  test('proposal actions carry the current state version', () async {
    late http.Request sent;
    final api = createApi((request) async {
      sent = request;
      return response(snapshot(status: 'AwaitingDriverResponse'));
    });
    addTearDown(api.auth.dispose);
    final trip = TripSnapshot.fromJson(snapshot());
    final proposal = trip.currentProposal!;

    await api.approve(trip, proposal);

    expect(sent.method, 'POST');
    expect(
      sent.url.path,
      '/api/trips/trip-1/proposals/proposal-1/approve',
    );
    expect(jsonDecode(sent.body), {'stateVersion': 4});
  });

  test('trip route decodes coordinates in latitude-longitude order', () {
    final route = TripRouteLeg(
      polyline: '_p~iF~ps|U_ulLnnqC_mqNvxq`@',
      distanceMeters: 1000,
      durationSeconds: 240,
    );

    final points = route.decode();
    expect(points, hasLength(3));
    expect(points[0].latitude, 38.5);
    expect(points[0].longitude, -120.2);
    expect(points[1].latitude, 40.7);
    expect(points[1].longitude, -120.95);
    expect(points[2].latitude, 43.252);
    expect(points[2].longitude, -126.453);
  });

  test('idempotency keys are RFC 4122 version 4 UUIDs', () {
    expect(
      newIdempotencyKey(),
      matches(
        RegExp(
          r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$',
        ),
      ),
    );
  });
}

TripApi createApi(Future<http.Response> Function(http.Request) handler) {
  final auth = AuthApi(
    role: AppRole.rider,
    baseUrl: 'http://test',
    store: MemoryStore(),
    client: MockClient(handler),
  )..session = AuthSession.fromJson(tokens());
  return TripApi(auth);
}

http.Response response(Object data, [int status = 200]) => http.Response(
  jsonEncode(data),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> snapshot({String status = 'AwaitingRiderApproval'}) => {
  'id': 'trip-1',
  'riderId': 'rider-1',
  'pickupAddress': 'Pickup',
  'pickupLatitude': 10.1,
  'pickupLongitude': 106.1,
  'destinationAddress': 'Destination',
  'destinationLatitude': 10.2,
  'destinationLongitude': 106.2,
  'vehicleType': 'Motorbike',
  'status': status,
  'stateVersion': 4,
  'createdAt': '2026-11-07T08:00:00Z',
  'updatedAt': '2026-11-07T08:01:00Z',
  'proposals': [
    {
      'id': 'proposal-1',
      'driverId': 'driver-1',
      'status': status == 'AwaitingDriverResponse'
          ? 'AwaitingDriverResponse'
          : 'AwaitingRiderApproval',
      'amount': 35000,
      'currency': 'VND',
      'distanceMeters': 4800,
      'estimatedDurationSeconds': 900,
      'riderApprovalDeadline': '2026-11-07T08:02:30Z',
      'driverResponseDeadline': '2026-11-07T08:03:00Z',
      'driverNameSnapshot': 'Minh Anh',
      'plateNumberSnapshot': '59A1-12345',
      'vehicleNameSnapshot': 'Honda Wave Alpha',
    },
  ],
};
