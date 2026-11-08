import 'dart:math';
import 'dart:convert';

import 'auth.dart';
import 'trip_models.dart';
import 'trip_realtime.dart';

class TripApi {
  TripApi(this.auth, {SessionStore? pendingStore})
    : _pendingStore = pendingStore;

  final AuthApi auth;
  final SessionStore? _pendingStore;
  SessionStore get pendingStore =>
      _pendingStore ??
      SecureSessionStore(
        AppRole.rider,
        namespace: 'booking.${auth.session?.user.phone ?? "anonymous"}',
      );

  TripRealtimeClient createRealtime() => SignalRTripRealtime(auth);

  Future<PendingTripBooking?> readPending() async {
    final value = await pendingStore.read();
    if (value == null) return null;
    final data = jsonDecode(value) as Map<String, dynamic>;
    return PendingTripBooking(
      data['key'] as String,
      Map<String, Object>.from(data['request'] as Map),
    );
  }

  Future<void> savePending(PendingTripBooking pending) => pendingStore.write(
    jsonEncode({'key': pending.key, 'request': pending.request}),
  );
  Future<void> clearPending() => pendingStore.clear();

  Future<TripSnapshot> createTrip({
    required Map<String, Object> request,
    required String idempotencyKey,
  }) async {
    final value = await auth.requestAuthenticatedJson(
      'trips',
      method: 'POST',
      headers: {'Idempotency-Key': idempotencyKey},
      body: request,
    );
    return _snapshot(value);
  }

  Future<TripSnapshot?> current() async {
    final value = await auth.requestAuthenticatedJson('trips/current');
    return value == null ? null : _snapshot(value);
  }

  Future<TripSnapshot> get(String tripId) async =>
      _snapshot(await auth.requestAuthenticatedJson('trips/$tripId'));

  Future<TripRouteSnapshot?> route(String tripId) async {
    final value = await auth.requestAuthenticatedJson('trips/$tripId/route');
    if (value == null) return null;
    if (value is! Map<String, dynamic>) {
      throw const AuthFailure('Thông tin tuyến đường không hợp lệ.');
    }
    return TripRouteSnapshot.fromJson(value);
  }

  Future<TripHistoryPage> history({int page = 1, int pageSize = 20}) async {
    final value = await auth.requestAuthenticatedJson(
      'trips/history',
      query: {'page': '$page', 'pageSize': '$pageSize'},
    );
    if (value is! Map<String, dynamic> ||
        value['items'] is! List ||
        value['total'] is! int) {
      throw const AuthFailure('Lịch sử chuyến đi không hợp lệ.');
    }
    final items = value['items'] as List;
    return TripHistoryPage(
      total: value['total'] as int,
      items: items.map((item) => _snapshot(item)).toList(growable: false),
    );
  }

  Future<TripSnapshot> approve(
    TripSnapshot trip,
    TripProposalSnapshot proposal,
  ) => _proposalAction(trip, proposal, 'approve');

  Future<TripSnapshot> reject(
    TripSnapshot trip,
    TripProposalSnapshot proposal,
  ) => _proposalAction(trip, proposal, 'reject');

  Future<TripSnapshot> _proposalAction(
    TripSnapshot trip,
    TripProposalSnapshot proposal,
    String action,
  ) async => _snapshot(
    await auth.requestAuthenticatedJson(
      'trips/${trip.id}/proposals/${proposal.id}/$action',
      method: 'POST',
      body: {'stateVersion': trip.stateVersion},
    ),
  );

  Future<TripSnapshot> cancel(TripSnapshot trip) async => _snapshot(
    await auth.requestAuthenticatedJson(
      'trips/${trip.id}/cancel',
      method: 'POST',
      body: {'stateVersion': trip.stateVersion},
    ),
  );

  Future<Map<String, dynamic>> driverAvailability() async =>
      Map<String, dynamic>.from(
        await auth.requestAuthenticatedJson('matching/drivers/me/availability')
            as Map,
      );

  Future<void> availability(
    bool online, {
    double? latitude,
    double? longitude,
  }) async {
    await auth.requestAuthenticatedJson(
      'matching/drivers/me/availability',
      method: 'PUT',
      body: {'online': online, 'latitude': ?latitude, 'longitude': ?longitude},
    );
  }

  Future<void> updateGps(double latitude, double longitude) async {
    await auth.requestAuthenticatedJson(
      'matching/drivers/me/location',
      method: 'PUT',
      body: {'latitude': latitude, 'longitude': longitude},
    );
  }

  Future<TripSnapshot> driverAnswer(
    String id,
    String proposalId,
    int version, {
    required bool accept,
  }) async => _snapshot(
    await auth.requestAuthenticatedJson(
      'trips/$id/${accept ? "driver-accept" : "driver-reject"}',
      method: 'POST',
      body: {'proposalId': proposalId, 'stateVersion': version},
    ),
  );
  Future<TripSnapshot> driverStep(String id, int version, String step) async =>
      _snapshot(
        await auth.requestAuthenticatedJson(
          'trips/$id/$step',
          method: 'POST',
          body: {'stateVersion': version},
        ),
      );
  Future<Map<String, dynamic>?> payment(String tripId) async {
    final data = await auth.requestAuthenticatedJson('payments/trips/$tripId');
    return data is Map<String, dynamic> ? data : null;
  }

  Future<Object?> earnings() =>
      auth.requestAuthenticatedJson('payments/drivers/me/earnings');

  static TripSnapshot _snapshot(Object? value) {
    if (value is! Map<String, dynamic>) {
      throw const AuthFailure('Thông tin chuyến đi không hợp lệ.');
    }
    try {
      return TripSnapshot.fromJson(value);
    } on FormatException {
      throw const AuthFailure('Thông tin chuyến đi không hợp lệ.');
    }
  }
}

class PendingTripBooking {
  PendingTripBooking(this.key, Map<String, Object> request)
    : request = Map.unmodifiable(request);
  final String key;
  final Map<String, Object> request;
}

class TripHistoryPage {
  const TripHistoryPage({required this.total, required this.items});

  final int total;
  final List<TripSnapshot> items;
}

String newIdempotencyKey() {
  final random = Random.secure();
  final bytes = List<int>.generate(16, (_) => random.nextInt(256));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  final hex = bytes
      .map((byte) => byte.toRadixString(16).padLeft(2, '0'))
      .join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}
