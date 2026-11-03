import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'package:swiftride_mobile/src/places_api.dart';
import 'auth_test.dart' show MemoryStore, tokens;

void main() {
  test(
    'search and details use bearer token and retain selected address/coordinates',
    () async {
      final requests = <http.Request>[];
      final auth = AuthApi(
        role: AppRole.rider,
        baseUrl: 'http://test',
        store: MemoryStore(),
        client: MockClient((r) async {
          requests.add(r);
          expect(r.headers['Authorization'], 'Bearer access');
          if (r.url.path.endsWith('/search')) {
            expect(r.url.queryParameters['input'], 'Đại học Sài Gòn');
            return reply([
              {
                'description': 'Đại học Sài Gòn, cơ sở 1',
                'placeId': 'campus/+==',
              },
            ]);
          }
          expect(r.url.path, '/api/matching/places/details');
          expect(r.url.queryParameters['placeId'], 'campus/+==');
          return reply({
            'placeId': 'campus/+==',
            'name': 'Đại học Sài Gòn',
            'formattedAddress': '273 An Dương Vương, Chợ Quán, Hồ Chí Minh',
            'latitude': 10.759,
            'longitude': 106.682,
          });
        }),
      );
      addTearDown(auth.dispose);
      auth.session = AuthSession.fromJson(tokens());
      final api = PlacesApi(auth);
      final suggestions = await api.search('Đại học Sài Gòn');
      final place = await api.details(suggestions.single.placeId);
      expect(place.name, 'Đại học Sài Gòn');
      expect(place.address, '273 An Dương Vương, Chợ Quán, Hồ Chí Minh');
      expect(place.point!.latitude, 10.759);
      expect(place.point!.longitude, closeTo(106.682, 1e-10));
      expect(place.isResolved, isTrue);
      expect(requests.length, 2);
    },
  );

  test(
    'expired session refreshes before place lookup without exposing Goong key',
    () async {
      final auth = AuthApi(
        role: AppRole.rider,
        baseUrl: 'http://test',
        store: MemoryStore(),
        client: MockClient((r) async {
          if (r.url.path == '/api/auth/refresh') {
            return reply({...tokens(), 'accessToken': 'new-access'});
          }
          expect(r.headers['Authorization'], 'Bearer new-access');
          expect(r.url.queryParameters.containsKey('api_key'), isFalse);
          return reply([]);
        }),
      );
      addTearDown(auth.dispose);
      auth.session = AuthSession.fromJson(tokens(expired: true));
      expect(await PlacesApi(auth).search('Campus'), isEmpty);
    },
  );

  test('401 triggers one token refresh and one retry', () async {
    var lookups = 0;
    var refreshes = 0;
    final auth = AuthApi(
      role: AppRole.rider,
      baseUrl: 'http://test',
      store: MemoryStore(),
      client: MockClient((r) async {
        if (r.url.path == '/api/auth/refresh') {
          refreshes++;
          return reply({...tokens(), 'accessToken': 'new-access'});
        }
        if (++lookups == 1) return reply({'title': 'unauthorized'}, 401);
        expect(r.headers['Authorization'], 'Bearer new-access');
        return reply([]);
      }),
    );
    addTearDown(auth.dispose);
    auth.session = AuthSession.fromJson(tokens());
    await PlacesApi(auth).search('Campus');
    expect(lookups, 2);
    expect(refreshes, 1);
  });

  test('details reject mismatched or missing coordinates', () async {
    for (final data in [
      {
        'placeId': 'other',
        'name': 'Campus',
        'formattedAddress': 'Street',
        'latitude': 10,
        'longitude': 106,
      },
      {
        'placeId': 'chosen',
        'name': 'Campus',
        'formattedAddress': 'Street',
        'latitude': 100,
        'longitude': 106,
      },
      {'placeId': 'chosen', 'name': 'Campus', 'formattedAddress': 'Street'},
    ]) {
      final auth = AuthApi(
        role: AppRole.rider,
        baseUrl: 'http://test',
        store: MemoryStore(),
        client: MockClient((_) async => reply(data)),
      );
      auth.session = AuthSession.fromJson(tokens());
      await expectLater(
        PlacesApi(auth).details('chosen'),
        throwsA(isA<AuthFailure>()),
      );
      auth.dispose();
    }
  });
}

http.Response reply(Object data, [int status = 200]) => http.Response(
  jsonEncode(data),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);
