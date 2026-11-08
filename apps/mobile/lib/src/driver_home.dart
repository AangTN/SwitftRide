import 'dart:async';
import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';
import 'package:maplibre_gl/maplibre_gl.dart';
import 'package:url_launcher/url_launcher.dart';
import 'app.dart' show Brand, green, muted;
import 'auth.dart';
import 'goong_map.dart';
import 'trip_api.dart';
import 'trip_models.dart';
import 'trip_realtime.dart';

class DriverHome extends StatefulWidget {
  const DriverHome({
    super.key,
    required this.api,
    required this.fullName,
    required this.account,
    this.tripApi,
    this.realtime,
    this.now,
  });
  final AuthApi api;
  final String fullName;
  final Widget account;
  final TripApi? tripApi;
  final TripRealtimeClient? realtime;
  final DateTime Function()? now;
  @override
  State<DriverHome> createState() => _DriverHomeState();
}

class _DriverHomeState extends State<DriverHome> with WidgetsBindingObserver {
  late final TripApi trips = widget.tripApi ?? TripApi(widget.api);
  late final TripRealtimeClient realtime =
      widget.realtime ?? SignalRTripRealtime(widget.api, keepAlive: true);
  StreamSubscription<TripRealtimeEvent>? events;
  StreamSubscription<Position>? gps;
  TripSnapshot? trip;
  TripRouteSnapshot? route;
  LatLng? driverLocation;
  Future<Object?>? earningsFuture;
  Future<TripHistoryPage>? historyFuture;
  Timer? heartbeat;
  int generation = 0;
  bool accountConnected = false;
  bool pendingInvitation = false;
  String? watchedTrip;
  Future<void> _watch(String? id) async {
    if (accountConnected && watchedTrip == id) return;
    accountConnected = true;
    watchedTrip = id;
    await realtime.watch(id);
  }

