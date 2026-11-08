import 'dart:async';

import 'package:signalr_netcore/signalr_client.dart';

import 'auth.dart';

class TripRealtimeEvent {
  const TripRealtimeEvent(this.name, this.data);
  final String name;
  final Map<String, dynamic> data;
}

abstract interface class TripRealtimeClient {
  Stream<TripRealtimeEvent> get events;
  Future<void> watch(String? tripId);
  Future<void> dispose();
}

class SignalRTripRealtime implements TripRealtimeClient {
  SignalRTripRealtime(AuthApi auth, {bool keepAlive = false})
    : _keepAlive = keepAlive {
    _hub = HubConnectionBuilder()
        .withUrl(
          '${auth.baseUrl}/api/trips/realtime',
          options: HttpConnectionOptions(
            accessTokenFactory: auth.realtimeAccessToken,
            requestTimeout: 10000,
          ),
        )
        .withAutomaticReconnect(retryDelays: [0, 2000, 10000, 30000])
        .build();
    for (final name in [
      'TripChanged',
      'DriverInvitation',
      'DriverLocationUpdated',
      'TripRouteProgress',
      'TripRoutePreview',
      'TripRoutesUpdated',
      'RideCompleted',
    ]) {
      _hub.on(name, (arguments) {
        if (_disposed ||
            arguments == null ||
            arguments.isEmpty ||
            arguments.first is! Map) {
          return;
        }
        _events.add(
          TripRealtimeEvent(
            name,
            Map<String, dynamic>.from(arguments.first as Map),
          ),
        );
      });
    }
    _hub.onreconnecting(({error}) => _notify('disconnected'));
    _hub.onreconnected(({connectionId}) => unawaited(_recover()));
    _hub.onclose(({error}) {
      _notify('disconnected');
      _scheduleRetry();
    });
  }

  final bool _keepAlive;
  late final HubConnection _hub;
  final _events = StreamController<TripRealtimeEvent>.broadcast();
  String? _tripId;
  Future<void>? _connecting;
  Timer? _retry;
  bool _disposed = false;

  @override
  Stream<TripRealtimeEvent> get events => _events.stream;

  void _notify(String name) {
    if (!_disposed) _events.add(TripRealtimeEvent(name, const {}));
  }

  @override
  Future<void> watch(String? tripId) async {
    if (_disposed) return;
    final previous = _tripId;
    _tripId = tripId;
    if (previous != null &&
        previous != tripId &&
        _hub.state == HubConnectionState.Connected) {
      try {
        await _hub.invoke('UnsubscribeTrip', args: [previous]);
      } catch (_) {}
    }
    if (tripId == null && !_keepAlive) {
      _retry?.cancel();
      return;
    }
    await (_connecting ??= _connect().whenComplete(() => _connecting = null));
  }

  Future<void> _connect() async {
    try {
      if (_hub.state == HubConnectionState.Disconnected) await _hub.start();
      if (_hub.state == HubConnectionState.Connected) await _join();
    } catch (_) {
      _notify('disconnected');
      _scheduleRetry();
    }
  }

  Future<void> _join() async {
    final tripId = _tripId;
    if (_disposed) return;
    if (tripId == null) {
      if (_keepAlive) _notify('connected');
      return;
    }
    await _hub.invoke('SubscribeTrip', args: [tripId]);
    if (!_disposed && _tripId == tripId) _notify('connected');
  }

  Future<void> _recover() async {
    try {
      await _join();
    } catch (_) {
      _notify('disconnected');
      _scheduleRetry();
    }
  }

  void _scheduleRetry() {
    if (_disposed ||
        (_tripId == null && !_keepAlive) ||
        _retry?.isActive == true) {
      return;
    }
    _retry = Timer(const Duration(seconds: 5), () => unawaited(watch(_tripId)));
  }

  @override
  Future<void> dispose() async {
    _disposed = true;
    _retry?.cancel();
    await _hub.stop();
    await _events.close();
  }
}
