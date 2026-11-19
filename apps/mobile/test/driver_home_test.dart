import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'package:swiftride_mobile/src/driver_home.dart';
import 'package:swiftride_mobile/src/goong_map.dart';
import 'package:swiftride_mobile/src/trip_api.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'package:swiftride_mobile/src/trip_realtime.dart';
import 'package:swiftride_mobile/src/trip_widgets.dart';
import 'auth_test.dart' show MemoryStore;
import 'trip_api_test.dart' show snapshot;

TripSnapshot driverTrip(
  String status,
  int version, {
  String method = 'Wallet',
}) {
  final data = snapshot(status: status)..['stateVersion'] = version;
  data['paymentMethod'] = method;
  data['driverId'] = 'driver-1';
  data['riderNameSnapshot'] = 'Khách demo';
  data['riderPhoneSnapshot'] = '0900000001';
  final p = (data['proposals'] as List).single as Map<String, dynamic>;
  p['status'] = status == 'AwaitingDriverResponse' ? status : 'DriverAccepted';
  p['driverResponseDeadline'] = DateTime.now()
      .add(const Duration(seconds: 30))
      .toIso8601String();
  return TripSnapshot.fromJson(data);
}

class DriverApi extends TripApi {
  DriverApi() : super(AuthApi(role: AppRole.driver, store: MemoryStore()));
  TripSnapshot? currentTrip;
  Future<TripSnapshot> Function()? read;
  String stage = 'ToPickup';
  String availabilityStatus = 'Offline';
  int gpsUpdates = 0;
  String? lastStep;
  Map<String, Object?> earningsData = {'totalEarnings': 0, 'transactions': []};
  @override
  Future<void> updateGps(double latitude, double longitude) async {
    gpsUpdates++;
  }

  int restores = 0;
  @override
  Future<Map<String, dynamic>> driverAvailability() async => {
    'status': availabilityStatus,
  };
  @override
  Future<TripSnapshot?> current() async {
    restores++;
    return currentTrip;
  }

  @override
  Future<TripSnapshot> get(String id) async =>
      read == null ? currentTrip! : await read!();
  @override
  Future<TripHistoryPage> history({int page = 1, int pageSize = 20}) async =>
      const TripHistoryPage(total: 0, items: []);
  @override
  Future<Object?> earnings() async => earningsData;
  @override
  Future<TripRouteSnapshot?> route(String id) async =>
      TripRouteSnapshot.fromJson({
        'tripId': 'trip-1',
        'proposalId': 'proposal-1',
        'driverId': 'driver-1',
        'stage': stage,
        'stageVersion': stage == 'ToPickup' ? 1 : 2,
        'toPickup': {
          'polyline': '_p~iF~ps|U_ulLnnqC_mqNvxq`@',
          'distanceMeters': 1000,
          'durationSeconds': 240,
        },
        'toDestination': {
          'polyline': '_p~iF~ps|U_ulLnnqC',
          'distanceMeters': 4800,
          'durationSeconds': 900,
        },
        'pickupEtaMinutes': 4,
      });
  @override
  Future<TripSnapshot> driverAnswer(
    String id,
    String proposalId,
    int version, {
    required bool accept,
  }) async =>
      currentTrip = driverTrip(accept ? 'EnRoute' : 'Cancelled', version + 1);
  @override
  Future<TripSnapshot> driverStep(String id, int version, String step) async {
    lastStep = step;
    if (step == 'dropoff') {
      return currentTrip = driverTrip(
        'PaymentPending',
        version + 1,
        method: currentTrip!.paymentMethod,
      );
    }
    stage = 'ToDestination';
    return currentTrip = driverTrip('PickedUp', version + 1);
  }
}

class DriverRealtime implements TripRealtimeClient {
  final controller = StreamController<TripRealtimeEvent>.broadcast();
  final watched = <String?>[];
  @override
  Stream<TripRealtimeEvent> get events => controller.stream;
  @override
  Future<void> watch(String? id) async {
    watched.add(id);
  }

