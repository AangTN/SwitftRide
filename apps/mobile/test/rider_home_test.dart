import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:maplibre_gl/maplibre_gl.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'package:swiftride_mobile/src/rider_home.dart';
import 'package:swiftride_mobile/src/goong_map.dart';
import 'package:swiftride_mobile/src/ride_locations.dart';
import 'package:swiftride_mobile/src/places_api.dart';
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

  testWidgets(
    'phone layouts select both fields and apply vehicle/payment with no GPS',
    (tester) async {
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPhysicalSize);
      for (final size in [
        const Size(360, 640),
        const Size(390, 844),
        const Size(430, 932),
      ]) {
        tester.view.physicalSize = size;
        final lookup = FakePlaces();
        final capture = GlobalKey();
        await tester.pumpWidget(
          RepaintBoundary(
            key: capture,
            child: MaterialApp(
              theme: ThemeData(fontFamily: 'BeVietnamPro'),
              home: RiderHome(
                fullName: 'Khách',
                account: const Text('Hồ sơ'),
                places: lookup,
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byTooltip('Về vị trí hiện tại'), findsNothing);
        await tester.tap(find.text('Bạn muốn đi đâu?'));
        await tester.pumpAndSettle();
        expect(
          tester.widget<GoongMap>(find.byType(GoongMap)).interactive,
          isFalse,
        );
        expect(find.byTooltip('Dùng vị trí hiện tại'), findsNothing);
        await choose(tester, 'pickup-field', 'Đại học Sài Gòn');
        await choose(tester, 'destination-field', 'Chợ Bến Thành');
        expect(
          find.text('273 An Dương Vương, Chợ Quán, Hồ Chí Minh'),
          findsOneWidget,
        );
        await tester.ensureVisible(find.text('7 chỗ'));
        await tester.tap(find.text('7 chỗ'));
        await tester.ensureVisible(find.text('Ví SwiftRide'));
        await tester.tap(find.text('Ví SwiftRide'));
        await tester.tap(find.text('Áp dụng'));
        await tester.pumpAndSettle();
        expect(find.byType(RideLocationPicker), findsNothing);
        expect(lookup.detailCalls, 2);
        await tester.tap(find.text('Bạn muốn đi đâu?'));
        await tester.pumpAndSettle();
        final picker = tester.widget<RideLocationPicker>(
          find.byType(RideLocationPicker),
        );
        expect(
          picker.pickup.address,
          '273 An Dương Vương, Chợ Quán, Hồ Chí Minh',
        );
        expect(picker.pickup.point!.latitude, 10.759);
        expect(
          picker.destination!.address,
          'Chợ Bến Thành, Bến Thành, Hồ Chí Minh',
        );
        expect(picker.vehicleType, RideVehicleType.car7Seats);
        expect(picker.paymentMethod, RidePaymentMethod.swiftRideWallet);
        expect(
          RideLocations(
            picker.pickup,
            picker.destination!,
            picker.vehicleType,
            picker.paymentMethod,
          ).toTripRequest()['pickupAddress'],
          picker.pickup.address,
        );
        await capturePreview(
          tester,
          capture,
          'place-search-${size.width.toInt()}x${size.height.toInt()}',
        );
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      }
    },
  );

  testWidgets(
    'typing alone cannot apply and editing invalidates old coordinates',
    (tester) async {
      final lookup = FakePlaces();
      await openPicker(tester, lookup);
      await tester.enterText(
        find.byKey(const Key('pickup-field')),
        'Địa chỉ tự nhập',
      );
      await tester.tap(find.text('Áp dụng'));
      await tester.pumpAndSettle();
      expect(
        find.text('Vui lòng chọn điểm đón từ kết quả tìm kiếm.'),
        findsOneWidget,
      );
      expect(find.text('Vui lòng nhập điểm đến.'), findsOneWidget);
      await choose(tester, 'pickup-field', 'Đại học Sài Gòn');
      await choose(tester, 'destination-field', 'Chợ Bến Thành');
      await tester.enterText(
        find.byKey(const Key('pickup-field')),
        'Địa chỉ khác',
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Áp dụng'));
      await tester.pumpAndSettle();
      expect(find.byType(RideLocationPicker), findsOneWidget);
      expect(
        find.text('Vui lòng chọn điểm đón từ kết quả tìm kiếm.'),
        findsOneWidget,
      );
      expect(
        find.text('273 An Dương Vương, Chợ Quán, Hồ Chí Minh'),
        findsNothing,
      );
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'late autocomplete and detail results cannot overwrite another field',
    (tester) async {
      final lookup = FakePlaces();
      final old = Completer<List<PlacePrediction>>();
      lookup.pendingSearch = old;
      await openPicker(tester, lookup);
      await tester.enterText(
        find.byKey(const Key('pickup-field')),
        'Old campus',
      );
      await tester.pump(const Duration(milliseconds: 360));
      lookup.pendingSearch = null;
      await tester.enterText(
        find.byKey(const Key('destination-field')),
        'Chợ Bến Thành',
      );
      await tester.pump(const Duration(milliseconds: 360));
      await tester.pumpAndSettle();
      old.complete([const PlacePrediction('OLD RESULT', 'old')]);
      await tester.pumpAndSettle();
      expect(find.text('OLD RESULT'), findsNothing);
      final details = Completer<RidePlace>();
      lookup.pendingDetails = details;
      await tester.tap(find.byKey(const ValueKey('place-Chợ Bến Thành')));
      await tester.pump();
      await tester.enterText(
        find.byKey(const Key('destination-field')),
        'Changed',
      );
      details.complete(FakePlaces.destination);
      await tester.pumpAndSettle();
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('destination-field')))
            .controller!
            .text,
        'Changed',
      );
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('pickup-field')))
            .controller!
            .text,
        'Old campus',
      );
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'place lookup error can retry and selection never changes the other field',
    (tester) async {
      final lookup = FakePlaces()..failSearch = true;
      await openPicker(tester, lookup);
      await tester.enterText(
        find.byKey(const Key('pickup-field')),
        'Đại học Sài Gòn',
      );
      await tester.pump(const Duration(milliseconds: 360));
      await tester.pumpAndSettle();
      expect(find.text('Lỗi tìm địa điểm thử nghiệm'), findsOneWidget);
      lookup.failSearch = false;
      await tester.tap(find.text('Thử lại'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('place-Đại học Sài Gòn')));
      await tester.pumpAndSettle();
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('destination-field')))
            .controller!
            .text,
        isEmpty,
      );
      expect(
        find.text('273 An Dương Vương, Chợ Quán, Hồ Chí Minh'),
        findsOneWidget,
      );
      await tester.pumpWidget(const SizedBox());
    },
  );
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
}

