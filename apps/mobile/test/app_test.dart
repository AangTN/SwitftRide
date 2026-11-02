import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:swiftride_mobile/src/app.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'dart:convert';
import 'auth_test.dart' show MemoryStore, tokens, utf8Response;

void main() {
  testWidgets(
    'Driver-only customer login offers Rider activation on the same phone',
    (tester) async {
      final api = AuthApi(
        role: AppRole.rider,
        store: MemoryStore(),
        client: MockClient((request) async {
          if (request.url.path == '/api/auth/login/request-otp') {
            return utf8Response('{"title":"rider_activation_required"}', 403);
          }
          if (request.url.path.endsWith('request-otp')) {
            return utf8Response(
              '{"challengeId":"test","expiresInSeconds":300,"retryAfterSeconds":60}',
              200,
            );
          }
          if (request.url.path.endsWith('logout')) {
            return http.Response('', 204);
          }
          return utf8Response(
            jsonEncode(
              tokens(
                roles: request.url.path.contains('/register/')
                    ? ['Driver', 'Rider']
                    : ['Driver'],
              ),
            ),
            200,
          );
        }),
      );
      await tester.pumpWidget(SwiftRideApp(role: AppRole.rider, api: api));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('phone')), '0900000002');
      await tester.ensureVisible(
        find.widgetWithText(FilledButton, 'Đăng nhập'),
      );
      await tester.tap(find.widgetWithText(FilledButton, 'Đăng nhập'));
      await tester.pumpAndSettle();
      expect(find.widgetWithText(TextFormField, 'Họ và tên'), findsOneWidget);
      expect(
        find.textContaining('đăng ký tài khoản khách hàng'),
        findsOneWidget,
      );
      expect(find.byKey(const Key('otp')), findsNothing);
      expect(api.session, isNull);
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Họ và tên'),
        'Tên khách',
      );
      await tester.ensureVisible(find.widgetWithText(FilledButton, 'Đăng ký'));
      await tester.tap(find.widgetWithText(FilledButton, 'Đăng ký'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('otp')), '123456');
      await tester.ensureVisible(find.text('Xác nhận'));
      await tester.tap(find.text('Xác nhận'));
      await tester.pumpAndSettle();
      expect(find.text('Trang chủ'), findsOneWidget);
      await tester.tap(find.text('Bạn muốn đi đâu?'));
      await tester.pumpAndSettle();
      // The modal dims only the centered phone frame, leaving both gutters unchanged.
      final barrier = tester.getRect(find.byType(ModalBarrier).last);
      expect(barrier.width, 430);
      expect(
        barrier.center.dx,
        tester.view.physicalSize.width / tester.view.devicePixelRatio / 2,
      );
      await tester.tap(find.byTooltip('Đóng'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Tài khoản'));
      await tester.pumpAndSettle();
      expect(find.text('Đăng nhập thành công'), findsOneWidget);
      expect(api.session!.user.roles, containsAll(['Driver', 'Rider']));
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    },
  );
  for (final role in AppRole.values) {
    testWidgets('${role.name} can request OTP, sign in, and log out', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(390, 844);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final api = AuthApi(
        role: role,
        store: MemoryStore(),
        client: MockClient((request) async {
          if (request.url.path.endsWith('request-otp')) {
            return utf8Response(
              jsonEncode({
                'challengeId': 'test',
                'expiresInSeconds': 300,
                'retryAfterSeconds': 60,
              }),
              200,
            );
          }
          if (request.url.path.endsWith('verify-otp')) {
            expect(jsonDecode(request.body)['clientApp'], role.clientApp);
            return utf8Response(
              jsonEncode(tokens(roles: [role.requiredRole])),
              200,
            );
          }
          if (request.url.path.endsWith('logout')) {
            return http.Response('', 204);
          }
          if (request.url.path == '/api/matching/drivers/me') {
            return utf8Response(
              jsonEncode({
                'legalName': 'Tên tài xế riêng',
                'plateNumber': '59A1-12345',
                'vehicleName': 'Honda Wave Alpha',
              }),
              200,
            );
          }
          return utf8Response(
            jsonEncode(tokens(roles: [role.requiredRole])['user']),
            200,
          );
        }),
      );
      await tester.pumpWidget(SwiftRideApp(role: role, api: api));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('phone')), '0900000001');
      await tester.ensureVisible(
        find.widgetWithText(FilledButton, 'Đăng nhập'),
      );
      await tester.tap(find.widgetWithText(FilledButton, 'Đăng nhập'));
      await tester.pumpAndSettle();
      expect(find.text('Xác thực số điện thoại'), findsOneWidget);
      await tester.enterText(find.byKey(const Key('otp')), '123456');
      await tester.ensureVisible(find.text('Xác nhận'));
      await tester.tap(find.text('Xác nhận'));
      await tester.pumpAndSettle();
      if (role == AppRole.rider) {
        expect(find.text('Trang chủ'), findsOneWidget);
        expect(find.text('Đặt xe ngay'), findsNothing);
        await tester.tap(find.text('Tài khoản'));
        await tester.pumpAndSettle();
      } else {
        expect(find.text('Trang chủ'), findsNothing);
      }
      expect(find.text('Đăng nhập thành công'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.ensureVisible(find.text('Đăng xuất'));
      await tester.tap(find.text('Đăng xuất'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('phone')), findsOneWidget);
      expect(api.session, isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }
  for (final role in AppRole.values) {
    testWidgets('${role.name} login fits small phone and desktop', (
      tester,
    ) async {
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPhysicalSize);
      final api = AuthApi(
        role: role,
        store: MemoryStore(),
        client: MockClient((_) async => http.Response('{}', 200)),
      );
      for (final size in [const Size(360, 740), const Size(1366, 900)]) {
        tester.view.physicalSize = size;
        await tester.pumpWidget(SwiftRideApp(role: role, api: api));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('phone')), findsOneWidget);
        expect(find.byType(Brand), findsOneWidget);
        expect(find.text('BẠN ĐỒNG HÀNH MỖI NGÀY'), findsNothing);
        expect(find.text('DÀNH CHO ĐỐI TÁC TÀI XẾ'), findsNothing);
        expect(
          tester.getSize(find.byKey(const Key('phone'))).width,
          lessThanOrEqualTo(382),
        );
        expect(tester.takeException(), isNull);
      }
      await tester.pumpWidget(const SizedBox());
    });
  }
  testWidgets('registration includes required name and validates empty input', (
    tester,
  ) async {
    final api = AuthApi(role: AppRole.rider, store: MemoryStore());
    await tester.pumpWidget(SwiftRideApp(role: AppRole.rider, api: api));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Đăng ký tài khoản'));
    await tester.tap(find.text('Đăng ký tài khoản'));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(TextFormField, 'Họ và tên'), findsOneWidget);
    await tester.ensureVisible(find.text('Đăng ký'));
    await tester.tap(find.text('Đăng ký'));
    await tester.pumpAndSettle();
    expect(find.text('Vui lòng nhập họ và tên.'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });
}
