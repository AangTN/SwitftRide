import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';
import 'package:swiftride_mobile/src/app.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'auth_test.dart' show MemoryStore, utf8Response;

void expectNoAuthScroll(WidgetTester tester) {
  final scroll = tester.state<ScrollableState>(
    find
        .descendant(
          of: find.byKey(const Key('auth-scroll')),
          matching: find.byType(Scrollable),
        )
        .first,
  );
  expect(scroll.position.maxScrollExtent, 0);
  expect(tester.takeException(), isNull);
}

Future<void> capturePreview(
  WidgetTester tester,
  GlobalKey key,
  String filename,
) async {
  if (!const bool.fromEnvironment('CAPTURE_UI')) return;
  await tester.runAsync(() async {
    final boundary =
        key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
    final image = await boundary.toImage(pixelRatio: 2);
    final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
    final directory = Directory('../../artifacts/mobile')
      ..createSync(recursive: true);
    await File(
      '${directory.path}/$filename.png',
    ).writeAsBytes(bytes!.buffer.asUint8List());
    image.dispose();
  });
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(() async {
    final font = FontLoader('BeVietnamPro')
      ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-Regular.ttf'))
      ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-SemiBold.ttf'))
      ..addFont(rootBundle.load('assets/fonts/BeVietnamPro-Bold.ttf'));
    await font.load();
    await (FontLoader(
      'MaterialIcons',
    )..addFont(rootBundle.load('fonts/MaterialIcons-Regular.otf'))).load();
  });
  for (final role in AppRole.values) {
    testWidgets('${role.name} auth fills SE and tall phones without scrolling', (
      tester,
    ) async {
      tester.view.devicePixelRatio = 1;
      tester.view.padding = const FakeViewPadding(top: 20, bottom: 20);
      addTearDown(tester.view.resetPadding);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPhysicalSize);
      final api = AuthApi(
        role: role,
        store: MemoryStore(),
        client: MockClient(
          (_) async => utf8Response(
            '{"challengeId":"test","expiresInSeconds":300,"retryAfterSeconds":60}',
            200,
          ),
        ),
      );
      final capture = GlobalKey();
      for (final size in [
        const Size(320, 568),
        const Size(375, 667),
        const Size(430, 932),
      ]) {
        tester.view.physicalSize = size;
        await tester.pumpWidget(
          RepaintBoundary(
            key: capture,
            child: SwiftRideApp(role: role, api: api),
          ),
        );
        await tester.runAsync(
          () => precacheImage(
            AssetImage('assets/images/${role.name}-hero.webp'),
            capture.currentContext!,
          ),
        );
        await tester.pumpAndSettle();
        expectNoAuthScroll(tester);
        expect(find.byIcon(Icons.two_wheeler_rounded), findsOneWidget);
        expect(find.byIcon(Icons.verified_user_rounded), findsOneWidget);
        await capturePreview(
          tester,
          capture,
          '${role.name}-${size.width.toInt()}x${size.height.toInt()}',
        );
        expect(
          tester.getSize(find.byKey(const Key('auth-scroll'))).height,
          size.height - 40,
        );
      }
      tester.view.physicalSize = const Size(320, 568);
      await tester.pumpAndSettle();
      if (role == AppRole.rider) {
        await tester.tap(find.text('Đăng ký tài khoản'));
        await tester.pumpAndSettle();
        expectNoAuthScroll(tester);
        expect(find.text('Quay về đăng nhập'), findsOneWidget);
        await capturePreview(tester, capture, 'rider-se-register');
        await tester.tap(find.text('Quay về đăng nhập'));
        await tester.pumpAndSettle();
        expect(find.text('Đăng ký tài khoản'), findsOneWidget);
        await tester.tap(find.text('Đăng ký tài khoản'));
        await tester.pumpAndSettle();
        await tester.enterText(
          find.widgetWithText(TextFormField, 'Họ và tên'),
          'Khách hàng SE',
        );
      }
      await tester.enterText(find.byKey(const Key('phone')), '0900000001');
      await tester.tap(
        find.widgetWithText(
          FilledButton,
          role == AppRole.rider ? 'Đăng ký' : 'Đăng nhập',
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('otp')), findsOneWidget);
      expectNoAuthScroll(tester);
      expect(find.text('Quay về đăng nhập'), findsOneWidget);
      await capturePreview(tester, capture, '${role.name}-se-otp');
      await tester.tap(find.text('Quay về đăng nhập'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('phone')), findsOneWidget);
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('phone')))
            .controller!
            .text,
        '0900000001',
      );
      expect(find.byKey(const Key('otp')), findsNothing);
      await tester.pumpWidget(const SizedBox());
    });
  }
  for (final cooldown in [0, 60]) {
    testWidgets('fifth invalid OTP locks entry, cooldown ${cooldown}s', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(320, 568);
      tester.view.devicePixelRatio = 1;
      tester.view.padding = const FakeViewPadding(top: 20, bottom: 20);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPadding);
      var attempts = 0;
      var requests = 0;
      final api = AuthApi(
        role: AppRole.driver,
        store: MemoryStore(),
        client: MockClient((request) async {
          if (request.url.path.endsWith('request-otp')) {
            requests++;
            return utf8Response(
              jsonEncode({
                'challengeId': 'challenge-$requests',
                'expiresInSeconds': 300,
                'retryAfterSeconds': cooldown,
              }),
              200,
            );
          }
          attempts++;
          return utf8Response(
            jsonEncode({
              'title': attempts < 5 ? 'invalid_otp' : 'otp_attempts_exceeded',
            }),
            400,
          );
        }),
      );
      final capture = GlobalKey();
      await tester.pumpWidget(
        RepaintBoundary(
          key: capture,
          child: SwiftRideApp(role: AppRole.driver, api: api),
        ),
      );
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('phone')), '0900000002');
      await tester.tap(find.widgetWithText(FilledButton, 'Đăng nhập'));
      await tester.pumpAndSettle();
      for (var i = 0; i < 5; i++) {
        await tester.enterText(find.byKey(const Key('otp')), '000000');
        await tester.tap(find.widgetWithText(FilledButton, 'Xác nhận'));
        await tester.pumpAndSettle();
      }
      expect(attempts, 5);
      expect(
        tester.widget<TextFormField>(find.byKey(const Key('otp'))).enabled,
        isFalse,
      );
      expect(
        tester
            .widget<FilledButton>(find.widgetWithText(FilledButton, 'Xác nhận'))
            .onPressed,
        isNull,
      );
      expect(find.textContaining('Mã này đã bị khóa'), findsOneWidget);
      expectNoAuthScroll(tester);
      await capturePreview(tester, capture, 'driver-se-otp-locked-$cooldown');
      if (cooldown > 0) {
        final resend = find.ancestor(
          of: find.textContaining('Gửi lại mã sau'),
          matching: find.byType(TextButton),
        );
        expect(tester.widget<TextButton>(resend).onPressed, isNull);
        expect(requests, 1);
        await tester.pumpWidget(const SizedBox());
        return;
      }
      await tester.tap(find.text('Gửi lại mã OTP'));
      await tester.pumpAndSettle();
      expect(requests, 2);
      expect(
        tester.widget<TextFormField>(find.byKey(const Key('otp'))).enabled,
        isTrue,
      );
      expect(
        tester
            .widget<FilledButton>(find.widgetWithText(FilledButton, 'Xác nhận'))
            .onPressed,
        isNotNull,
      );
      expect(find.textContaining('Mã này đã bị khóa'), findsNothing);
      await tester.pumpWidget(const SizedBox());
    });
  }
  test('unknown driver phone never suggests customer registration', () async {
    final api = AuthApi(
      role: AppRole.driver,
      store: MemoryStore(),
      client: MockClient(
        (_) async => utf8Response('{"title":"registration_required"}', 404),
      ),
    );
    await expectLater(
      api.requestOtp('0900000099'),
      throwsA(
        isA<AuthFailure>().having(
          (error) => error.message,
          'message',
          allOf(contains('tài xế'), isNot(contains('đăng ký'))),
        ),
      ),
    );
  });
}
