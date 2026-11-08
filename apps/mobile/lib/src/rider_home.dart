import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:pointer_interceptor/pointer_interceptor.dart';

import 'app.dart' show Brand, green, ink, muted;
import 'goong_map.dart';
import 'ride_locations.dart';
import 'places_api.dart';
import 'auth.dart';
import 'trip_api.dart';
import 'trip_models.dart';
import 'rider_trip_views.dart';
import 'package:maplibre_gl/maplibre_gl.dart';

class RiderHome extends StatefulWidget {
  const RiderHome({
    super.key,
    required this.fullName,
    required this.account,
    this.places,
    this.tripApi,
    this.recentDestinations = const [],
  });

  final String fullName;
  final Widget account;
  final PlaceLookup? places;
  final TripApi? tripApi;
  final List<RidePlace> recentDestinations;

  @override
  State<RiderHome> createState() => _RiderHomeState();
}

class _RiderHomeState extends State<RiderHome>
    with SingleTickerProviderStateMixin {
  final _map = GlobalKey<GoongMapState>();
  int _tab = 0;
  bool _mapBlocked = false;
  TripSnapshot? _currentTrip;
  TripRouteSnapshot? _tripRoute;
  List<LatLng> _routePoints = const [];
  final List<TripSnapshot> _history = [];
  bool _tripBusy = false;
  bool _historyLoading = false;
  bool _historyLoaded = false;
  bool _refreshingTrip = false;
  bool _refreshingRoute = false;
  String? _tripError;
  String? _historyError;
  String? _pendingIdempotencyKey;
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
  RidePaymentMethod _paymentMethod = RidePaymentMethod.swiftRideWallet;
  int _tickerSeconds = 0;
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
    unawaited(_restoreTrip());
    _tripTicker = Timer.periodic(const Duration(seconds: 1), (_) {
      if (!mounted || _currentTrip == null) return;
      _now = DateTime.now();
      _tickerSeconds++;
      if (_tickerSeconds % 5 == 0) unawaited(_refreshTrip());
      setState(() {});
    });
  }

  @override
  void dispose() {
    _tripTicker?.cancel();
    _panelAnimation.dispose();
    super.dispose();
  }

  Future<void> _restoreTrip() async {
    final api = widget.tripApi;
    if (api == null) return;
    try {
      final trip = await api.current();
      if (!mounted) return;
      setState(() {
        _currentTrip = trip;
        _tripError = null;
      });
      if (trip != null) await _refreshRoute(trip);
    } on AuthFailure catch (error) {
      if (mounted) setState(() => _tripError = error.message);
    }
  }

  Future<void> _refreshTrip() async {
    final api = widget.tripApi;
    final current = _currentTrip;
    if (api == null || current == null || _refreshingTrip) return;
    _refreshingTrip = true;
    TripSnapshot trip;
    try {
      trip = await api.get(current.id);
      if (!mounted || _currentTrip?.id != current.id) return;
      setState(() {
        _currentTrip = trip;
        _tripError = null;
      });
    } on AuthFailure catch (error) {
      if (mounted) setState(() => _tripError = error.message);
      return;
    } finally {
      _refreshingTrip = false;
    }
    if (!mounted || _currentTrip?.id != current.id) return;
    if (trip.status.isActive &&
        trip.status != TripStatus.paymentPending &&
        trip.status != TripStatus.droppedOff) {
      await _refreshRoute(trip);
    } else {
      setState(() {
        _tripRoute = null;
        _routePoints = const [];
      });
    }
  }

  Future<void> _refreshRoute(TripSnapshot trip) async {
    final api = widget.tripApi;
    if (api == null || !trip.status.isActive || _refreshingRoute) return;
    _refreshingRoute = true;
    try {
      final route = await api.route(trip.id);
      final points = route?.forTrip(trip)?.decode() ?? const <LatLng>[];
      if (!mounted || _currentTrip?.id != trip.id) return;
      setState(() {
        _tripRoute = route;
        _routePoints = points;
        _tripError = null;
      });
    } on AuthFailure catch (error) {
      if (mounted) setState(() => _tripError = error.message);
    } on FormatException {
      if (mounted) {
        setState(() {
          _tripRoute = null;
          _routePoints = const [];
          _tripError = 'Dữ liệu tuyến đường không hợp lệ.';
        });
      }
    } finally {
      _refreshingRoute = false;
    }
  }

  Future<void> _createTrip() async {
    final api = widget.tripApi;
    final destination = _destination;
    if (api == null || destination == null) return;
    if (!_pickup.isResolved || !destination.isResolved) {
      _showMessage('Vui lòng chọn điểm đón và điểm đến từ kết quả tìm kiếm.');
      return;
    }
    setState(() {
      _tripBusy = true;
      _tripError = null;
      _pendingIdempotencyKey ??= newIdempotencyKey();
    });
    try {
      final request = RideLocations(
        _pickup,
        destination,
        _vehicleType,
        _paymentMethod,
      ).toTripRequest();
      final trip = await api.createTrip(
        request: request,
        idempotencyKey: _pendingIdempotencyKey!,
      );
      if (!mounted) return;
      setState(() {
        _currentTrip = trip;
        _pendingIdempotencyKey = null;
      });
      await _refreshRoute(trip);
    } on AuthFailure catch (error) {
      if (mounted) setState(() => _tripError = error.message);
    } finally {
      if (mounted) setState(() => _tripBusy = false);
    }
  }

  Future<void> _runTripAction(
    Future<TripSnapshot> Function() action,
  ) async {
    if (_tripBusy) return;
    setState(() {
      _tripBusy = true;
      _tripError = null;
    });
    try {
      final trip = await action();
      if (!mounted) return;
      setState(() => _currentTrip = trip);
      await _refreshRoute(trip);
    } on AuthFailure catch (error) {
      if (mounted) {
        setState(() => _tripError = error.message);
        if (error.code == 'stale_trip_version') await _refreshTrip();
      }
    } finally {
      if (mounted) setState(() => _tripBusy = false);
    }
  }

  Future<void> _approveProposal() async {
    final api = widget.tripApi;
    final trip = _currentTrip;
    final proposal = trip?.currentProposal;
    if (api == null || trip == null || proposal == null) return;
    await _runTripAction(() => api.approve(trip, proposal));
  }

  Future<void> _rejectProposal() async {
    final api = widget.tripApi;
    final trip = _currentTrip;
    final proposal = trip?.currentProposal;
    if (api == null || trip == null || proposal == null) return;
    await _runTripAction(() => api.reject(trip, proposal));
  }

  Future<void> _cancelTrip() async {
    final api = widget.tripApi;
    final trip = _currentTrip;
    if (api == null || trip == null) return;
    await _runTripAction(() => api.cancel(trip));
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
    final paid = _currentTrip?.status == TripStatus.paid;
    setState(() {
      _currentTrip = null;
      _tripRoute = null;
      _routePoints = const [];
      _tripError = null;
      _tab = paid ? 1 : 0;
    });
    if (paid) unawaited(_loadHistory());
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
    bottomNavigationBar: Container(
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: const BorderRadius.vertical(top: Radius.circular(26)),
        boxShadow: [
          BoxShadow(
            color: ink.withValues(alpha: .08),
            blurRadius: 24,
            offset: const Offset(0, -4),
          ),
        ],
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 10, horizontal: 6),
          child: Row(
            children: [
              _nav(0, Icons.home_rounded, 'Trang chủ'),
              _nav(1, Icons.history_rounded, 'Chuyến đi'),
              _nav(2, Icons.person_outline_rounded, 'Tài khoản'),
            ],
          ),
        ),
      ),
    ),
  );

  Widget _nav(int index, IconData icon, String label) => Expanded(
    child: Semantics(
      selected: _tab == index,
      child: InkWell(
        borderRadius: BorderRadius.circular(16),
        onTap: () => _selectTab(index),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 5),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, size: 27, color: _tab == index ? green : muted),
              const SizedBox(height: 5),
              Text(
                label,
                style: TextStyle(
                  fontSize: 10,
                  fontWeight: _tab == index ? FontWeight.w700 : FontWeight.w600,
                  color: _tab == index ? green : muted,
                ),
              ),
            ],
          ),
        ),
      ),
    ),
  );

  Widget _activeTrip() {
    final trip = _currentTrip!;
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
            initialTarget: mapCenter,
            initialZoom: 12.5,
            pickupPoint: LatLng(trip.pickupLatitude, trip.pickupLongitude),
            destinationPoint: LatLng(
              trip.destinationLatitude,
              trip.destinationLongitude,
            ),
          ),
        ),
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
                child: Brand(size: 27),
              ),
            ),
          ),
        ),
        Align(
          alignment: Alignment.bottomCenter,
          child: RiderTripPanel(
            trip: trip,
            route: _tripRoute,
            busy: _tripBusy,
            error: _tripError,
            now: _now,
            onApprove: _approveProposal,
            onReject: _rejectProposal,
            onCancel: _cancelTrip,
            onDismiss: trip.status == TripStatus.paymentPending
                ? _refreshTrip
                : _dismissTrip,
          ),
        ),
      ],
    );
  }

  Widget _bookingAction() {
    if (_destination == null || widget.tripApi == null) {
      return const SizedBox.shrink();
    }
    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(
            height: 46,
            child: FilledButton.icon(
              key: const Key('request-trip'),
              onPressed: _tripBusy ? null : _createTrip,
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
              label: Text(_tripBusy ? 'Đang gửi yêu cầu...' : 'Đặt chuyến'),
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
                style: const TextStyle(
                  color: Color(0xFFB3261E),
                  fontSize: 12,
                ),
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
                            child: Brand(size: 27),
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
              _destination == null)
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
