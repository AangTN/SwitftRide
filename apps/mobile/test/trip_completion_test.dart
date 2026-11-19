import 'dart:io';
import 'dart:ui' as ui;
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/trip_widgets.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'package:swiftride_mobile/src/goong_map.dart';
import 'trip_api_test.dart' show snapshot;

void main() {
  testWidgets(
    'payment label uses the saved method and old snapshots default to wallet',
    (t) async {
      final old = TripSnapshot.fromJson(snapshot());
      expect(old.paymentMethod, 'Wallet');
      final data = snapshot()..['paymentMethod'] = 'Cash';
      final cash = TripSnapshot.fromJson(data);
      await t.pumpWidget(
        MaterialApp(
          home: Scaffold(body: TripPaymentMethodLabel(trip: cash)),
        ),
      );
      expect(find.text('Tiền mặt'), findsOneWidget);
      expect(find.text('Ví SwiftRide'), findsNothing);
      await t.pumpWidget(const SizedBox());
    },
  );
  setUpAll(() async {
    await (FontLoader('BeVietnamPro')
          ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-Regular.ttf'))
          ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-Bold.ttf')))
        .load();
    await (FontLoader(
      'MaterialIcons',
    )..addFont(rootBundle.load('fonts/MaterialIcons-Regular.otf'))).load();
  });
  for (final driver in [false, true]) {
    for (final size in [const Size(360, 640), const Size(390, 844)]) {
      testWidgets(
        'completion receipt fits $size for ${driver ? 'driver' : 'rider'} with payment failure separated',
        (t) async {
          t.view.physicalSize = size;
          t.view.devicePixelRatio = 1;
          addTearDown(t.view.resetPhysicalSize);
          addTearDown(t.view.resetDevicePixelRatio);
          final data = snapshot(status: 'PaymentPending');
          data['paymentStatus'] = 'Failed';
          data['agreedAmount'] = 53850;
          data['riderNameSnapshot'] = 'Nguyễn Minh Anh';
          data['pickupAddress'] =
              '18 Hẻm 259 Hàn Hải Nguyên, Minh Phụng, Hồ Chí Minh';
          data['destinationAddress'] =
              'Phố đi bộ Nguyễn Huệ, Sài Gòn, Hồ Chí Minh';
          data['pickedUpAt'] = '2026-11-08T08:00:00Z';
          data['droppedOffAt'] = '2026-11-08T08:26:00Z';
          final p = (data['proposals'] as List).single as Map<String, dynamic>;
          p['status'] = 'DriverAccepted';
          p['distanceMeters'] = 8200;
          p['amount'] = 53850;
          p['driverNameSnapshot'] = 'Nguyễn Minh An';
          final trip = TripSnapshot.fromJson(data);
          var continued = false;
          final capture = GlobalKey();
          await t.pumpWidget(
            RepaintBoundary(
              key: capture,
              child: MaterialApp(
                debugShowCheckedModeBanner: false,
                theme: ThemeData(
                  useMaterial3: true,
                  fontFamily: 'BeVietnamPro',
                  colorScheme: ColorScheme.fromSeed(
                    seedColor: const Color(0xFF00B650),
                  ),
                ),
                home: Scaffold(
                  body: TripCompletionPage(
                    trip: trip,
                    driver: driver,
                    onContinue: () => continued = true,
                  ),
                  bottomNavigationBar: TripNavigation(
                    index: 0,
                    driver: driver,
                    onChanged: (_) {},
                  ),
                ),
              ),
            ),
          );
          await t.pumpAndSettle();
          expect(find.text('Chuyến đi hoàn tất'), findsOneWidget);
          expect(find.text('Thanh toán chưa thành công'), findsOneWidget);
          expect(find.byType(GoongMap), findsNothing);
          final failed = t.widget<Text>(
            find.text('Thanh toán chưa thành công'),
          );
          expect(failed.style!.color, const Color(0xFFB42318));
          expect(find.text('53.850 ₫'), findsOneWidget);
          expect(find.text('Quãng đường chuyến'), findsOneWidget);
          expect(t.takeException(), isNull);
          const output = String.fromEnvironment('TRIP_UI_DIR');
          if (output.isNotEmpty && size.height == 844) {
            await t.runAsync(() async {
              final boundary =
                  capture.currentContext!.findRenderObject()!
                      as RenderRepaintBoundary;
              final image = await boundary.toImage(pixelRatio: 2);
              final bytes = await image.toByteData(
                format: ui.ImageByteFormat.png,
              );
              await Directory(output).create(recursive: true);
              await File(
                '$output/${driver ? 'driver' : 'rider'}-completion.png',
              ).writeAsBytes(bytes!.buffer.asUint8List());
              image.dispose();
            });
          }
          await t.tap(
            find.text(driver ? 'Tiếp tục nhận chuyến' : 'Về trang chủ'),
          );
          expect(continued, isTrue);
          await t.pumpWidget(const SizedBox());
        },
      );
    }
  }
}
