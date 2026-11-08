import 'dart:async';
import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';
import 'package:maplibre_gl/maplibre_gl.dart';
import 'package:url_launcher/url_launcher.dart';
import 'app.dart' show Brand, green, ink, muted;
import 'auth.dart';
import 'goong_map.dart';
import 'trip_api.dart';
import 'trip_models.dart';
import 'trip_realtime.dart';
import 'trip_widgets.dart';

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
  TripProgress? progress;
  final _activePanelKey = GlobalKey();
  double _activePanelHeight = 0;
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
    if (event.name == 'TripRouteProgress' && trip != null) {
      final value = TripProgress.parse(event.data, trip!, progress);
      if (value != null) setState(() => progress = value);
    } else if (event.name == 'DriverInvitation') {
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
      bottomNavigationBar: TripNavigation(
        index: tab,
        driver: true,
        onChanged: (v) => setState(() {
          tab = v;
          if (v == 1) historyFuture = null;
          if (v == 2) earningsFuture = null;
        }),
      ),
    );
  }

  Widget _dashboard() {
    if (trip != null && tripFinished(trip!)) {
      return TripCompletionPage(
        trip: trip!,
        onContinue: _dismiss,
        driver: true,
        api: trips,
      );
    }
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final box =
          _activePanelKey.currentContext?.findRenderObject() as RenderBox?;
      if (box != null &&
          box.hasSize &&
          (box.size.height - _activePanelHeight).abs() > 1) {
        setState(() => _activePanelHeight = box.size.height);
      }
    });
    return Stack(
      children: [
        Positioned.fill(
          child: GoongMap(
            bottomInset: _activePanelHeight > 0
                ? _activePanelHeight
                : MediaQuery.sizeOf(context).height * .54,
            routePoints: trip == null
                ? const []
                : route?.forTrip(trip!)?.decode() ?? const [],
            pickupPoint: trip == null || trip!.status == TripStatus.pickedUp
                ? null
                : LatLng(trip!.pickupLatitude, trip!.pickupLongitude),
            destinationPoint:
                trip == null ||
                    trip!.status == TripStatus.driverAccepted ||
                    trip!.status == TripStatus.enRoute
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
              child: Row(children: [const Brand(size: 25)]),
            ),
          ),
        ),
        Align(alignment: Alignment.bottomCenter, child: _panel()),
      ],
    );
  }

  Widget _panel() {
    final t = trip;
    final status = t?.status;
    final invite = status == TripStatus.awaitingDriverResponse;
    final approaching =
        status == TripStatus.driverAccepted || status == TripStatus.enRoute;
    final picked = status == TripStatus.pickedUp;
    if (t == null ||
        status == TripStatus.cancelled ||
        status == TripStatus.failed) {
      return TripSheet(
        title: online ? 'Đang chờ chuyến' : 'Bạn đang ngoại tuyến',
        children: [
          Text(
            online
                ? 'Sẵn sàng đón khách. Giữ ứng dụng mở để nhận lời mời.'
                : 'Bật nhận chuyến khi bạn đã sẵn sàng di chuyển.',
            style: const TextStyle(color: muted, fontSize: 13),
          ),
          const SizedBox(height: 18),
          if (error != null) ...[
            Text(
              error!,
              style: const TextStyle(color: Colors.red, fontSize: 12),
            ),
            const SizedBox(height: 12),
          ],
          TripAction(
            label: online ? 'Tắt nhận chuyến' : 'Bật nhận chuyến',
            onTap: _toggleOnline,
            busy: busy,
            secondary: online,
          ),
        ],
      );
    }
    return TripSheet(
      title: invite
          ? 'Lời mời chuyến mới'
          : approaching
          ? 'Đang đến đón khách'
          : picked
          ? 'Đang chở khách'
          : status!.label,
      trailing: invite && invitationExpiry != null
          ? TripCountdown(
              seconds: invitationExpiry!
                  .difference(_now())
                  .inSeconds
                  .clamp(0, 30),
              total: 30,
              size: 54,
            )
          : null,
      children: [
        TripPersonCard(
          name: t.riderName ?? 'Khách hàng',
          subtitle: approaching || picked
              ? t.riderPhone
              : 'Khách hàng SwiftRide',
          onCall: t.riderPhone != null && (approaching || picked)
              ? () => launchUrl(Uri(scheme: 'tel', path: t.riderPhone))
              : null,
        ),
        const SizedBox(height: 12),
        if (approaching) ...[
          TripEta(
            text:
                (progress?.tripId == t.id && progress?.stageVersion == 1
                    ? progress?.label
                    : null) ??
                (route?.pickupEtaMinutes != null
                    ? 'Tới đón khoảng ${route!.pickupEtaMinutes} phút'
                    : 'Đang di chuyển tới điểm đón'),
          ),
          const SizedBox(height: 12),
        ],
        if (picked) ...[
          TripEta(
            text:
                (progress?.tripId == t.id && progress?.stageVersion == 2
                    ? progress?.label
                    : null) ??
                (route?.toDestination != null
                    ? 'Tới điểm đến khoảng ${(route!.toDestination!.durationSeconds / 60).ceil()} phút'
                    : 'Đang trên đường tới điểm đến'),
          ),
          const SizedBox(height: 12),
        ],
        if (invite)
          ExpansionTile(
            dense: true,
            visualDensity: VisualDensity.compact,
            tilePadding: EdgeInsets.zero,
            leading: const Icon(Icons.route_rounded, color: green, size: 20),
            title: Text(
              '${t.pickupAddress.split(',').first} → ${t.destinationAddress.split(',').first}',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
            ),
            children: [TripJourneyCard(trip: t)],
          )
        else
          ExpansionTile(
            tilePadding: EdgeInsets.zero,
            title: Text(
              picked ? t.destinationAddress : t.pickupAddress,
              style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600),
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
            ),
            subtitle: Text(
              picked ? 'Điểm đến' : 'Điểm đón',
              style: const TextStyle(fontSize: 11, color: muted),
            ),
            leading: const Icon(Icons.location_on_rounded, color: green),
            children: [TripJourneyCard(trip: t)],
          ),
        const SizedBox(height: 12),
        TripMetrics(trip: t),
        const SizedBox(height: 8),
        TripPaymentMethodLabel(trip: t),
        if (error != null) ...[
          const SizedBox(height: 10),
          Text(error!, style: const TextStyle(color: Colors.red, fontSize: 12)),
        ],
        const SizedBox(height: 16),
        if (invite)
          Row(
            children: [
              Expanded(
                child: TripAction(
                  label: 'Từ chối',
                  onTap: () => _answer(false),
                  busy: busy,
                  secondary: true,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: TripAction(
                  label: 'Nhận chuyến',
                  onTap: () => _answer(true),
                  busy: busy,
                ),
              ),
            ],
          ),
        if (approaching)
          TripAction(
            label: 'Đã đón khách',
            onTap: () => _step('pickup'),
            busy: busy,
          ),
        if (picked)
          TripAction(
            label: t.isCash ? 'Đã trả khách và thu tiền' : 'Đã trả khách',
            onTap: () => _step('dropoff'),
            busy: busy,
          ),
      ],
    );
  }

  Widget _history() => FutureBuilder<TripHistoryPage>(
    future: historyFuture ??= trips.history(),
    builder: (c, s) => Scaffold(
      backgroundColor: const Color(0xFFF6FAF7),
      appBar: AppBar(
        title: const Text(
          'Chuyến đi',
          style: TextStyle(fontWeight: FontWeight.w800),
        ),
        backgroundColor: Colors.white,
      ),
      body: s.connectionState != ConnectionState.done
          ? const Center(child: CircularProgressIndicator())
          : s.hasError
          ? _retry(
              'Chưa tải được lịch sử',
              () => setState(() => historyFuture = null),
            )
          : s.data!.items.isEmpty
          ? const Center(
              child: Text(
                'Chưa có chuyến đi nào.',
                style: TextStyle(color: muted),
              ),
            )
          : ListView.separated(
              padding: const EdgeInsets.all(16),
              itemCount: s.data!.items.length,
              separatorBuilder: (_, i) => const SizedBox(height: 12),
              itemBuilder: (c, i) {
                final t = s.data!.items[i];
                return InkWell(
                  onTap: () => showModalBottomSheet<void>(
                    context: c,
                    isScrollControlled: true,
                    showDragHandle: true,
                    builder: (c) => SingleChildScrollView(
                      padding: const EdgeInsets.all(20),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(
                            tripFinished(t)
                                ? 'Chuyến đi hoàn tất'
                                : t.status.label,
                            style: const TextStyle(
                              color: ink,
                              fontSize: 23,
                              fontWeight: FontWeight.w800,
                            ),
                          ),
                          const SizedBox(height: 16),
                          TripPersonCard(name: t.riderName ?? 'Khách hàng'),
                          const SizedBox(height: 12),
                          TripJourneyCard(trip: t),
                          const SizedBox(height: 12),
                          TripMetrics(trip: t),
                          const SizedBox(height: 8),
                          TripPaymentMethodLabel(trip: t),
                          if (tripFinished(t)) ...[
                            const SizedBox(height: 12),
                            TripPaymentBadge(
                              status: t.status == TripStatus.paid
                                  ? 'Succeeded'
                                  : t.paymentStatus,
                            ),
                          ],
                        ],
                      ),
                    ),
                  ),
                  child: TripCard(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          '${t.createdAt.day}/${t.createdAt.month}/${t.createdAt.year} · ${t.createdAt.hour.toString().padLeft(2, '0')}:${t.createdAt.minute.toString().padLeft(2, '0')} · ${t.paymentMethodLabel}',
                          style: const TextStyle(color: muted, fontSize: 11),
                        ),
                        const SizedBox(height: 10),
                        Text(
                          t.pickupAddress,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                        const Padding(
                          padding: EdgeInsets.only(left: 3),
                          child: Icon(
                            Icons.south_rounded,
                            size: 17,
                            color: green,
                          ),
                        ),
                        Text(
                          t.destinationAddress,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                        const SizedBox(height: 12),
                        Row(
                          children: [
                            Expanded(
                              child: Text(
                                moneyVnd(
                                  t.agreedAmount ?? t.currentProposal?.amount,
                                ),
                                style: const TextStyle(
                                  color: ink,
                                  fontSize: 19,
                                  fontWeight: FontWeight.w800,
                                ),
                              ),
                            ),
                            if (tripFinished(t))
                              Flexible(
                                child: TripPaymentBadge(
                                  status: t.status == TripStatus.paid
                                      ? 'Succeeded'
                                      : t.paymentStatus,
                                ),
                              )
                            else
                              Text(
                                t.status.label,
                                style: const TextStyle(
                                  fontSize: 11,
                                  color: muted,
                                ),
                              ),
                          ],
                        ),
                      ],
                    ),
                  ),
                );
              },
            ),
    ),
  );
  Widget _retry(String message, VoidCallback action) => Center(
    child: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(message, style: const TextStyle(color: muted)),
        TextButton(onPressed: action, child: const Text('Thử lại')),
      ],
    ),
  );
  Widget _income() => FutureBuilder<Object?>(
    future: earningsFuture ??= trips.earnings(),
    builder: (c, s) {
      final data = s.data is Map ? s.data as Map : const {};
      final items = data['transactions'] as List? ?? const [];
      return Scaffold(
        backgroundColor: const Color(0xFFF6FAF7),
        appBar: AppBar(
          title: const Text(
            'Thu nhập',
            style: TextStyle(fontWeight: FontWeight.w800),
          ),
          backgroundColor: Colors.white,
        ),
        body: s.hasError
            ? _retry(
                'Chưa tải được thu nhập',
                () => setState(() => earningsFuture = null),
              )
            : s.connectionState != ConnectionState.done
            ? const Center(child: CircularProgressIndicator())
            : ListView(
                padding: const EdgeInsets.all(18),
                children: [
                  TripCard(
                    color: tripMint,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        const Text(
                          'Tổng thu nhập',
                          style: TextStyle(
                            color: muted,
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                        const SizedBox(height: 8),
                        Text(
                          moneyVnd(data['totalEarnings'] as num? ?? 0),
                          style: const TextStyle(
                            color: ink,
                            fontSize: 34,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                        const SizedBox(height: 8),
                        Text(
                          '${data['tripCount'] ?? items.length} chuyến đã thanh toán · Đã trừ phí nền tảng',
                          style: const TextStyle(color: muted, fontSize: 11),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 12),
                  TripCard(
                    child: Column(
                      children: [
                        _earningLine(
                          'Tiền mặt đã thu',
                          data['cashCollected'] as num? ?? 0,
                        ),
                        _earningLine(
                          'Thu nhập tiền mặt (sau phí)',
                          data['cashNetEarnings'] as num? ?? 0,
                        ),
                        _earningLine(
                          'Thu nhập qua ví/online',
                          data['nonCashEarnings'] as num? ??
                              data['totalEarnings'] as num? ??
                              0,
                        ),
                        const Divider(height: 20),
                        _earningLine(
                          'Phí tiền mặt cần nộp',
                          data['cashCommissionDue'] as num? ?? 0,
                        ),
                        const SizedBox(height: 8),
                        const Text(
                          'Tiền mặt là khoản bạn đã trực tiếp thu từ khách. Tổng thu nhập đã trừ phí nền tảng.',
                          style: TextStyle(color: muted, fontSize: 11),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 22),
                  const Text(
                    'Giao dịch gần đây',
                    style: TextStyle(
                      color: ink,
                      fontWeight: FontWeight.w800,
                      fontSize: 17,
                    ),
                  ),
                  const SizedBox(height: 12),
                  if (items.isEmpty)
                    const Padding(
                      padding: EdgeInsets.symmetric(vertical: 30),
                      child: Text(
                        'Chưa có thu nhập từ chuyến đi.',
                        textAlign: TextAlign.center,
                        style: TextStyle(color: muted),
                      ),
                    ),
                  for (final item in items.whereType<Map>())
                    Padding(
                      padding: const EdgeInsets.only(bottom: 10),
                      child: TripCard(
                        padding: 13,
                        child: Row(
                          children: [
                            Container(
                              padding: const EdgeInsets.all(9),
                              decoration: BoxDecoration(
                                color: tripMint,
                                borderRadius: BorderRadius.circular(12),
                              ),
                              child: const Icon(
                                Icons.two_wheeler_rounded,
                                color: green,
                              ),
                            ),
                            const SizedBox(width: 12),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(
                                    item['paymentMethod'] == 'Cash'
                                        ? 'Thu nhập · Tiền mặt'
                                        : 'Thu nhập · Ví/online',
                                    style: TextStyle(
                                      color: ink,
                                      fontWeight: FontWeight.w700,
                                      fontSize: 13,
                                    ),
                                  ),
                                  const SizedBox(height: 4),
                                  Text(
                                    _date(item['paidAt']),
                                    style: const TextStyle(
                                      color: muted,
                                      fontSize: 11,
                                    ),
                                  ),
                                ],
                              ),
                            ),
                            Text(
                              '${item['paymentMethod'] == 'Cash' ? '' : '+'}${moneyVnd(item['amount'] as num? ?? 0)}',
                              style: const TextStyle(
                                color: green,
                                fontWeight: FontWeight.w800,
                                fontSize: 15,
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                ],
              ),
      );
    },
  );
  Widget _earningLine(String label, num amount) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 5),
    child: Row(
      children: [
        Expanded(
          child: Text(
            label,
            style: const TextStyle(color: muted, fontSize: 12),
          ),
        ),
        Text(
          moneyVnd(amount),
          style: const TextStyle(
            color: ink,
            fontWeight: FontWeight.w700,
            fontSize: 13,
          ),
        ),
      ],
    ),
  );
  String _date(Object? value) {
    final date = DateTime.tryParse('$value')?.toLocal();
    return date == null ? '' : '${date.day}/${date.month}/${date.year}';
  }
}
