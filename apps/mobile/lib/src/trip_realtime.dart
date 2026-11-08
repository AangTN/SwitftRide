import 'dart:async';
import 'package:signalr_netcore/signalr_client.dart';
import 'auth.dart';

class TripRealtime {
  TripRealtime(this.auth);
  final AuthApi auth;
  HubConnection? _hub;
  final _events = StreamController<TripRealtimeEvent>.broadcast();
  String? _subscribedTripId;
  bool _connecting = false;
  Stream<TripRealtimeEvent> get events => _events.stream;
  bool get connected => _hub?.state == HubConnectionState.Connected;

  Future<void> connect() async {
    if (connected || _connecting) return;
    _connecting = true;
    try {
      final token = auth.session?.accessToken;
      if (token == null) throw const AuthFailure('Vui lòng đăng nhập lại.');
      final base = auth.baseUrl;
      _hub ??= HubConnectionBuilder()
          .withUrl('$base/api/trips/realtime', options: HttpConnectionOptions(
            accessTokenFactory: () async => auth.session?.accessToken ?? token,
          ))
          .withAutomaticReconnect()
          .build();
      _hub!
        ..on('DriverInvitation', (args) {
          if (args != null && args.isNotEmpty && args.first is Map) {
            _events.add(TripRealtimeEvent('DriverInvitation', Map<String, dynamic>.from(args.first as Map)));
          }
        })
        ..on('TripChanged', (args) => _emit('TripChanged', args))
        ..on('TripRoutePreview', (args) => _emit('TripRoutePreview', args))
        ..on('TripRoutesUpdated', (args) => _emit('TripRoutesUpdated', args))
        ..on('DriverLocationUpdated', (args) => _emit('DriverLocationUpdated', args))
        ..on('RideCompleted', (args) => _emit('RideCompleted', args))
        ..onreconnecting(({error}) { _events.add(TripRealtimeEvent('Reconnecting', {'error': '$error'})); })
        ..onreconnected(({connectionId}) {
          _events.add(TripRealtimeEvent('Reconnected', {'connectionId': '$connectionId'}));
          final tripId = _subscribedTripId;
          if (tripId != null) unawaited(_hub!.invoke('SubscribeTrip', args: [tripId]));
        })
        ..onclose(({error}) { _events.add(TripRealtimeEvent('Disconnected', {'error': '$error'})); });
      await _hub!.start();
      if (_subscribedTripId != null) await subscribeTrip(_subscribedTripId!);
    } finally {
      _connecting = false;
    }
  }

  void _emit(String name, List<Object?>? args) {
    if (args != null && args.isNotEmpty && args.first is Map) {
      _events.add(TripRealtimeEvent(name, Map<String, dynamic>.from(args.first as Map)));
    }
  }

  Future<void> subscribeTrip(String tripId) async {
    _subscribedTripId = tripId;
    if (!connected) await connect();
    await _hub!.invoke('SubscribeTrip', args: [tripId]);
  }

  Future<void> dispose() async {
    await _hub?.stop();
    await _events.close();
  }
}

class TripRealtimeEvent {
  const TripRealtimeEvent(this.name, this.data);
  final String name;
  final Map<String, dynamic> data;
}
