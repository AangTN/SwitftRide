import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:geolocator/geolocator.dart';
import 'package:swiftride_mobile/src/rider_home.dart';
import 'package:swiftride_mobile/src/goong_map.dart';
import 'package:swiftride_mobile/src/ride_locations.dart';
import 'auth_layout_test.dart' show capturePreview;

void main() {
  setUpAll(() async {
    await (FontLoader('BeVietnamPro')
          ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-Regular.ttf'))
          ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-SemiBold.ttf'))
          ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-Bold.ttf')))
        .load();
    await (FontLoader(
      'MaterialIcons',
    )..addFont(rootBundle.load('fonts/MaterialIcons-Regular.otf'))).load();
  });
  testWidgets('rider home fits phone sizes, opens suggestions and account', (
    tester,
  ) async {
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    for (final size in [
      const Size(360, 640),
      const Size(390, 844),
      const Size(430, 932),
    ]) {
      tester.view.physicalSize = size;
      final capture = GlobalKey();
      await tester.pumpWidget(
        RepaintBoundary(
          key: capture,
          child: MaterialApp(
            theme: ThemeData(fontFamily: 'BeVietnamPro'),
            home: const RiderHome(
              fullName: 'Nguyễn Văn An',
              account: Text('Hồ sơ khách hàng'),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Đặt xe ngay'), findsNothing);
      expect(find.text('Xem tất cả'), findsNothing);
      expect(find.text('Điểm đến đề xuất'), findsOneWidget);
      expect(find.text('Điểm đến gần đây'), findsNothing);
      for (final place in suggestedDestinations) {
        expect(find.text(place.name), findsOneWidget);
      }
      expect(find.text('Chưa có cấu hình bản đồ Goong.'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await capturePreview(
        tester,
        capture,
        'rider-home-${size.width.toInt()}x${size.height.toInt()}',
      );
      expect(
        tester.widget<GoongMap>(find.byType(GoongMap)).interactive,
        isTrue,
      );
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      expect(
        tester.widget<GoongMap>(find.byType(GoongMap)).interactive,
        isFalse,
      );
      expect(find.byKey(const Key('pickup-field')), findsOneWidget);
      expect(find.byKey(const Key('destination-field')), findsOneWidget);
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('pickup-field')))
            .controller!
            .text,
        isEmpty,
      );
      await tester.enterText(
        find.byKey(const Key('pickup-field')),
        'Điểm đón thử',
      );
      await tester.runAsync(
        () => precacheImage(
          const AssetImage('assets/images/booking-vehicles.png'),
          tester.element(find.byType(RideLocationPicker)),
        ),
      );
      await tester.pumpAndSettle();
      await capturePreview(
        tester,
        capture,
        'booking-picker-${size.width.toInt()}x${size.height.toInt()}',
      );

      await tester.enterText(
        find.byKey(const Key('destination-field')),
        'Chợ Bến',
      );
      await tester.pumpAndSettle();
      final sheet = find.byType(BottomSheet);
      expect(
        find.descendant(of: sheet, matching: find.text('Sân bay Tân Sơn Nhất')),
        findsNothing,
      );
      await tester.tap(
        find.descendant(of: sheet, matching: find.text('Chợ Bến Thành')),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Áp dụng'));
      await tester.pumpAndSettle();
      expect(sheet, findsNothing);
      expect(
        tester.widget<GoongMap>(find.byType(GoongMap)).interactive,
        isTrue,
      );
      expect(
        find.text('Trung tâm TP. Hồ Chí Minh → Chợ Bến Thành'),
        findsNothing,
      );
      await tester.tap(find.text('Tài khoản'));
      await tester.pumpAndSettle();
      expect(find.text('Hồ sơ khách hàng'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    }
  });

  testWidgets(
    'empty history shows three HCM landmarks and selecting one prefills destination',
    (tester) async {
      await tester.pumpWidget(
        const MaterialApp(
          home: RiderHome(
            fullName: 'Khách',
            account: SizedBox(),
            recentDestinations: [],
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Điểm đến gần đây'), findsNothing);
      expect(find.text('Điểm đến đề xuất'), findsOneWidget);
      for (final place in suggestedDestinations) {
        expect(find.text(place.name), findsOneWidget);
      }
      await tester.tap(find.text('Chợ Bến Thành'));
      await tester.pumpAndSettle();
      final field = tester.widget<TextFormField>(
        find.byKey(const Key('destination-field')),
      );
      expect(field.controller!.text, 'Chợ Bến Thành');
      await tester.enterText(
        find.byKey(const Key('pickup-field')),
        'Điểm đón thử',
      );
      await tester.tap(find.text('Áp dụng'));
      await tester.pumpAndSettle();
      expect(
        find.text('Trung tâm TP. Hồ Chí Minh → Chợ Bến Thành'),
        findsNothing,
      );
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'location picker validates destination, changes pickup and cancels edits',
    (tester) async {
      tester.view.physicalSize = const Size(360, 640);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetViewInsets);
      await tester.pumpWidget(
        const MaterialApp(
          home: RiderHome(fullName: 'Khách', account: SizedBox()),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Áp dụng'));
      await tester.pumpAndSettle();
      expect(find.text('Vui lòng nhập điểm đến.'), findsOneWidget);
      tester.view.viewInsets = const FakeViewPadding(bottom: 240);
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      await tester.enterText(find.byKey(const Key('pickup-field')), 'Nhà thờ');
      await tester.pumpAndSettle();
      for (final place in suggestedDestinations) {
        expect(
          find.descendant(
            of: find.byType(BottomSheet),
            matching: find.text(place.name),
          ),
          findsOneWidget,
        );
      }
      final destinationSuggestion = find.descendant(
        of: find.byType(BottomSheet),
        matching: find.text('Nhà thờ Đức Bà'),
      );
      await tester.ensureVisible(destinationSuggestion);
      await tester.tap(destinationSuggestion);
      await tester.pumpAndSettle();
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('pickup-field')))
            .controller!
            .text,
        'Nhà thờ',
      );
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('destination-field')))
            .controller!
            .text,
        'Nhà thờ Đức Bà',
      );
      expect(find.byTooltip('Thông tin địa điểm'), findsNothing);
      tester.view.resetViewInsets();
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const Key('destination-field')),
        'Địa chỉ tự nhập',
      );
      await tester.tap(find.text('Áp dụng'));
      await tester.pumpAndSettle();
      expect(find.text('Nhà thờ → Địa chỉ tự nhập'), findsNothing);
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('destination-field')))
            .controller!
            .text,
        isEmpty,
      );
      await tester.enterText(
        find.byKey(const Key('destination-field')),
        'Địa chỉ chưa lưu',
      );
      await tester.tap(find.byTooltip('Đóng'));
      await tester.pumpAndSettle();
      expect(find.text('Nhà thờ → Địa chỉ tự nhập'), findsNothing);
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      final destinationField = find.byKey(const Key('destination-field'));
      expect(
        tester.widget<TextFormField>(destinationField).controller!.text,
        isEmpty,
      );
      await tester.enterText(destinationField, 'Địa chỉ khác');
      await tester.enterText(destinationField, '');
      await tester.tap(find.byTooltip('Đóng'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      expect(
        tester.widget<TextFormField>(destinationField).controller!.text,
        isEmpty,
      );
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    },
  );
  testWidgets('vehicle and payment choices apply together', (tester) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await tester.pumpWidget(
      const MaterialApp(
        home: RiderHome(fullName: 'Khách', account: SizedBox()),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Bạn muốn đi đâu?'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('pickup-field')),
      'Điểm đón thử',
    );
    await tester.enterText(
      find.byKey(const Key('destination-field')),
      'Chợ Bến Thành',
    );
    await tester.ensureVisible(find.text('7 chỗ'));
    await tester.tap(find.text('7 chỗ'));
    await tester.ensureVisible(find.text('Ví SwiftRide'));
    await tester.tap(find.text('Ví SwiftRide'));
    await tester.tap(find.text('Áp dụng'));
    await tester.pumpAndSettle();
    expect(
      find.text('Trung tâm TP. Hồ Chí Minh → Chợ Bến Thành'),
      findsNothing,
    );
    await tester.tap(find.text('Bạn muốn đi đâu?'));
    await tester.pumpAndSettle();
    final picker = tester.widget<RideLocationPicker>(
      find.byType(RideLocationPicker),
    );
    expect(picker.vehicleType, RideVehicleType.car7Seats);
    expect(picker.paymentMethod, RidePaymentMethod.swiftRideWallet);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets(
    'home panel collapses and picker close stays fixed while scrolling',
    (tester) async {
      tester.view.physicalSize = const Size(360, 640);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await tester.pumpWidget(
        const MaterialApp(
          home: RiderHome(fullName: 'Khách', account: SizedBox()),
        ),
      );
      await tester.pumpAndSettle();
      final panel = find.byKey(const Key('home-booking-panel'));
      final slowDrag = await tester.startGesture(
        tester.getTopLeft(panel) + const Offset(90, 10),
      );
      final expandedHeight = tester.getSize(panel).height;
      await slowDrag.moveBy(const Offset(0, 20));
      await tester.pump();
      await slowDrag.moveBy(const Offset(0, 60));
      await tester.pump();
      final intermediateHeight = tester.getSize(panel).height;
      expect(intermediateHeight, lessThan(expandedHeight));
      expect(intermediateHeight, greaterThan(126));
      await slowDrag.moveBy(const Offset(0, 140));
      await tester.pump(const Duration(milliseconds: 600));
      await slowDrag.up();
      await tester.pumpAndSettle();
      expect(find.text('Điểm đến đề xuất'), findsNothing);
      expect(find.text('Bạn muốn đi đâu?'), findsOneWidget);
      await tester.flingFrom(
        tester.getTopLeft(panel) + const Offset(90, 10),
        const Offset(0, -100),
        800,
      );
      await tester.pumpAndSettle();
      expect(find.text('Điểm đến đề xuất'), findsOneWidget);
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      final close = find.byTooltip('Đóng');
      final before = tester.getCenter(close);
      await tester.drag(
        find.byKey(const Key('booking-scroll')),
        const Offset(0, -300),
      );
      await tester.pumpAndSettle();
      expect(tester.getCenter(close), before);
      await tester.tap(close);
      await tester.pumpAndSettle();
      expect(find.byType(RideLocationPicker), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'location button uses device coordinates and handles permission denial',
    (tester) async {
      final original = GeolocatorPlatform.instance;
      final location = _TestGeolocator();
      GeolocatorPlatform.instance = location;
      addTearDown(() => GeolocatorPlatform.instance = original);
      await tester.pumpWidget(
        const MaterialApp(
          home: RiderHome(fullName: 'Khách', account: SizedBox()),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byTooltip('Về vị trí hiện tại'));
      await tester.pumpAndSettle();
      expect(location.requested, isTrue);
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      final picker = tester.widget<RideLocationPicker>(
        find.byType(RideLocationPicker),
      );
      expect(picker.pickup.name, 'Vị trí hiện tại');
      expect(picker.pickup.point!.latitude, 10.81);
      expect(picker.pickup.point!.longitude, closeTo(106.72, .000001));
      await tester.enterText(
        find.byKey(const Key('pickup-field')),
        'Địa chỉ tạm',
      );
      location.requested = false;
      location.pending = Completer<Position>();
      await tester.tap(find.byTooltip('Dùng vị trí hiện tại'));
      await tester.pump();
      expect(
        find.descendant(
          of: find.byType(BottomSheet),
          matching: find.byType(CircularProgressIndicator),
        ),
        findsOneWidget,
      );
      location.pending!.complete(location.position);
      await tester.pumpAndSettle();
      location.pending = null;
      expect(location.requested, isTrue);
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('pickup-field')))
            .controller!
            .text,
        'Vị trí hiện tại',
      );

      await tester.tap(find.byTooltip('Đóng'));
      await tester.pumpAndSettle();
      location.permission = LocationPermission.deniedForever;
      location.requested = false;
      await tester.tap(find.byTooltip('Về vị trí hiện tại'));
      await tester.pumpAndSettle();
      expect(location.requested, isFalse);
      expect(
        find.textContaining('Hãy cho phép truy cập vị trí'),
        findsOneWidget,
      );
      await tester.pumpWidget(const SizedBox());
    },
  );
}

class _TestGeolocator extends GeolocatorPlatform {
  LocationPermission permission = LocationPermission.whileInUse;
  bool requested = false;
  Completer<Position>? pending;
  @override
  Future<LocationPermission> checkPermission() async => permission;
  @override
  Future<Position> getCurrentPosition({
    LocationSettings? locationSettings,
  }) async {
    requested = true;
    if (pending != null) return pending!.future;
    return position;
  }

  Position get position => Position(
    latitude: 10.81,
    longitude: 106.72,
    timestamp: DateTime(2026),
    accuracy: 5,
    altitude: 0,
    altitudeAccuracy: 0,
    heading: 0,
    headingAccuracy: 0,
    speed: 0,
    speedAccuracy: 0,
  );
}