Future<void> openPicker(WidgetTester tester, PlaceLookup lookup) async {
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: RideLocationPicker(
          pickup: defaultPickup,
          recentDestinations: const [],
          vehicleType: RideVehicleType.motorcycle,
          paymentMethod: RidePaymentMethod.swiftRideWallet,
          places: lookup,
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> choose(WidgetTester tester, String field, String query) async {
  await tester.ensureVisible(find.byKey(Key(field)));
  await tester.enterText(find.byKey(Key(field)), query);
  await tester.pump(const Duration(milliseconds: 360));
  await tester.pumpAndSettle();
  final result = find.byKey(ValueKey('place-$query'));
  await tester.ensureVisible(result);
  await tester.tap(result);
  await tester.pumpAndSettle();
}

class FakePlaces implements PlaceLookup {
  static const pickup = RidePlace(
    'Đại học Sài Gòn',
    '273 An Dương Vương, Chợ Quán, Hồ Chí Minh',
    LatLng(10.759, 106.682),
    'Đại học Sài Gòn',
  );
  static const destination = RidePlace(
    'Chợ Bến Thành',
    'Chợ Bến Thành, Bến Thành, Hồ Chí Minh',
    LatLng(10.7725, 106.698),
    'Chợ Bến Thành',
  );
  int detailCalls = 0;
  bool failSearch = false;
  Completer<List<PlacePrediction>>? pendingSearch;
  Completer<RidePlace>? pendingDetails;
  @override
  Future<List<PlacePrediction>> search(String input) async {
    if (pendingSearch != null) return pendingSearch!.future;
    if (failSearch) throw const AuthFailure('Lỗi tìm địa điểm thử nghiệm');
    return [PlacePrediction(input, input)];
  }

  @override
  Future<RidePlace> details(String placeId) async {
    detailCalls++;
    if (pendingDetails != null) return pendingDetails!.future;
    return placeId == pickup.placeId ? pickup : destination;
  }
}
