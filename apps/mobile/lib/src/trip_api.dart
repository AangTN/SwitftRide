import 'dart:math';

import 'auth.dart';
import 'trip_models.dart';

class TripApi {
  const TripApi(this.auth);

  final AuthApi auth;

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
  final hex = bytes.map((byte) => byte.toRadixString(16).padLeft(2, '0')).join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}
