import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/rider_trip_views.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'trip_api_test.dart' show snapshot;

void main() {
  testWidgets('confirmation displays both full addresses and the agreed fare', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(360, 640);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    const pickup =
        '18 Hẻm 259 Hàn Hải Nguyên, Phường Minh Phụng, Thành phố Hồ Chí Minh';
    const destination =
        'Đại học Sài Gòn, 273 An Dương Vương, Phường Chợ Quán, Thành phố Hồ Chí Minh';
    final data = snapshot()
      ..['pickupAddress'] = pickup
      ..['destinationAddress'] = destination;
    var approved = false;
    var rejected = false;
    var cancelled = false;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: RiderTripPanel(
            trip: TripSnapshot.fromJson(data),
            onApprove: () => approved = true,
            onReject: () => rejected = true,
            onCancel: () => cancelled = true,
            onDismiss: () {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    for (final address in [pickup, destination]) {
      final text = tester.widget<Text>(find.text(address));
      expect(text.maxLines, isNull);
    }
    await tester.ensureVisible(find.text('Đồng ý · 35.000 ₫'));
    await tester.tap(find.text('Đồng ý · 35.000 ₫'));
    expect(approved, isTrue);
    await tester.ensureVisible(find.text('Từ chối và tìm tài xế khác'));
    await tester.tap(find.text('Từ chối và tìm tài xế khác'));
    expect(rejected, isTrue);
    expect(cancelled, isFalse);
    await tester.ensureVisible(find.text('Hủy chuyến'));
    await tester.tap(find.text('Hủy chuyến'));
    expect(cancelled, isTrue);
    expect(tester.takeException(), isNull);
  });

  testWidgets('picked up hides driver identity and duplicate trip metrics', (
    tester,
  ) async {
    final data = snapshot(status: 'PickedUp')..['driverId'] = 'driver-1';
    final proposal = (data['proposals'] as List).single as Map<String, dynamic>;
    proposal['status'] = 'DriverAccepted';
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: RiderTripPanel(
            trip: TripSnapshot.fromJson(data),
            onApprove: () {},
            onReject: () {},
            onCancel: () {},
            onDismiss: () {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Minh Anh'), findsNothing);
    expect(find.text('59A1-12345'), findsNothing);
    expect(find.text('Quãng đường chuyến'), findsNothing);
    expect(find.text('Destination'), findsOneWidget);
    expect(find.text('Giá chuyến'), findsOneWidget);
    expect(find.text('35.000 ₫'), findsOneWidget);
    expect(find.text('Ví SwiftRide'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'driver approach shows identity and fare without duplicate trip metrics',
    (tester) async {
      final data = snapshot(status: 'EnRoute')..['driverId'] = 'driver-1';
      final proposal =
          (data['proposals'] as List).single as Map<String, dynamic>;
      proposal['status'] = 'DriverAccepted';
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: RiderTripPanel(
              trip: TripSnapshot.fromJson(data),
              onApprove: () {},
              onReject: () {},
              onCancel: () {},
              onDismiss: () {},
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Minh Anh'), findsOneWidget);
      expect(find.text('59A1-12345'), findsOneWidget);
      expect(find.text('Quãng đường chuyến'), findsNothing);
      expect(find.text('Pickup'), findsOneWidget);
      expect(find.text('Giá chuyến'), findsOneWidget);
      expect(find.text('35.000 ₫'), findsOneWidget);
      expect(find.text('Ví SwiftRide'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'failed search after a rider rejection explains the next search',
    (tester) async {
      final data = snapshot(status: 'Failed')
        ..['failureReason'] = 'no_driver_available';
      (data['proposals'] as List).first['status'] = 'RiderRejected';
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: RiderTripPanel(
              trip: TripSnapshot.fromJson(data),
              onApprove: () {},
              onReject: () {},
              onCancel: () {},
              onDismiss: () {},
            ),
          ),
        ),
      );
      expect(find.textContaining('chưa tìm được tài xế khác'), findsOneWidget);
    },
  );
}
