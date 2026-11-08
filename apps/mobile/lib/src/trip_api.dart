import 'dart:convert';
import 'dart:math';
import 'auth.dart';
import 'trip_models.dart';

class TripApi {
  TripApi(this.auth);
  final AuthApi auth;
  String? _pendingIdempotencyKey;
  String? _pendingFingerprint;

  Future<TripSnapshot?> current() async {
    final result = await auth.getAuthenticatedJson('trips/current');
    return result is Map<String, dynamic> ? TripSnapshot.fromJson(result) : null;
  }

  Future<TripSnapshot> get(String tripId) async => _snapshot(
    await auth.getAuthenticatedJson('trips/$tripId'),
  );

  Future<TripSnapshot> create(Map<String, Object> request, {String? idempotencyKey}) async {
    final fingerprint = jsonEncode(request);
    if (idempotencyKey != null) {
      _pendingIdempotencyKey = idempotencyKey;
      _pendingFingerprint = fingerprint;
    } else if (_pendingIdempotencyKey == null || _pendingFingerprint != fingerprint) {
      _pendingIdempotencyKey = '${DateTime.now().microsecondsSinceEpoch}-${Random().nextInt(1 << 32)}';
      _pendingFingerprint = fingerprint;
    }
    final key = _pendingIdempotencyKey!;
    final result = await auth.sendAuthenticatedJson(
      'trips', body: request, headers: {'Idempotency-Key': key},
    );
    _pendingIdempotencyKey = null;
    _pendingFingerprint = null;
    return _snapshot(result);
  }

  Future<TripRoute?> getRoute(String tripId) async {
    final result = await auth.getAuthenticatedJson('trips/$tripId/route');
    return result is Map<String, dynamic> ? TripRoute.fromJson(result) : null;
  }

  Future<TripSnapshot> approve(String tripId, String proposalId, int version) => _action(
    'trips/$tripId/proposals/$proposalId/approve', {'stateVersion': version});
  Future<TripSnapshot> reject(String tripId, String proposalId, int version) => _action(
    'trips/$tripId/proposals/$proposalId/reject', {'stateVersion': version});
  Future<TripSnapshot> cancel(String tripId, int version) => _action(
    'trips/$tripId/cancel', {'stateVersion': version});
  Future<TripSnapshot> driverAnswer(String tripId, String proposalId, int version, {required bool accept}) => _action(
    'trips/$tripId/${accept ? 'driver-accept' : 'driver-reject'}',
    {'proposalId': proposalId, 'stateVersion': version});
  Future<TripSnapshot> driverStep(String tripId, int version, String step) => _action(
    'trips/$tripId/$step', {'stateVersion': version});

  Future<List<TripSnapshot>> history({int page = 1}) async {
    final result = await auth.getAuthenticatedJson('trips/history', query: {'page': '$page', 'pageSize': '30'});
    if (result is! Map<String, dynamic>) return const [];
    return (result['items'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(TripSnapshot.fromJson).toList();
  }

  Future<void> availability(bool online, {double? latitude, double? longitude}) async {
    await auth.sendAuthenticatedJson('matching/drivers/me/availability', method: 'PUT', body: {
      'online': online,
      'latitude': ?latitude,
      'longitude': ?longitude,
    });
  }

  Future<void> updateGps(double latitude, double longitude) async {
    await auth.sendAuthenticatedJson('matching/drivers/me/location', method: 'PUT', body: {
      'latitude': latitude, 'longitude': longitude,
    });
  }

  Future<Object?> earnings() => auth.getAuthenticatedJson('payments/drivers/me/earnings');

  Future<TripSnapshot> _action(String path, Map<String, dynamic> body) async =>
      _snapshot(await auth.sendAuthenticatedJson(path, body: body));

  TripSnapshot _snapshot(Object? result) {
    if (result is! Map<String, dynamic>) throw const AuthFailure('Dữ liệu chuyến đi không hợp lệ.');
    return TripSnapshot.fromJson(result);
  }
}
