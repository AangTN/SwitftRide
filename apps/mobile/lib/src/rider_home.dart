import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:pointer_interceptor/pointer_interceptor.dart';

import 'app.dart' show BrandBadge, green, muted;
import 'goong_map.dart';
import 'haptics.dart';
import 'ride_locations.dart';
import 'places_api.dart';
import 'auth.dart';
import 'trip_api.dart';
import 'trip_models.dart';
import 'trip_realtime.dart';
import 'rider_trip_views.dart';
import 'trip_widgets.dart';
import 'package:maplibre_gl/maplibre_gl.dart';

class RiderHome extends StatefulWidget {
  const RiderHome({
    super.key,
    required this.fullName,
    required this.account,
    this.places,
    this.tripApi,
    this.realtime,
    this.recentDestinations = const [],
  });

  final String fullName;
  final Widget account;
  final PlaceLookup? places;
  final TripApi? tripApi;
  final TripRealtimeClient? realtime;
  final List<RidePlace> recentDestinations;

  @override
  State<RiderHome> createState() => _RiderHomeState();
}

class _RiderHomeState extends State<RiderHome>
    with SingleTickerProviderStateMixin, WidgetsBindingObserver {
  final _map = GlobalKey<GoongMapState>();
  final _activePanelKey = GlobalKey();
  double _activePanelHeight = 0;
  int _tab = 0;
  bool _mapBlocked = false;
  TripSnapshot? _currentTrip;
  TripRouteSnapshot? _tripRoute;
  TripProgress? _progress;
  List<LatLng> _routePoints = const [];
  final List<TripSnapshot> _history = [];
  bool _tripBusy = false;
  bool _historyLoading = false;
  bool _historyLoaded = false;
  bool _refreshingTrip = false;
  String? _tripError;
  String? _historyError;
  PendingTripBooking? _pendingBooking;
  bool _restoring = true;
  bool _restoreInFlight = false;
  bool _restoreFailed = false;
  bool _refreshAgain = false;
  TripRealtimeClient? _realtime;
  StreamSubscription<TripRealtimeEvent>? _realtimeSubscription;
  String? _watchedTripId;
  String? _vibratedProposalId;
  LatLng? _driverPoint;
  DateTime? _driverRecordedAt;
  bool _realtimeDisconnected = false;
  Timer? _tripTicker;
  DateTime _now = DateTime.now();
  late final _panelAnimation = AnimationController(
    vsync: this,
    value: 1,
    duration: const Duration(milliseconds: 220),
  )..addListener(() => setState(() {}));
  RidePlace _pickup = defaultPickup;
  RidePlace? _destination;
  RideVehicleType _vehicleType = RideVehicleType.motorcycle;
  RidePaymentMethod _paymentMethod = RidePaymentMethod.cash;
  bool get _hasRecent => widget.recentDestinations.isNotEmpty;
  List<RidePlace> get _places =>
      _hasRecent ? widget.recentDestinations : suggestedDestinations;
  String get _placesTitle =>
      _hasRecent ? 'Điểm đến gần đây' : 'Điểm đến đề xuất';

  void _settlePanel(double velocity) {
    final expanded = velocity.abs() > 400
        ? velocity < 0
        : _panelAnimation.value >= .5;
    _panelAnimation.animateTo(expanded ? 1 : 0, curve: Curves.easeOutCubic);
  }

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _realtime = widget.realtime ?? widget.tripApi?.createRealtime();
    _realtimeSubscription = _realtime?.events.listen(_onRealtime);
    unawaited(_restoreTrip());
    _tripTicker = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted && _currentTrip != null) {
        setState(() => _now = DateTime.now());
      }
    });
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _tripTicker?.cancel();
    unawaited(_realtimeSubscription?.cancel());
    unawaited(_realtime?.dispose());
    _panelAnimation.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) {
      unawaited(_currentTrip == null ? _restoreTrip() : _refreshTrip());
    }
  }

  Future<void> _watchTrip(String? id) async {
    if (_watchedTripId == id) return;
    _watchedTripId = id;
    await _realtime?.watch(id);
  }

  bool _adoptTrip(TripSnapshot trip) {
    if (!mounted) return false;
    final previous = _currentTrip;
    if (previous?.id == trip.id && previous!.stateVersion > trip.stateVersion) {
      return false;
    }
    if (trip.status == TripStatus.awaitingRiderApproval &&
        trip.currentProposal?.id != null &&
        trip.currentProposal!.id != _vibratedProposalId) {
      _vibratedProposalId = trip.currentProposal!.id;
      unawaited(vibrateProposal());
    }
    setState(() {
      _currentTrip = trip;
      if (_tripRoute?.matches(trip) != true) {
        _tripRoute = null;
        _routePoints = const [];
      }
      if (previous?.id != trip.id ||
          previous?.driverId != trip.driverId ||
          !{
            TripStatus.driverAccepted,
            TripStatus.enRoute,
            TripStatus.pickedUp,
          }.contains(trip.status)) {
        _driverPoint = null;
        _driverRecordedAt = null;
      }
      _tripError = null;
    });
    unawaited(_watchTrip(trip.status.isActive ? trip.id : null));
    return true;
  }

  void _onRealtime(TripRealtimeEvent event) {
    if (!mounted) return;
    if (event.name == 'connected' || event.name == 'disconnected') {
      setState(() => _realtimeDisconnected = event.name == 'disconnected');
      if (event.name == 'connected') unawaited(_refreshTrip());
      return;
    }
    final trip = _currentTrip;
    final data = event.data;
    if (trip == null || data['tripId'] != trip.id) return;
    if (event.name == 'TripRouteProgress') {
      final value = TripProgress.parse(data, trip, _progress);
      if (value != null) setState(() => _progress = value);
    } else if (event.name == 'DriverLocationUpdated') {
      final time = DateTime.tryParse(data['recordedAt']?.toString() ?? '');
      final lat = data['latitude'];
      final lon = data['longitude'];
      if (data['proposalId'] != trip.currentProposal?.id ||
          data['driverId'] != trip.driverId ||
          !{
            TripStatus.driverAccepted,
            TripStatus.enRoute,
            TripStatus.pickedUp,
          }.contains(trip.status) ||
          time == null ||
          (_driverRecordedAt != null && !time.isAfter(_driverRecordedAt!)) ||
          lat is! num ||
          lon is! num ||
          !lat.isFinite ||
          !lon.isFinite ||
          lat < -90 ||
          lat > 90 ||
          lon < -180 ||
          lon > 180) {
        return;
      }
      setState(() {
        _driverPoint = LatLng(lat.toDouble(), lon.toDouble());
        _driverRecordedAt = time;
      });
    } else if (event.name == 'TripRoutePreview' ||
        event.name == 'TripRoutesUpdated') {
      try {
        final route = TripRouteSnapshot.fromJson(data);
        if (!_applyRoute(route, trip)) unawaited(_refreshTrip());
      } on FormatException {
        unawaited(_refreshRoute(trip));
      }
    } else if (event.name == 'TripChanged' || event.name == 'RideCompleted') {
      if (data['stateVersion'] is int &&
          (data['stateVersion'] as int) < trip.stateVersion) {
        return;
      }
      unawaited(_refreshTrip());
    }
  }

  bool _applyRoute(TripRouteSnapshot route, TripSnapshot trip) {
    final current = _currentTrip;
    if (!mounted ||
        current == null ||
        current.id != trip.id ||
        !route.matches(current)) {
      return false;
    }
    final previousTime = _tripRoute?.forTripTimestamp(current);
    final time = route.forTripTimestamp(current);
    if (previousTime != null && (time == null || time.isBefore(previousTime))) {
      return false;
    }
    final points = route.forTrip(current)?.decode() ?? const <LatLng>[];
    setState(() {
      _tripRoute = route;
      _routePoints = current.status == TripStatus.matching ? const [] : points;
    });
    return true;
  }

  Future<void> _restoreTrip() async {
    if (_tripBusy || _restoreInFlight) return;
    _restoreInFlight = true;
    final api = widget.tripApi;
    if (api == null) {
      _restoreInFlight = false;
      if (mounted) setState(() => _restoring = false);
      return;
    }
    if (mounted) {
      setState(() {
        _restoring = true;
        _restoreFailed = false;
      });
    }
    try {
      _pendingBooking = await api.readPending();
      final trip = await api.current();
      if (!mounted) return;
      if (trip != null && _adoptTrip(trip)) await _refreshRoute(trip);
    } catch (_) {
      if (mounted) {
        setState(() {
          _restoreFailed = true;
          _tripError = 'Chưa kiểm tra được chuyến đang có. Vui lòng thử lại.';
        });
      }
    } finally {
      _restoreInFlight = false;
      if (mounted) setState(() => _restoring = false);
    }
  }

  Future<void> _refreshTrip() async {
    final api = widget.tripApi;
    final current = _currentTrip;
    if (api == null || current == null) return;
    if (_refreshingTrip) {
      _refreshAgain = true;
      return;
    }
    _refreshingTrip = true;
    try {
      final trip = await api.get(current.id);
      if (!mounted || _currentTrip?.id != current.id) return;
      if (_adoptTrip(trip) &&
          trip.status.isActive &&
          trip.status != TripStatus.paymentPending &&
          trip.status != TripStatus.droppedOff) {
        await _refreshRoute(trip);
      }
    } on AuthFailure catch (error) {
      if (mounted && _currentTrip?.id == current.id) {
        setState(() => _tripError = error.message);
      }
    } finally {
      _refreshingTrip = false;
      if (_refreshAgain && mounted) {
        _refreshAgain = false;
        unawaited(_refreshTrip());
      }
    }
  }

  Future<void> _refreshRoute(TripSnapshot trip) async {
    final api = widget.tripApi;
    if (api == null || !trip.status.isActive) return;
    // Responses are checked against the current proposal, phase and timestamp.
    try {
      final route = await api.route(trip.id);
      if (route != null) _applyRoute(route, trip);
    } on AuthFailure {
      // Keep the most recent route while recovering from a temporary failure.
    } on FormatException {
      if (mounted && _currentTrip?.id == trip.id) {
        setState(() => _tripError = 'Dữ liệu tuyến đường không hợp lệ.');
      }
    }
  }

  Future<void> _createTrip() async {
    final api = widget.tripApi;
    if (api == null ||
        _tripBusy ||
        _restoring ||
        _restoreFailed ||
        _currentTrip != null) {
      return;
    }
    if (_pendingBooking == null) {
      final destination = _destination;
      if (destination == null ||
          !_pickup.isResolved ||
          !destination.isResolved) {
        return;
      }
      _pendingBooking = PendingTripBooking(
        newIdempotencyKey(),
        RideLocations(
          _pickup,
          destination,
          _vehicleType,
          _paymentMethod,
        ).toTripRequest(),
      );
    }
    setState(() {
      _tripBusy = true;
      _tripError = null;
    });
    try {
      final pending = _pendingBooking!;
      await api.savePending(pending);
      TripSnapshot trip;
      try {
        trip = await api.createTrip(
          request: pending.request,
          idempotencyKey: pending.key,
        );
      } on AuthFailure catch (error) {
        if (error.code != 'active_trip_exists') rethrow;
        final active = error.tripId == null
            ? await api.current()
            : await api.get(error.tripId!);
        if (active == null) rethrow;
        trip = active;
      }
      await api.clearPending();
      if (!mounted) return;
      _pendingBooking = null;
      if (_adoptTrip(trip)) await _refreshRoute(trip);
    } on AuthFailure catch (error) {
      if (mounted) setState(() => _tripError = error.message);
    } catch (_) {
      if (mounted) {
        setState(
          () => _tripError =
              'Chưa xác nhận được yêu cầu đặt xe. Vui lòng thử lại yêu cầu cũ.',
        );
      }
    } finally {
      if (mounted) setState(() => _tripBusy = false);
    }
  }

  Future<void> _runTripAction(Future<TripSnapshot> Function() action) async {
    if (_tripBusy) return;
    final id = _currentTrip?.id;
    setState(() {
      _tripBusy = true;
      _tripError = null;
    });
    try {
      final trip = await action();
      if (!mounted || _currentTrip?.id != id) return;
      if (_adoptTrip(trip)) await _refreshRoute(trip);
    } on AuthFailure catch (error) {
      if (mounted && _currentTrip?.id == id) {
        setState(() => _tripError = error.message);
        await _refreshTrip();
      }
    } finally {
      if (mounted) setState(() => _tripBusy = false);
    }
  }

  Future<void> _approveProposal() async {
    final trip = _currentTrip;
    final proposal = trip?.currentProposal;
    if (widget.tripApi != null && trip != null && proposal != null) {
      await _runTripAction(() => widget.tripApi!.approve(trip, proposal));
    }
  }

  Future<void> _rejectProposal() async {
    final trip = _currentTrip;
    final proposal = trip?.currentProposal;
    if (widget.tripApi != null && trip != null && proposal != null) {
      await _runTripAction(() => widget.tripApi!.reject(trip, proposal));
    }
  }

  Future<void> _cancelTrip() async {
    final trip = _currentTrip;
    if (widget.tripApi != null && trip != null) {
      await _runTripAction(() => widget.tripApi!.cancel(trip));
    }
  }

  Future<void> _loadHistory() async {
    final api = widget.tripApi;
    if (api == null || _historyLoading) return;
    setState(() {
      _historyLoading = true;
      _historyError = null;
    });
    try {
      final page = await api.history();
      if (mounted) {
        setState(() {
          _history
            ..clear()
            ..addAll(page.items);
          _historyLoaded = true;
        });
      }
    } on AuthFailure catch (error) {
      if (mounted) setState(() => _historyError = error.message);
    } finally {
      if (mounted) setState(() => _historyLoading = false);
    }
  }

  void _selectTab(int index) {
    setState(() => _tab = index);
    if (index == 1 && !_historyLoaded) unawaited(_loadHistory());
  }

  void _showMessage(String message) {
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  void _dismissTrip() {
    setState(() {
      _currentTrip = null;
      _tripRoute = null;
      _routePoints = const [];
      _tripError = null;
      _tab = 0;
    });
    unawaited(_watchTrip(null));
    unawaited(_loadHistory());
  }

  void _showTripDetails(TripSnapshot trip) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      backgroundColor: Colors.white,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(26)),
      ),
      builder: (context) => ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 430),
        child: RiderTripDetailsSheet(trip: trip),
      ),
    );
  }

  Future<T?> _withMapPaused<T>(Future<T?> Function() openPanel) async {
    if (_mapBlocked) return null;
    setState(() => _mapBlocked = true);
    try {
      return await openPanel();
    } finally {
      if (mounted) setState(() => _mapBlocked = false);
    }
  }

  Future<void> _selectLocations({RidePlace? destination}) async {
    if (_tripBusy || _restoring || _restoreFailed || _pendingBooking != null) {
      _showMessage(
        'Vui lòng kiểm tra lại yêu cầu đặt xe đang chờ trước khi đổi địa chỉ.',
      );
      return;
    }
    final result = await _withMapPaused(
      () => showModalBottomSheet<RideLocations>(
        context: context,
        isScrollControlled: true,
        showDragHandle: true,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
        ),
        clipBehavior: Clip.antiAlias,
        backgroundColor: Colors.white,
        constraints: const BoxConstraints(maxWidth: 430),
        builder: (context) => PointerInterceptor(
          child: Padding(
            padding: EdgeInsets.only(
              bottom: MediaQuery.viewInsetsOf(context).bottom,
            ),
            child: FractionallySizedBox(
              heightFactor: .94,
              child: RideLocationPicker(
                pickup: _pickup,
                destination: destination ?? _destination,
                places: widget.places,
                recentDestinations: widget.recentDestinations,
                vehicleType: _vehicleType,
                paymentMethod: _paymentMethod,
              ),
            ),
          ),
        ),
      ),
    );
    if (result == null || !mounted) return;
    setState(() {
      _pickup = result.pickup;
      _destination = result.destination;
      _vehicleType = result.vehicleType;
      _paymentMethod = result.paymentMethod;
    });
    _panelAnimation.animateTo(1, curve: Curves.easeOutCubic);
    final point = result.destination.point;
    if (point != null) _map.currentState?.focus(point);
    await _createTrip();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    backgroundColor: Colors.white,
    body: IndexedStack(
      index: _tab,
      children: [
        _currentTrip == null ? _home() : _activeTrip(),
        RiderTripHistory(
          trips: _history,
          loading: _historyLoading,
          error: _historyError,
          onRetry: _loadHistory,
          onSelected: _showTripDetails,
        ),
        SingleChildScrollView(child: widget.account),
      ],
    ),
    bottomNavigationBar: TripNavigation(index: _tab, onChanged: _selectTab),
  );

  Widget _activeTrip() {
    final trip = _currentTrip!;
    if (tripFinished(trip)) {
      return TripCompletionPage(
        trip: trip,
        onContinue: _dismissTrip,
        driver: false,
        api: widget.tripApi,
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
    final mapCenter = LatLng(
      (trip.pickupLatitude + trip.destinationLatitude) / 2,
      (trip.pickupLongitude + trip.destinationLongitude) / 2,
    );
    return Stack(
      children: [
        Positioned.fill(
          child: GoongMap(
            key: _map,
            routePoints: _routePoints,
            bottomInset: _activePanelHeight > 0
                ? _activePanelHeight
                : MediaQuery.sizeOf(context).height * .56,
            driverPoint: _driverPoint,
            driverApproaching:
                trip.status == TripStatus.driverAccepted ||
                trip.status == TripStatus.enRoute,
            initialTarget: mapCenter,
            initialZoom: 12.5,
            pickupPoint: trip.status == TripStatus.pickedUp
                ? null
                : LatLng(trip.pickupLatitude, trip.pickupLongitude),
            destinationPoint:
                trip.status == TripStatus.driverAccepted ||
                    trip.status == TripStatus.enRoute
                ? null
                : LatLng(trip.destinationLatitude, trip.destinationLongitude),
          ),
        ),
        Align(
          alignment: Alignment.bottomCenter,
          child: PointerInterceptor(
            child: RiderTripPanel(
              key: _activePanelKey,
              trip: trip,
              route: _tripRoute,
              progress:
                  _progress?.tripId == trip.id &&
                      _progress?.stageVersion ==
                          (trip.status == TripStatus.pickedUp ? 2 : 1)
                  ? _progress
                  : null,
              busy: _tripBusy,
              error:
                  _tripError ??
                  (_realtimeDisconnected
                      ? 'Mất kết nối cập nhật trực tiếp. Đang kết nối lại…'
                      : null),
              now: _now,
              onApprove: _approveProposal,
              onReject: _rejectProposal,
              onCancel: _cancelTrip,
              onDismiss: _dismissTrip,
            ),
          ),
        ),
      ],
    );
  }

  Widget _bookingAction() {
    if (widget.tripApi == null || _pendingBooking == null) {
      return const SizedBox.shrink();
    }
    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (_pendingBooking != null)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Text(
                'Yêu cầu đang chờ xác nhận: ${_pendingBooking!.request['pickupAddress']} → '
                '${_pendingBooking!.request['destinationAddress']}. Thử lại để kiểm tra trước khi đặt chuyến khác.',
                style: const TextStyle(fontSize: 12, color: muted),
              ),
            ),
          SizedBox(
            height: 46,
            child: FilledButton.icon(
              key: const Key('request-trip'),
              onPressed: _tripBusy || _restoring || _restoreFailed
                  ? null
                  : _createTrip,
              icon: _tripBusy
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: Colors.white,
                      ),
                    )
                  : const Icon(Icons.local_taxi_rounded),
              label: Text(
                _tripBusy
                    ? 'Đang gửi yêu cầu...'
                    : _pendingBooking != null
                    ? 'Thử lại yêu cầu đặt xe'
                    : 'Thử lại yêu cầu đặt xe',
              ),
              style: FilledButton.styleFrom(
                backgroundColor: green,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(13),
                ),
              ),
            ),
          ),
          if (_tripError != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                _tripError!,
                key: const Key('trip-error'),
                style: const TextStyle(color: Color(0xFFB3261E), fontSize: 12),
              ),
            ),
        ],
      ),
    );
  }

  Widget _home() => LayoutBuilder(
    builder: (context, constraints) {
      final expandedHeight = (constraints.maxHeight * .75).clamp(126.0, 380.0);
      const collapsedHeight = 126.0;
      final travel = expandedHeight - collapsedHeight;
      final panelHeight = collapsedHeight + travel * _panelAnimation.value;
      return Stack(
        children: [
          Positioned.fill(
            child: GoongMap(key: _map, interactive: !_mapBlocked),
          ),
          Column(
            children: [
              Expanded(
                child: Stack(
                  children: [
                    const Positioned(
                      top: 0,
                      left: 0,
                      right: 0,
                      child: SafeArea(
                        bottom: false,
                        child: Padding(
                          padding: EdgeInsets.all(18),
                          child: Align(
                            alignment: Alignment.centerLeft,
                            child: BrandBadge(),
                          ),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              PointerInterceptor(
                child: GestureDetector(
                  key: const Key('home-booking-panel'),
                  dragStartBehavior: DragStartBehavior.down,
                  behavior: HitTestBehavior.opaque,
                  onVerticalDragStart: (_) => _panelAnimation.stop(),
                  onVerticalDragUpdate: (details) {
                    if (travel <= 0) return;
                    _panelAnimation.value =
                        (_panelAnimation.value - details.delta.dy / travel)
                            .clamp(0.0, 1.0);
                  },
                  onVerticalDragEnd: (details) =>
                      _settlePanel(details.primaryVelocity ?? 0),
                  onVerticalDragCancel: () => _settlePanel(0),
                  child: Container(
                    height: panelHeight,
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 14),
                    decoration: const BoxDecoration(
                      color: Colors.white,
                      borderRadius: BorderRadius.vertical(
                        top: Radius.circular(24),
                      ),
                    ),
                    child: Column(
                      children: [
                        Semantics(
                          button: true,
                          label: _panelAnimation.value > .5
                              ? 'Ẩn điểm đến đề xuất'
                              : 'Hiện điểm đến đề xuất',
                          child: GestureDetector(
                            behavior: HitTestBehavior.opaque,
                            onTap: () => _panelAnimation.animateTo(
                              _panelAnimation.value > .5 ? 0 : 1,
                              curve: Curves.easeOutCubic,
                            ),
                            child: SizedBox(
                              height: 24,
                              width: double.infinity,
                              child: Center(
                                child: Container(
                                  width: 38,
                                  height: 4,
                                  decoration: BoxDecoration(
                                    color: const Color(0xFFC6CBD3),
                                    borderRadius: BorderRadius.circular(4),
                                  ),
                                ),
                              ),
                            ),
                          ),
                        ),
                        _DestinationPrompt(onTap: _selectLocations),
                        _bookingAction(),
                        if (_panelAnimation.value > 0)
                          Expanded(
                            child: ClipRect(
                              child: ScrollConfiguration(
                                behavior: ScrollConfiguration.of(
                                  context,
                                ).copyWith(scrollbars: false),
                                child: SingleChildScrollView(
                                  physics: const NeverScrollableScrollPhysics(),
                                  child: Column(
                                    children: [
                                      const SizedBox(height: 16),
                                      Row(
                                        children: [
                                          Icon(
                                            _hasRecent
                                                ? Icons.history_rounded
                                                : Icons.explore_outlined,
                                            color: green,
                                            size: 21,
                                          ),
                                          const SizedBox(width: 8),
                                          Text(
                                            _placesTitle,
                                            style: const TextStyle(
                                              fontSize: 14,
                                              fontWeight: FontWeight.w700,
                                            ),
                                          ),
                                        ],
                                      ),
                                      const SizedBox(height: 12),
                                      for (final place in _places)
                                        _homePlace(place),
                                    ],
                                  ),
                                ),
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
                ),
              ),
            ],
          ),
          if (_tripError != null &&
              _currentTrip == null &&
              (_destination == null || _restoreFailed))
            Positioned(
              top: 76,
              left: 16,
              right: 16,
              child: Material(
                color: Colors.white,
                elevation: 3,
                borderRadius: BorderRadius.circular(14),
                child: Padding(
                  padding: const EdgeInsets.all(12),
                  child: Row(
                    children: [
                      Expanded(
                        child: Text(
                          _tripError!,
                          style: const TextStyle(
                            color: Color(0xFFB3261E),
                            fontSize: 12,
                          ),
                        ),
                      ),
                      TextButton(
                        onPressed: () => unawaited(_restoreTrip()),
                        child: const Text('Thử lại'),
                      ),
                    ],
                  ),
                ),
              ),
            ),
        ],
      );
    },
  );

  Widget _homePlace(RidePlace place) => Padding(
    padding: const EdgeInsets.only(bottom: 7),
    child: Material(
      color: const Color(0xFFF1F9F4),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(15),
        side: const BorderSide(color: Color(0xFFDCEDE2)),
      ),
      child: ListTile(
        dense: true,
        visualDensity: const VisualDensity(vertical: -2),
        leading: const CircleAvatar(
          radius: 18,
          backgroundColor: Color(0xFFDDF2E5),
          child: Icon(Icons.place_rounded, color: green, size: 23),
        ),
        title: Text(
          place.name,
          style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
        ),
        subtitle: Text(
          place.address,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(
            fontSize: 11,
            color: Color(0xFF53665C),
            fontWeight: FontWeight.w600,
          ),
        ),
        trailing: const Icon(Icons.chevron_right_rounded, color: green),
        onTap: () => _selectLocations(destination: place),
      ),
    ),
  );
}

class _DestinationPrompt extends StatelessWidget {
  const _DestinationPrompt({required this.onTap});
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Container(
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(22),
      boxShadow: [
        BoxShadow(
          color: green.withValues(alpha: .18),
          blurRadius: 16,
          offset: const Offset(0, 5),
        ),
      ],
    ),
    child: ClipRRect(
      borderRadius: BorderRadius.circular(22),
      child: DecoratedBox(
        decoration: const BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topLeft,
            end: Alignment.bottomRight,
            colors: [Color(0xFF2CDB69), Color(0xFF00B56D), Color(0xFF00ADAA)],
          ),
        ),
        child: CustomPaint(
          painter: _PromptAccentPainter(),
          child: Material(
            color: Colors.transparent,
            child: InkWell(
              onTap: onTap,
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  horizontal: 18,
                  vertical: 16,
                ),
                child: Row(
                  children: [
                    const Icon(
                      Icons.search_rounded,
                      color: Colors.white,
                      size: 29,
                    ),
                    const SizedBox(width: 12),
                    const Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(
                            'Bạn muốn đi đâu?',
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              color: Colors.white,
                              fontSize: 17,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                          SizedBox(height: 4),
                          Text(
                            'Chọn điểm đến, bắt đầu hành trình',
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              color: Color(0xFFE9FFF3),
                              fontSize: 11,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: 6),
                    const Icon(
                      Icons.chevron_right_rounded,
                      color: Colors.white,
                      size: 27,
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

class _PromptAccentPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawOval(
      Rect.fromLTWH(
        size.width * .65,
        size.height * .72,
        size.width * .55,
        size.height * .85,
      ),
      Paint()..color = const Color(0xFF59EB87).withValues(alpha: .65),
    );
    canvas.drawOval(
      Rect.fromLTWH(
        size.width * .78,
        size.height * .82,
        size.width * .42,
        size.height * .8,
      ),
      Paint()..color = Colors.white.withValues(alpha: .12),
    );
  }

  @override
  bool shouldRepaint(_PromptAccentPainter oldDelegate) => false;
}
