import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/rider_home.dart';
import 'rider_home_test.dart' show FakePlaces, choose;
import 'rider_realtime_test.dart' show FakeApi, FakeRealtime, trip;

void main() {
  testWidgets(
    'one submit from location picker creates one trip without a second home action',
    (tester) async {
      tester.view.physicalSize = const Size(390, 844);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final api = FakeApi();
      addTearDown(api.auth.dispose);
      final response = Completer<void>();
      var requests = 0;
      api.onCreate = () async {
        requests++;
        await response.future;
        return trip('Matching', 1);
      };
      await tester.pumpWidget(
        MaterialApp(
          home: RiderHome(
            fullName: 'Khách',
            account: const Text('Account'),
            places: FakePlaces(),
            tripApi: api,
            realtime: FakeRealtime(),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('request-trip')), findsNothing);
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      await choose(tester, 'pickup-field', 'Đại học Sài Gòn');
      await choose(tester, 'destination-field', 'Chợ Bến Thành');
      await tester.ensureVisible(find.text('Tiền mặt'));
      await tester.tap(find.text('Tiền mặt'));
      await tester.ensureVisible(find.text('Đặt xe'));
      await tester.tap(find.text('Đặt xe'));
      await tester.pump(const Duration(milliseconds: 400));
      await tester.pump();
      expect(requests, 1);
      expect(api.sentBody!['paymentMethod'], 'Cash');
      expect(find.text('Áp dụng'), findsNothing);
      expect(find.text('Đặt chuyến'), findsNothing);
      response.complete();
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 200));
      expect(find.text('Đang tìm tài xế'), findsOneWidget);
      expect(requests, 1);
      await tester.pumpWidget(const SizedBox());
    },
  );
}