  bool restoring = false,
      refreshing = false,
      refreshAgain = false,
      sendingGps = false;
  bool online = false, busy = false, loading = true;
  String? error;
  DateTime? invitationExpiry;
  Timer? clock;
  int tab = 0;
  DateTime _now() => widget.now?.call() ?? DateTime.now();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    earningsFuture = null;
    historyFuture = null;
    events = realtime.events.listen(_onEvent);
    clock = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
      if (invitationExpiry != null && !invitationExpiry!.isAfter(_now())) {
        invitationExpiry = null;
        unawaited(_restore());
        if (mounted) setState(() {});
      }
    });
    _restore();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) unawaited(_restore());
  }

  bool _assigned(TripSnapshot t) => {
    TripStatus.driverAccepted,
    TripStatus.enRoute,
    TripStatus.pickedUp,
  }.contains(t.status);
  void _adopt(TripSnapshot value) {
    if (!mounted ||
        (trip?.id == value.id && trip!.stateVersion > value.stateVersion)) {
      return;
    }
    setState(() {
      trip = value;
      if (route?.matches(value) != true) route = null;
      invitationExpiry = value.status == TripStatus.awaitingDriverResponse
          ? value.currentProposal?.driverResponseDeadline
          : null;
      error = null;
    });
    unawaited(_watch(_assigned(value) ? value.id : null));
  }

  Future<void> _restore() async {
    if (restoring || busy) return;
    restoring = true;
    final request = ++generation;
    try {
      final availability = await trips.driverAvailability();
      await _watch(trip != null && _assigned(trip!) ? trip!.id : null);
      final current = await trips.current();
      if (!mounted || request != generation) return;
      online = {
        'Available',
        'Reserved',
        'Busy',
      }.contains(availability['status']);
      if (current != null) {
        _adopt(current);
        await _loadRoute();
      } else if (trip == null || trip!.status != TripStatus.paymentPending) {
        setState(() {
          trip = null;
          route = null;
          invitationExpiry = null;
        });
        await _watch(null);
      }
      if (online || (trip != null && _assigned(trip!))) {
        await _startGpsStream(requestPermission: false);
      }
    } catch (e) {
      if (mounted) setState(() => error = '$e');
    } finally {
      restoring = false;
      if (mounted) setState(() => loading = false);
    }
  }

  void _onEvent(TripRealtimeEvent event) {
    if (!mounted) return;
    if (event.name == 'DriverInvitation') {
      if (busy) {
        pendingInvitation = true;
        return;
      }
      final id = event.data['tripId']?.toString();
      final proposal = event.data['proposalId']?.toString();
      final expires = DateTime.tryParse('${event.data['expiresAt']}');
      if (id == null ||
          proposal == null ||
          expires == null ||
          !expires.isAfter(_now())) {
        return;
      }
      if (trip != null && _assigned(trip!) && trip!.id != id) return;
      unawaited(_loadTrip(id, invitationProposal: proposal));
    } else if (event.name == 'connected') {
      unawaited(_restore());
    } else if (event.name == 'disconnected') {
      setState(
        () => error = 'Mất kết nối cập nhật trực tiếp. Đang kết nối lại…',
      );
    } else if ({
          'TripChanged',
          'RideCompleted',
          'TripRoutesUpdated',
          'TripRoutePreview',
        }.contains(event.name) &&
        event.data['tripId'] == trip?.id) {
      unawaited(_refreshTrip());
    }
  }

  Future<void> _loadTrip(String id, {String? invitationProposal}) async {
    final request = ++generation;
    try {
      final value = await trips.get(id);
      if (!mounted || request != generation) return;
      if (invitationProposal != null &&
          (value.status != TripStatus.awaitingDriverResponse ||
              value.currentProposal?.id != invitationProposal ||
              value.currentProposal?.driverResponseDeadline?.isAfter(_now()) !=
                  true)) {
        return;
      }
      _adopt(value);
      await _loadRoute();
    } catch (e) {
      if (mounted && request == generation) setState(() => error = '$e');
    }
  }

  Future<void> _refreshTrip() async {
    if (refreshing) {
      refreshAgain = true;
      return;
    }
    refreshing = true;
    try {
      if (trip != null) await _loadTrip(trip!.id);
    } finally {
      refreshing = false;
      if (refreshAgain && mounted) {
        refreshAgain = false;
        unawaited(_refreshTrip());
      }
    }
  }

  Future<void> _loadRoute() async {
    final current = trip;
    if (current == null ||
        !current.status.isActive ||
        current.status == TripStatus.paymentPending) {
      return;
    }
    try {
      final value = await trips.route(current.id);
      final latest = trip;
      if (!mounted ||
          value == null ||
          latest == null ||
          latest.id != current.id ||
          !value.matches(latest)) {
        return;
      }
      final oldTime = route?.forTripTimestamp(latest),
          newTime = value.forTripTimestamp(latest);
      if (oldTime != null && (newTime == null || newTime.isBefore(oldTime))) {
        return;
      }
      value.forTrip(latest)?.decode();
      setState(() => route = value);
    } catch (_) {
      /* Preserve the valid route during recovery. */
    }
  }

  Future<void> _sendGps() async {
    if (!mounted || sendingGps) return;
    sendingGps = true;
    try {
      final p = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          accuracy: LocationAccuracy.high,
          timeLimit: Duration(seconds: 15),
        ),
      );
      if (!mounted) return;
      setState(() => driverLocation = LatLng(p.latitude, p.longitude));
      await trips.updateGps(p.latitude, p.longitude);
    } catch (_) {
      if (mounted) {
        setState(
          () => error =
              'Chưa cập nhật được GPS. Kiểm tra quyền vị trí và kết nối.',
        );
      }
    } finally {
      sendingGps = false;
    }
  }

  Future<Position> _position() async {
    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied ||
        permission == LocationPermission.deniedForever) {
      throw Exception('Cần cấp quyền vị trí để bật nhận chuyến.');
    }
    if (!await Geolocator.isLocationServiceEnabled()) {
      throw Exception('Hãy bật GPS trên thiết bị.');
    }
    return Geolocator.getCurrentPosition(
      locationSettings: const LocationSettings(
        accuracy: LocationAccuracy.high,
        timeLimit: Duration(seconds: 15),
      ),
    );
  }

  Future<void> _startGpsStream({required bool requestPermission}) async {
    if (heartbeat?.isActive == true) return;
    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied && requestPermission) {
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied ||
        permission == LocationPermission.deniedForever) {
      if (mounted) {
        setState(() => error = 'Hãy cấp quyền GPS để tiếp tục nhận chuyến.');
      }
      return;
    }
    if (!mounted) return;
    heartbeat = Timer.periodic(
      const Duration(seconds: 20),
      (_) => unawaited(_sendGps()),
    );
    unawaited(_sendGps());
    gps =
        Geolocator.getPositionStream(
          locationSettings: const LocationSettings(
            accuracy: LocationAccuracy.high,
            distanceFilter: 15,
          ),
        ).listen(
          (position) {
            if (!mounted) return;
            setState(
              () => driverLocation = LatLng(
                position.latitude,
                position.longitude,
              ),
            );
            unawaited(_sendGps());
          },
          onError: (Object e) {
            if (mounted) {
              setState(
                () => error = 'Không đọc được GPS. Kiểm tra quyền vị trí.',
              );
            }
          },
        );
  }

  Future<void> _toggleOnline() async {
    if (busy) return;
    setState(() {
      busy = true;
      error = null;
    });
    try {
      if (!online) {
        final p = await _position();
        await _watch(null);
        await trips.availability(
          true,
          latitude: p.latitude,
          longitude: p.longitude,
        );
        await trips.updateGps(p.latitude, p.longitude);
        driverLocation = LatLng(p.latitude, p.longitude);
        await _startGpsStream(requestPermission: false);
        online = true;
      } else {
        await trips.availability(false);
        heartbeat?.cancel();
        heartbeat = null;
        await gps?.cancel();
        gps = null;
        online = false;
      }
    } catch (e) {
      error = '$e';
    }
    if (mounted) setState(() => busy = false);
  }

  Future<void> _answer(bool accept) async {
    final t = trip, proposal = t?.currentProposal;
    if (t == null || proposal == null || busy) return;
    ++generation;
    setState(() => busy = true);
    try {
      final updated = await trips.driverAnswer(
        t.id,
        proposal.id,
        t.stateVersion,
        accept: accept,
      );
      if (!mounted) return;
      if (trip?.id != t.id || trip?.currentProposal?.id != proposal.id) {
        setState(() => busy = false);
        return;
      }
      invitationExpiry = null;
      if (accept) {
        _adopt(updated);
        await _loadRoute();
      } else {
        trip = null;
        route = null;
        await _watch(null);
      }
    } catch (e) {
      error = '$e';
      await _refreshTrip();
    }
    if (mounted) {
      setState(() => busy = false);
      if (pendingInvitation) {
        pendingInvitation = false;
        await _restore();
      }
    }
  }

  Future<void> _step(String step) async {
    final t = trip;
    if (t == null || busy) return;
    ++generation;
    setState(() => busy = true);
    try {
      final updated = await trips.driverStep(t.id, t.stateVersion, step);
      if (!mounted) return;
      if (trip?.id != t.id) {
        setState(() => busy = false);
        return;
      }
      _adopt(updated);
      await _loadRoute();
      historyFuture = null;
      earningsFuture = null;
    } catch (e) {
      error = '$e';
      await _refreshTrip();
    }
    if (mounted) {
      setState(() => busy = false);
      if (pendingInvitation) {
        pendingInvitation = false;
        await _restore();
      }
    }
  }

  void _dismiss() {
    ++generation;
    setState(() {
      trip = null;
      route = null;
      invitationExpiry = null;
      error = null;
      historyFuture = null;
      earningsFuture = null;
    });
    unawaited(_watch(null));
    unawaited(_restore());
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    heartbeat?.cancel();
    clock?.cancel();
    events?.cancel();
    gps?.cancel();
    realtime.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (loading) {
      return const Scaffold(body: Center(child: CircularProgressIndicator()));
    }
    return Scaffold(
      body: IndexedStack(
        index: tab,
        children: [
          _dashboard(),
          _history(),
          _income(),
          SingleChildScrollView(child: widget.account),
        ],
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: tab,
        onDestinationSelected: (v) => setState(() {
          tab = v;
          if (v == 1) historyFuture = null;
          if (v == 2) earningsFuture = null;
        }),
        destinations: const [
          NavigationDestination(
            icon: Icon(Icons.home_outlined),
            label: 'Trang chủ',
          ),
          NavigationDestination(icon: Icon(Icons.history), label: 'Chuyến đi'),
          NavigationDestination(icon: Icon(Icons.bar_chart), label: 'Thu nhập'),
          NavigationDestination(
            icon: Icon(Icons.person_outline),
            label: 'Tài khoản',
          ),
        ],
      ),
    );
  }

  Widget _dashboard() => Stack(
    children: [
      Positioned.fill(
        child: GoongMap(
          routePoints: trip == null
              ? const []
              : route?.forTrip(trip!)?.decode() ?? const [],
          pickupPoint: trip == null
              ? null
              : LatLng(trip!.pickupLatitude, trip!.pickupLongitude),
          destinationPoint: trip == null
              ? null
              : LatLng(trip!.destinationLatitude, trip!.destinationLongitude),
          driverPoint: driverLocation,
          driverApproaching:
              trip?.status == TripStatus.driverAccepted ||
              trip?.status == TripStatus.enRoute,
        ),
      ),
      Positioned(
        top: 0,
        left: 0,
        right: 0,
        child: SafeArea(
          child: Padding(
            padding: const EdgeInsets.all(18),
            child: Row(
              children: [
                const Brand(size: 25),
                const Spacer(),
                CircleAvatar(
                  backgroundColor: Colors.white,
                  child: Icon(Icons.person, color: green),
                ),
              ],
            ),
          ),
        ),
      ),
      Align(alignment: Alignment.bottomCenter, child: _panel()),
    ],
  );

  Widget _panel() {
    final t = trip, p = t?.currentProposal;
    final status = t?.status;
    final invite = status == TripStatus.awaitingDriverResponse;
    final accepted = status == TripStatus.driverAccepted;
    final enRoute = status == TripStatus.enRoute;
    final picked = status == TripStatus.pickedUp;
    return SafeArea(
      top: false,
      child: Container(
        width: double.infinity,
        constraints: BoxConstraints(
          maxHeight: MediaQuery.sizeOf(context).height * .8,
        ),
        padding: const EdgeInsets.fromLTRB(20, 18, 20, 18),
        decoration: const BoxDecoration(
          color: Colors.white,
          borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
        ),
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Center(
                child: Container(
                  width: 40,
                  height: 4,
                  decoration: BoxDecoration(
                    color: const Color(0xFFD2D8DF),
                    borderRadius: BorderRadius.circular(4),
                  ),
                ),
              ),
              const SizedBox(height: 16),
              if (error != null)
                Text(error!, style: const TextStyle(color: Colors.red)),
              if (t == null ||
                  status == TripStatus.cancelled ||
                  status == TripStatus.failed) ...[
                Text(
                  online ? 'Đang chờ chuyến' : 'Bạn đang ngoại tuyến',
                  style: const TextStyle(
                    fontSize: 21,
                    fontWeight: FontWeight.bold,
                  ),
                ),
                const SizedBox(height: 14),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton(
                    onPressed: busy ? null : _toggleOnline,
                    child: Text(online ? 'Tắt nhận chuyến' : 'Bật nhận chuyến'),
                  ),
                ),
              ] else ...[
                Text(
                  switch (status) {
                    TripStatus.awaitingDriverResponse => 'Lời mời chuyến mới',
                    TripStatus.driverAccepted => 'Đã nhận chuyến',
                    TripStatus.enRoute => 'Đang đến đón khách',
                    TripStatus.pickedUp => 'Đang chở khách',
                    TripStatus.droppedOff ||
                    TripStatus.paymentPending => 'Đã trả khách',
                    _ => 'Đang xử lý chuyến',
                  },
                  style: const TextStyle(
                    fontSize: 21,
                    fontWeight: FontWeight.bold,
                  ),
                ),
                const SizedBox(height: 10),
                if (invite && invitationExpiry != null)
                  Text(
                    'Thời gian phản hồi: ${invitationExpiry!.difference(_now()).inSeconds.clamp(0, 30)} giây',
                    style: const TextStyle(
                      color: green,
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                if (t.riderName != null)
                  Text(
                    'Khách hàng: ${t.riderName}',
                    style: const TextStyle(fontWeight: FontWeight.w600),
                  ),
                if (t.riderPhone != null && (accepted || enRoute || picked))
                  Row(
                    children: [
                      Expanded(child: Text('SĐT: ${t.riderPhone}')),
                      IconButton(
                        onPressed: () =>
                            launchUrl(Uri(scheme: 'tel', path: t.riderPhone)),
                        icon: const Icon(Icons.call, color: green),
                        tooltip: 'Gọi khách',
                      ),
                    ],
                  ),
                const SizedBox(height: 8),
                _address(Icons.trip_origin, 'Điểm đón', t.pickupAddress),
                _address(Icons.location_on, 'Điểm đến', t.destinationAddress),
                const SizedBox(height: 8),
                Text(
                  'Giá chuyến: ${(p?.amount ?? t.agreedAmount ?? 0).toStringAsFixed(0)} đ  •  ${p?.distanceMeters == null ? 'Đang tính tuyến' : '${(p!.distanceMeters! / 1000).toStringAsFixed(1)} km'}',
                ),
                if (route?.pickupEtaMinutes != null &&
                    (invite || accepted || enRoute))
                  Text(
                    'Tới đón khoảng ${route!.pickupEtaMinutes} phút',
                    style: const TextStyle(
                      color: green,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                if (picked && route?.toDestination?.durationSeconds != null)
                  Text(
                    'Tới điểm đến khoảng ${(route!.toDestination!.durationSeconds / 60).ceil()} phút',
                    style: const TextStyle(
                      color: green,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                const SizedBox(height: 14),
                if (invite)
                  Row(
                    children: [
                      Expanded(
                        child: OutlinedButton(
                          onPressed: busy ? null : () => _answer(false),
                          child: const Text('Từ chối'),
                        ),
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: FilledButton(
                          onPressed: busy ? null : () => _answer(true),
                          child: const Text('Nhận chuyến'),
                        ),
                      ),
                    ],
                  ),
                if (accepted)
                  SizedBox(
                    width: double.infinity,
                    child: FilledButton(
                      onPressed: busy ? null : () => _step('en-route'),
                      child: const Text('Bắt đầu đến đón'),
                    ),
                  ),
                if (enRoute)
                  SizedBox(
                    width: double.infinity,
                    child: FilledButton(
                      onPressed: busy ? null : () => _step('pickup'),
                      child: const Text('Đã đón khách'),
                    ),
                  ),
                if (picked)
                  SizedBox(
                    width: double.infinity,
                    child: FilledButton(
                      onPressed: busy ? null : () => _step('dropoff'),
                      child: const Text('Đã trả khách'),
                    ),
                  ),
                if (status == TripStatus.paymentPending)
                  Text(
                    t.paymentStatus == 'Failed'
                        ? 'Thanh toán chưa thành công'
                        : 'Đang xử lý thanh toán theo hệ thống…',
                    style: const TextStyle(color: green),
                  ),
                if ({
                  TripStatus.paymentPending,
                  TripStatus.paid,
                }.contains(status))
                  FilledButton(
                    onPressed: _dismiss,
                    child: const Text('Tiếp tục nhận chuyến'),
                  ),
                if (status == TripStatus.paid)
                  const Text(
                    'Thanh toán thành công',
                    style: TextStyle(color: green),
                  ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  Widget _address(IconData icon, String label, String value) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 5),
    child: Row(
      children: [
        Icon(icon, color: green, size: 19),
        const SizedBox(width: 8),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label, style: const TextStyle(color: muted, fontSize: 11)),
              Text(
                value,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(fontWeight: FontWeight.w600),
              ),
            ],
          ),
        ),
      ],
    ),
  );
  Widget _history() => FutureBuilder<TripHistoryPage>(
    future: historyFuture ??= trips.history(),
    builder: (context, snap) =>
        _listPage('Chuyến đi', snap.data?.items ?? const []),
  );
  Widget _listPage(String title, List<TripSnapshot> items) => Scaffold(
    appBar: AppBar(title: Text(title)),
    body: ListView(
      children: [
        for (final t in items)
          ListTile(
            title: Text(t.destinationAddress),
            subtitle: Text('${t.pickupAddress}\n${t.status.label}'),
            trailing: Text('${(t.agreedAmount ?? 0).toStringAsFixed(0)} đ'),
            onTap: () => showDialog<void>(
              context: context,
              builder: (context) => AlertDialog(
                title: const Text('Chi tiết chuyến đi'),
                content: Text(
                  'Khách: ${t.riderName ?? 'Khách hàng'}\nĐón: ${t.pickupAddress}\nĐến: ${t.destinationAddress}\nTrạng thái: ${t.status.label}\nGiá: ${(t.agreedAmount ?? 0).toStringAsFixed(0)} đ',
                ),
                actions: [
                  TextButton(
                    onPressed: () => Navigator.pop(context),
                    child: const Text('Đóng'),
                  ),
                ],
              ),
            ),
          ),
      ],
    ),
  );
  Widget _income() => FutureBuilder<Object?>(
    future: earningsFuture ??= trips.earnings(),
    builder: (context, snap) {
      final data = snap.data is Map<String, dynamic>
          ? snap.data as Map<String, dynamic>
          : const <String, dynamic>{};
      final amount = (data['totalEarnings'] as num?)?.toDouble() ?? 0;
      final items = data['transactions'] as List? ?? const [];
      return Scaffold(
        appBar: AppBar(title: const Text('Thu nhập')),
        body: ListView(
          padding: const EdgeInsets.all(20),
          children: [
            const Text('Tổng thu nhập'),
            Text(
              '${amount.toStringAsFixed(0)} đ',
              style: const TextStyle(
                fontSize: 28,
                fontWeight: FontWeight.bold,
                color: green,
              ),
            ),
            const SizedBox(height: 16),
            for (final item in items.whereType<Map>())
              ListTile(
                leading: const Icon(Icons.payments, color: green),
                title: Text('${item['amount'] ?? 0} đ'),
                subtitle: Text('${item['paidAt'] ?? ''}'),
              ),
          ],
        ),
      );
    },
  );
}
