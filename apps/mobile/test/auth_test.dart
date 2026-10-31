import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:swiftride_mobile/src/auth.dart';

class MemoryStore implements SessionStore {
  String? value;
  @override
  Future<String?> read() async => value;
  @override
  Future<void> write(String data) async => value = data;
  @override
  Future<void> clear() async => value = null;
}

Map<String, dynamic> tokens({
  List<String> roles = const ['Rider'],
  bool expired = false,
}) => {
  'accessToken': 'access',
  'refreshToken': 'refresh',
  'accessTokenExpiresAt': DateTime.now()
      .add(Duration(minutes: expired ? -1 : 15))
      .toIso8601String(),
  'user': {
    'fullName': 'Nguyễn An',
    'phoneNumber': '+84900000001',
    'roles': roles,
    'driver': null,
  },
};
OtpChallenge challenge() => OtpChallenge.fromJson({
  'challengeId': 'test',
  'expiresInSeconds': 300,
  'retryAfterSeconds': 60,
});

void main() {
  for (final role in AppRole.values) {
    test('${role.name} login sends its app before requesting OTP', () async {
      final api = AuthApi(
        role: role,
        store: MemoryStore(),
        baseUrl: 'http://test',
        client: MockClient((request) async {
          expect(request.url.path, '/api/auth/login/request-otp');
          expect(jsonDecode(request.body), {
            'phoneNumber': '+84900000001',
            'clientApp': role.clientApp,
          });
          return utf8Response(
            jsonEncode({
              'challengeId': 'id',
              'expiresInSeconds': 300,
              'retryAfterSeconds': 60,
            }),
            200,
          );
        }),
      );
      expect((await api.requestOtp('0900000001')).id, 'id');
      expect(api.session, isNull);
    });
    test(
      '${role.name} missing role is explained before the OTP screen',
      () async {
        final code = role == AppRole.rider
            ? 'rider_activation_required'
            : 'role_required';
        final api = AuthApi(
          role: role,
          store: MemoryStore(),
          baseUrl: 'http://test',
          client: MockClient(
            (_) async => utf8Response(jsonEncode({'title': code}), 403),
          ),
        );
        await expectLater(
          api.requestOtp('0900000001'),
          throwsA(
            isA<AuthFailure>()
                .having((e) => e.code, 'code', code)
                .having(
                  (e) => e.message,
                  'message',
                  contains(role == AppRole.rider ? 'đăng ký' : 'hỗ trợ'),
                ),
          ),
        );
        expect(api.session, isNull);
      },
    );
  }
  test(
    'driver reads legal name from Matching even when Auth name differs',
    () async {
      final api = AuthApi(
        role: AppRole.driver,
        store: MemoryStore(),
        baseUrl: 'http://test',
        client: MockClient((request) async {
          if (request.url.path.endsWith('verify-otp')) {
            return utf8Response(jsonEncode(tokens(roles: ['Driver'])), 200);
          }
          if (request.url.path == '/api/auth/me') {
            return utf8Response(
              jsonEncode(tokens(roles: ['Driver'])['user']),
              200,
            );
          }
          expect(request.url.path, '/api/matching/drivers/me');
          expect(request.headers['Authorization'], 'Bearer access');
          return utf8Response(
            jsonEncode({
              'legalName': 'Tên tài xế riêng',
              'vehicleName': 'Wave',
              'plateNumber': '59A1-12345',
            }),
            200,
          );
        }),
      );
      await api.verify(challenge(), '123456');
      expect(api.currentProfile!.fullName, 'Tên tài xế riêng');
      expect(api.session!.user.fullName, 'Nguyễn An');
      expect(api.currentProfile!.driver!['plateNumber'], '59A1-12345');
    },
  );
  test('Driver-only rider login asks for explicit Rider activation', () async {
    final api = AuthApi(
      role: AppRole.rider,
      store: MemoryStore(),
      baseUrl: 'http://test',
      client: MockClient((request) async {
        if (request.url.path.endsWith('logout')) return http.Response('', 204);
        return utf8Response(jsonEncode(tokens(roles: ['Driver'])), 200);
      }),
    );
    await expectLater(
      api.verify(challenge(), '123456'),
      throwsA(
        isA<AuthFailure>().having(
          (e) => e.code,
          'code',
          'rider_activation_required',
        ),
      ),
    );
    expect(api.session, isNull);
  });
  test('unavailable Matching does not fall back to Auth driver name', () async {
    final api = AuthApi(
      role: AppRole.driver,
      store: MemoryStore(),
      baseUrl: 'http://test',
      client: MockClient((request) async {
        if (request.url.path.endsWith('verify-otp')) {
          return utf8Response(jsonEncode(tokens(roles: ['Driver'])), 200);
        }
        if (request.url.path == '/api/auth/me') {
          return utf8Response(
            jsonEncode(tokens(roles: ['Driver'])['user']),
            200,
          );
        }
        if (request.url.path.endsWith('logout')) return http.Response('', 204);
        return utf8Response('{"title":"driver_profile_not_found"}', 404);
      }),
    );
    await expectLater(
      api.verify(challenge(), '123456'),
      throwsA(
        isA<AuthFailure>().having((e) => e.code, 'code', 'profile_load_failed'),
      ),
    );
    expect(api.session, isNull);
    expect(api.currentProfile, isNull);
  });
  test('Vietnamese phone input normalizes and rejects invalid prefixes', () {
    for (final value in [
      '0900000001',
      '900000001',
      '+84900000001',
      '090 000 0001',
    ]) {
      expect(normalizePhone(value), '+84900000001');
    }
    for (final value in ['', '0123456789', '+11234567890', '09000']) {
      expect(() => normalizePhone(value), throwsA(isA<AuthFailure>()));
    }
  });
  test(
    'registration sends name and normalized phone; uses server cooldown',
    () async {
      final api = AuthApi(
        role: AppRole.rider,
        store: MemoryStore(),
        baseUrl: 'http://test',
        client: MockClient((request) async {
          expect(request.url.path, '/api/auth/register/request-otp');
          expect(jsonDecode(request.body), {
            'fullName': 'Nguyễn An',
            'phoneNumber': '+84900000001',
          });
          return utf8Response(
            jsonEncode({
              'challengeId': 'id',
              'expiresInSeconds': 120,
              'retryAfterSeconds': 30,
            }),
            200,
          );
        }),
      );
      final result = await api.requestOtp(
        '0900000001',
        fullName: ' Nguyễn An ',
      );
      expect(result.id, 'id');
      expect(
        result.resendAt.difference(DateTime.now()).inSeconds,
        inInclusiveRange(28, 30),
      );
    },
  );
  test(
    'driver app rejects Rider-only tokens and revokes the issued session',
    () async {
      final calls = <String>[];
      final store = MemoryStore();
      final api = AuthApi(
        role: AppRole.driver,
        store: store,
        baseUrl: 'http://test',
        client: MockClient((request) async {
          calls.add(request.url.path);
          if (request.url.path.endsWith('logout')) {
            return utf8Response('', 204);
          }
          expect(jsonDecode(request.body)['clientApp'], 'DriverApp');
          return utf8Response(jsonEncode(tokens()), 200);
        }),
      );
      await expectLater(
        api.verify(challenge(), '123456'),
        throwsA(
          isA<AuthFailure>().having((e) => e.code, 'code', 'role_required'),
        ),
      );
      expect(api.session, isNull);
      expect(store.value, isNull);
      expect(calls.last, '/api/auth/logout');
    },
  );
  test('OTP errors are localized and never create a session', () async {
    final api = AuthApi(
      role: AppRole.rider,
      store: MemoryStore(),
      baseUrl: 'http://test',
      client: MockClient(
        (_) async => utf8Response('{"title":"invalid_otp"}', 400),
      ),
    );
    await expectLater(
      api.verify(challenge(), '000000'),
      throwsA(
        isA<AuthFailure>().having(
          (e) => e.message,
          'message',
          contains('chưa đúng'),
        ),
      ),
    );
    expect(api.session, isNull);
  });
  test('simultaneous profile calls share one token refresh', () async {
    var refreshes = 0;
    final api = AuthApi(
      role: AppRole.rider,
      store: MemoryStore(),
      baseUrl: 'http://test',
      client: MockClient((request) async {
        if (request.url.path.endsWith('refresh')) {
          refreshes++;
          await Future<void>.delayed(const Duration(milliseconds: 20));
          return utf8Response(jsonEncode(tokens()), 200);
        }
        return utf8Response(jsonEncode(tokens()['user']), 200);
      }),
    );
    api.session = AuthSession.fromJson(tokens(expired: true));
    final users = await Future.wait([
      api.profile(),
      api.profile(),
      api.profile(),
    ]);
    expect(refreshes, 1);
    expect(users.every((u) => u.fullName == 'Nguyễn An'), isTrue);
  });
  test('lost refresh response clears old token without retry', () async {
    var calls = 0;
    final store = MemoryStore()..value = 'old';
    final api = AuthApi(
      role: AppRole.rider,
      store: store,
      baseUrl: 'http://test',
      client: MockClient((_) async {
        calls++;
        throw http.ClientException('offline');
      }),
    );
    api.session = AuthSession.fromJson(tokens(expired: true));
    await expectLater(api.profile(), throwsA(isA<AuthFailure>()));
    expect(calls, 1);
    expect(api.session, isNull);
    expect(store.value, isNull);
  });
  test('logout removes local session even if server is unavailable', () async {
    final store = MemoryStore()..value = 'old';
    final api = AuthApi(
      role: AppRole.rider,
      store: store,
      baseUrl: 'http://test',
      client: MockClient(
        (_) async => utf8Response('{"title":"unavailable"}', 503),
      ),
    );
    api.session = AuthSession.fromJson(tokens());
    await expectLater(api.logout(), throwsA(isA<AuthFailure>()));
    expect(api.session, isNull);
    expect(store.value, isNull);
  });
  test('restore verifies saved user against server', () async {
    final store = MemoryStore()..value = jsonEncode(tokens());
    final api = AuthApi(
      role: AppRole.rider,
      store: store,
      baseUrl: 'http://test',
      client: MockClient((request) async {
        expect(request.headers['Authorization'], 'Bearer access');
        return utf8Response(jsonEncode(tokens()['user']), 200);
      }),
    );
    await api.restore();
    expect(api.session!.user.fullName, 'Nguyễn An');
  });
}

http.Response utf8Response(String body, int status) => http.Response(
  body,
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);