  void emit(String event, Map<String, dynamic> data) =>
      controller.add(TripRealtimeEvent(event, data));
  @override
  Future<void> dispose() => controller.close();
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUp(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
          const MethodChannel('flutter.baseflow.com/geolocator'),
          (call) async => 0,
        );
  });
  Future<void> open(
    WidgetTester t,
    DriverApi api,
    DriverRealtime realtime, {
    DateTime Function()? now,
  }) async {
    t.view.physicalSize = const Size(430, 932);
    t.view.devicePixelRatio = 1;
    addTearDown(t.view.resetPhysicalSize);
    addTearDown(t.view.resetDevicePixelRatio);
    addTearDown(api.auth.dispose);
    await t.pumpWidget(
      MaterialApp(
        home: DriverHome(
          api: api.auth,
          fullName: 'Driver',
          account: const Text('Account'),
          tripApi: api,
          realtime: realtime,
          now: now,
        ),
      ),
    );
    await t.pumpAndSettle();
  }

  testWidgets(
    'restored invitation has deadline but joins account only; expiry reloads current trip',
    (t) async {
      var now = DateTime.now();
      final api = DriverApi()
        ..currentTrip = driverTrip('AwaitingDriverResponse', 4);
      final realtime = DriverRealtime();
      await open(t, api, realtime, now: () => now);
      expect(find.text('Lời mời chuyến mới'), findsOneWidget);
      expect(find.text('Pickup'), findsOneWidget);
      expect(find.text('Destination'), findsOneWidget);
      expect(t.widget<Text>(find.text('Pickup')).maxLines, isNull);
      expect(t.widget<Text>(find.text('Destination')).maxLines, isNull);
      expect(find.byType(TripCountdown), findsOneWidget);
      expect(realtime.watched, [null]);
      api.currentTrip = null;
      now = now.add(const Duration(seconds: 31));
      await t.pump(const Duration(seconds: 1));
      await t.pumpAndSettle();
      expect(find.text('Bạn đang ngoại tuyến'), findsOneWidget);
      await t.pumpWidget(const SizedBox());
    },
  );
  testWidgets('realtime warning clears after reconnecting', (t) async {
    final api = DriverApi();
    final realtime = DriverRealtime();
    await open(t, api, realtime);

    realtime.emit('disconnected', {});
    await t.pumpAndSettle();
    expect(
      find.text('Mất kết nối cập nhật trực tiếp. Đang kết nối lại…'),
      findsOneWidget,
    );

    realtime.emit('connected', {});
    await t.pumpAndSettle();
    expect(
      find.text('Mất kết nối cập nhật trực tiếp. Đang kết nối lại…'),
      findsNothing,
    );
    await t.pumpWidget(const SizedBox());
  });
  testWidgets('income distinguishes cash held from net earnings and fees due', (
    t,
  ) async {
    final api = DriverApi()
      ..earningsData = {
        'totalEarnings': 170000,
        'tripCount': 2,
        'cashCollected': 100000,
        'cashNetEarnings': 85000,
        'nonCashEarnings': 85000,
        'cashCommissionDue': 15000,
        'transactions': [],
      };
    await open(t, api, DriverRealtime());
    await t.tap(find.text('Thu nhập'));
    await t.pumpAndSettle();
    expect(find.text('170.000 ₫'), findsOneWidget);
    expect(find.text('100.000 ₫'), findsOneWidget);
    expect(find.text('85.000 ₫'), findsNWidgets(2));
    expect(find.text('15.000 ₫'), findsOneWidget);
    expect(find.text('Phí tiền mặt cần nộp'), findsOneWidget);
    expect(t.takeException(), isNull);
    await t.pumpWidget(const SizedBox());
  });
  testWidgets(
    'cash dropoff confirms collection using the existing trip action',
    (t) async {
      final api = DriverApi()
        ..currentTrip = driverTrip('PickedUp', 6, method: 'Cash');
      await open(t, api, DriverRealtime());
      expect(find.text('Tiền mặt'), findsOneWidget);
      await t.ensureVisible(find.text('Đã trả khách và thu tiền'));
      await t.tap(find.text('Đã trả khách và thu tiền'));
      await t.pumpAndSettle();
      expect(api.lastStep, 'dropoff');
      expect(find.text('Chuyến đi hoàn tất'), findsOneWidget);
      expect(find.text('Tiền mặt'), findsOneWidget);
      await t.pumpWidget(const SizedBox());
    },
  );
  testWidgets('active trip sheet follows finger and collapses by dragging', (
    t,
  ) async {
    final api = DriverApi()..currentTrip = driverTrip('EnRoute', 5);
    await open(t, api, DriverRealtime());
    final title = find.text('Đang đến đón khách');
    final start = t.getTopLeft(title);
    expect(find.text('Điểm đón'), findsOneWidget);
    expect(find.text('Đã đón khách'), findsOneWidget);
    final gesture = await t.startGesture(t.getCenter(title));
    await gesture.moveBy(const Offset(0, 90));
    await t.pump();
    expect(t.getTopLeft(title).dy, closeTo(start.dy + 90, 1));
    await gesture.moveBy(const Offset(0, 400));
    await gesture.up();
    await t.pumpAndSettle();
    expect(find.text('Điểm đón'), findsNothing);
    expect(find.text('Đã đón khách'), findsNothing);
    expect(find.text('Đang đến đón khách'), findsOneWidget);
    await t.fling(title, const Offset(0, -300), 1000);
    await t.pumpAndSettle();
    expect(find.text('Điểm đón'), findsOneWidget);
    expect(find.text('Đã đón khách'), findsOneWidget);
    await t.pumpWidget(const SizedBox());
  });
  testWidgets('new driver invitation vibrates the phone once', (t) async {
    final methods = <String>[];
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, (call) async {
          methods.add(call.method);
          return null;
        });
    addTearDown(
      () => TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, null),
    );
    final api = DriverApi();
    final realtime = DriverRealtime();
    await open(t, api, realtime);
    api.currentTrip = driverTrip('AwaitingDriverResponse', 4);
    realtime.emit('DriverInvitation', {
      'tripId': 'trip-1',
      'proposalId': 'proposal-1',
      'expiresAt': DateTime.now()
          .add(const Duration(seconds: 30))
          .toIso8601String(),
    });
    await t.pumpAndSettle();
    expect(find.text('Lời mời chuyến mới'), findsOneWidget);
    expect(
      methods.where((method) => method == 'HapticFeedback.vibrate'),
      hasLength(1),
    );
    realtime.emit('DriverInvitation', {
      'tripId': 'trip-1',
      'proposalId': 'proposal-1',
      'expiresAt': DateTime.now()
          .add(const Duration(seconds: 30))
          .toIso8601String(),
    });
    await t.pumpAndSettle();
    expect(
      methods.where((method) => method == 'HapticFeedback.vibrate'),
      hasLength(1),
    );
    await t.pumpWidget(const SizedBox());
  });
  testWidgets('pickup switches map from pickup leg to destination leg', (
    t,
  ) async {
    final api = DriverApi()..currentTrip = driverTrip('EnRoute', 5);
    final realtime = DriverRealtime();
    await open(t, api, realtime);
    expect(t.widget<GoongMap>(find.byType(GoongMap)).routePoints.length, 3);
    await t.tap(find.text('Đã đón khách'));
    await t.pumpAndSettle();
    expect(find.text('Đang chở khách'), findsOneWidget);
    expect(t.widget<GoongMap>(find.byType(GoongMap)).routePoints.length, 2);
    expect(find.text('Tới điểm đến khoảng 15 phút'), findsOneWidget);
    await t.pumpWidget(const SizedBox());
  });
  testWidgets('driver can leave payment pending and receive next invitation', (
    t,
  ) async {
    final api = DriverApi()..currentTrip = driverTrip('PaymentPending', 9);
    final realtime = DriverRealtime();
    await open(t, api, realtime);
    expect(find.text('Chuyến đi hoàn tất'), findsOneWidget);
    api.currentTrip = null;
    await t.tap(find.text('Tiếp tục nhận chuyến'));
    await t.pumpAndSettle();
    expect(find.text('Bạn đang ngoại tuyến'), findsOneWidget);
    api.currentTrip = driverTrip('AwaitingDriverResponse', 10);
    realtime.emit('DriverInvitation', {
      'tripId': 'trip-1',
      'proposalId': 'proposal-1',
      'expiresAt': DateTime.now()
          .add(const Duration(seconds: 30))
          .toIso8601String(),
    });
    await t.pumpAndSettle();
    expect(find.text('Lời mời chuyến mới'), findsOneWidget);
    await t.pumpWidget(const SizedBox());
  });
  testWidgets('older snapshot cannot roll back accepted ride', (t) async {
    final api = DriverApi()..currentTrip = driverTrip('EnRoute', 7);
    final realtime = DriverRealtime();
    await open(t, api, realtime);
    api.read = () async => driverTrip('AwaitingDriverResponse', 4);
    realtime.emit('TripChanged', {'tripId': 'trip-1'});
    await t.pumpAndSettle();
    expect(find.text('Đang đến đón khách'), findsOneWidget);
    expect(find.text('Lời mời chuyến mới'), findsNothing);
    await t.pumpWidget(const SizedBox());
  });
  testWidgets(
    'stationary online driver sends heartbeat and stops after disposal',
    (t) async {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
            const MethodChannel('flutter.baseflow.com/geolocator'),
            (call) async {
              if (call.method == 'checkPermission') return 2;
              if (call.method == 'getCurrentPosition') {
                return {
                  'latitude': 10.78,
                  'longitude': 106.70,
                  'timestamp': DateTime.now().millisecondsSinceEpoch,
                  'accuracy': 5.0,
                  'altitude': 0.0,
                  'heading': 0.0,
                  'speed': 0.0,
                  'speed_accuracy': 0.0,
                };
              }
              return null;
            },
          );
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
            const MethodChannel('flutter.baseflow.com/geolocator_updates'),
            (call) async => null,
          );
      final api = DriverApi()..availabilityStatus = 'Available';
      await open(t, api, DriverRealtime());
      expect(find.text('Đang chờ chuyến'), findsOneWidget);
      final map = t.widget<GoongMap>(find.byType(GoongMap));
      expect(map.initialTarget?.latitude, 10.78);
      expect(map.initialTarget!.longitude, closeTo(106.70, 0.000001));
      expect(map.driverPoint?.latitude, 10.78);
      final initial = api.gpsUpdates;
      expect(initial, greaterThan(0));
      await t.pump(const Duration(seconds: 21));
      await t.pumpAndSettle();
      expect(api.gpsUpdates, greaterThan(initial));
      await t.pumpWidget(const SizedBox());
      final stopped = api.gpsUpdates;
      await t.pump(const Duration(seconds: 40));
      expect(api.gpsUpdates, stopped);
    },
  );
  testWidgets(
    'accepting invitation immediately exposes pickup action without another departure button',
    (t) async {
      final api = DriverApi()
        ..currentTrip = driverTrip('AwaitingDriverResponse', 4);
      await open(t, api, DriverRealtime());
      await t.ensureVisible(find.text('Nhận chuyến'));
      await t.tap(find.text('Nhận chuyến'));
      await t.pumpAndSettle();
      expect(find.text('Đang đến đón khách'), findsOneWidget);
      expect(find.text('Đã đón khách'), findsOneWidget);
      expect(find.text('Bắt đầu đến đón'), findsNothing);
      await t.pumpWidget(const SizedBox());
    },
  );
}
