import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'package:swiftride_mobile/src/goong_map.dart';
import 'package:swiftride_mobile/src/ride_locations.dart';
import 'package:swiftride_mobile/src/rider_home.dart';
import 'package:swiftride_mobile/src/trip_api.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'package:swiftride_mobile/src/trip_realtime.dart';

import 'auth_test.dart' show MemoryStore, tokens;
import 'trip_api_test.dart' show snapshot;

void main() {
  Future<void> open(
    WidgetTester tester,
    FakeApi api,
    FakeRealtime realtime,
  ) async {
    tester.view.physicalSize = const Size(430, 932);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(api.auth.dispose);
    await tester.pumpWidget(
      MaterialApp(
        home: RiderHome(
          fullName: 'Rider',
          account: const Text('Account'),
          tripApi: api,
          realtime: realtime,
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('cash is default and completed trip returns to home', (
    tester,
  ) async {
    final api = FakeApi()..restored = trip('Paid', 9);
    await open(tester, api, FakeRealtime());

    expect(find.text('Chuyến đi hoàn tất'), findsOneWidget);
    await tester.tap(find.text('Về trang chủ'));
    await tester.pumpAndSettle();
    expect(find.text('Bạn muốn đi đâu?'), findsOneWidget);
    expect(find.text('Chuyến đi hoàn tất'), findsNothing);

    await tester.tap(find.text('Bạn muốn đi đâu?'));
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<RideLocationPicker>(find.byType(RideLocationPicker))
          .paymentMethod,
      RidePaymentMethod.cash,
    );
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
    'restores one subscription and receives driver GPS without periodic API polling',
    (tester) async {
      final api = FakeApi()..restored = trip('EnRoute', 7);
      final realtime = FakeRealtime();
      await open(tester, api, realtime);
      expect(realtime.watched, ['trip-1']);
      await tester.pump(const Duration(seconds: 15));
      expect(api.reads, 0);
      realtime.emit('DriverLocationUpdated', gps());
      await tester.pumpAndSettle();
      var map = tester.widget<GoongMap>(find.byType(GoongMap));
      expect(map.driverPoint!.latitude, 10.78);

      realtime.emit('DriverLocationUpdated', gps()..['proposalId'] = 'old');
      realtime.emit(
        'DriverLocationUpdated',
        gps()
          ..['latitude'] = 12.0
          ..['recordedAt'] = '2026-11-07T07:00:00Z',
      );
      await tester.pumpAndSettle();
      map = tester.widget<GoongMap>(find.byType(GoongMap));
      expect(map.driverPoint!.latitude, 10.78);

      realtime.emit('connected', {});
      await tester.pumpAndSettle();
      expect(api.reads, 1);
      await tester.pumpWidget(const SizedBox());
      expect(realtime.disposed, isTrue);
    },
  );

  testWidgets('a slow GET cannot roll back an approved proposal', (
    tester,
  ) async {
    final api = FakeApi()..restored = trip('AwaitingRiderApproval', 4);
    final realtime = FakeRealtime();
    await open(tester, api, realtime);
    final slow = Completer<TripSnapshot>();
    api.onGet = () => slow.future;
    realtime.emit('TripChanged', {'tripId': 'trip-1', 'stateVersion': 5});
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.textContaining('Đồng ý ·'));
    await tester.tap(find.textContaining('Đồng ý ·'));
    await tester.pumpAndSettle();
    expect(find.text('Chờ tài xế xác nhận'), findsOneWidget);
    slow.complete(trip('AwaitingRiderApproval', 4));
    await tester.pumpAndSettle();
    expect(find.text('Chờ tài xế xác nhận'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('route events and delayed GETs must match the current phase', (
    tester,
  ) async {
    final api = FakeApi()..restored = trip('EnRoute', 7);
    final realtime = FakeRealtime();
    await open(tester, api, realtime);
    final slow = Completer<TripRouteSnapshot?>();
    api.onRoute = () => slow.future;
    realtime.emit('connected', {});
    await tester.pumpAndSettle();
    api.restored = trip('PickedUp', 8);
    realtime.emit('TripChanged', {'tripId': 'trip-1', 'stateVersion': 8});
    await tester.pumpAndSettle();
    api.onRoute = () async => null;
    slow.complete(route('ToPickup', 1));
    await tester.pumpAndSettle();
    expect(find.text('Đang trên đường'), findsOneWidget);
    expect(tester.widget<GoongMap>(find.byType(GoongMap)).routePoints, isEmpty);
    realtime.emit('TripRoutesUpdated', routeJson('ToDestination', 2));
    await tester.pumpAndSettle();
    expect(
      tester.widget<GoongMap>(find.byType(GoongMap)).routePoints.length,
      3,
    );
    realtime.emit('TripRoutesUpdated', routeJson('ToPickup', 1));
    await tester.pumpAndSettle();
    expect(
      tester.widget<GoongMap>(find.byType(GoongMap)).routePoints.length,
      3,
    );
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
    'uncertain booking survives restart and retries the original body and key',
    (tester) async {
      final api = FakeApi();
      final pending = PendingTripBooking('original-key', {
        'pickupAddress': 'A',
        'destinationAddress': 'B',
        'vehicleType': 'Motorbike',
      });
      await api.savePending(pending);
      api.onCreate = () async => throw const AuthFailure('Network lost');
      await open(tester, api, FakeRealtime());
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('pickup-field')), findsNothing);
      await tester.tap(find.text('Thử lại yêu cầu đặt xe'));
      await tester.pumpAndSettle();
      expect(api.sentKey, pending.key);
      expect(api.sentBody, pending.request);
      expect(await api.readPending(), isNotNull);
      await tester.pumpWidget(const SizedBox());

      api.onCreate = () async => trip('Matching', 1);
      await open(tester, api, FakeRealtime());
      await tester.tap(find.text('Thử lại yêu cầu đặt xe'));
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pump();
      expect(api.sentKey, pending.key);
      expect(api.sentBody, pending.request);
      expect(await api.readPending(), isNull);
      expect(find.text('Đang tìm tài xế'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'active_trip_exists opens the existing trip and clears pending data',
    (tester) async {
      final api = FakeApi();
      await api.savePending(PendingTripBooking('key', {'pickupAddress': 'A'}));
      api.onCreate = () async =>
          throw const AuthFailure('Active', 'active_trip_exists', 'trip-1');
      await open(tester, api, FakeRealtime());
      api.restored = trip('EnRoute', 7);
      await tester.tap(find.text('Thử lại yêu cầu đặt xe'));
      await tester.pumpAndSettle();
      expect(find.text('Tài xế đang đến'), findsOneWidget);
      expect(await api.readPending(), isNull);
      await tester.pumpWidget(const SizedBox());
    },
  );
}

TripSnapshot trip(String status, int version) {
  final json = snapshot(status: status)..['stateVersion'] = version;
  json['driverId'] = status == 'EnRoute' || status == 'PickedUp'
      ? 'driver-1'
      : null;
  final proposal = (json['proposals'] as List).single as Map<String, dynamic>;
  proposal['status'] = status == 'EnRoute' || status == 'PickedUp'
      ? 'DriverAccepted'
      : status == 'Matching'
      ? 'Searching'
      : status;
  proposal['riderApprovalDeadline'] = DateTime.now()
      .add(const Duration(seconds: 90))
      .toIso8601String();
  return TripSnapshot.fromJson(json);
}

Map<String, dynamic> gps() => {
  'tripId': 'trip-1',
  'proposalId': 'proposal-1',
  'driverId': 'driver-1',
  'latitude': 10.78,
  'longitude': 106.69,
  'recordedAt': '2026-11-07T08:00:00Z',
};

Map<String, dynamic> routeJson(String stage, int version) => {
  'tripId': 'trip-1',
  'proposalId': 'proposal-1',
  'driverId': 'driver-1',
  'stage': stage,
  'stageVersion': version,
  stage == 'ToPickup' ? 'toPickup' : 'toDestination': {
    'polyline': '_p~iF~ps|U_ulLnnqC_mqNvxq`@',
    'distanceMeters': 1000,
    'durationSeconds': 300,
    'calculatedAt': '2026-11-07T08:00:00Z',
  },
};
TripRouteSnapshot route(String stage, int version) =>
    TripRouteSnapshot.fromJson(routeJson(stage, version));

class FakeRealtime implements TripRealtimeClient {
  final controller = StreamController<TripRealtimeEvent>.broadcast();
  final watched = <String?>[];
  bool disposed = false;
  void emit(String name, Map<String, dynamic> data) =>
      controller.add(TripRealtimeEvent(name, data));
  @override
  Stream<TripRealtimeEvent> get events => controller.stream;
  @override
  Future<void> watch(String? tripId) async {
    watched.add(tripId);
  }

  @override
  Future<void> dispose() async {
    disposed = true;
    await controller.close();
  }
}

class FakeApi extends TripApi {
  FakeApi()
    : super(
        AuthApi(role: AppRole.rider, store: MemoryStore())
          ..session = AuthSession.fromJson(tokens()),
        pendingStore: MemoryStore(),
      );
  TripSnapshot? restored;
  int reads = 0;
  String? sentKey;
  Map<String, Object>? sentBody;
  Future<TripSnapshot> Function()? onGet;
  Future<TripRouteSnapshot?> Function()? onRoute;
  Future<TripSnapshot> Function()? onCreate;
  @override
  Future<TripSnapshot?> current() async => restored;
  @override
  Future<TripSnapshot> get(String id) async {
    reads++;
    return onGet != null ? await onGet!() : restored!;
  }

  @override
  Future<TripRouteSnapshot?> route(String id) async =>
      onRoute == null ? null : await onRoute!();
  @override
  Future<TripSnapshot> approve(
    TripSnapshot value,
    TripProposalSnapshot proposal,
  ) async => trip('AwaitingDriverResponse', 5);
  @override
  Future<TripSnapshot> createTrip({
    required Map<String, Object> request,
    required String idempotencyKey,
  }) async {
    sentBody = request;
    sentKey = idempotencyKey;
    return await onCreate!();
  }
}
