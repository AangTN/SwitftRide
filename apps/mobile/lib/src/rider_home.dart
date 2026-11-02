import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:geolocator/geolocator.dart';
import 'package:maplibre_gl/maplibre_gl.dart' show LatLng;
import 'package:pointer_interceptor/pointer_interceptor.dart';

import 'app.dart' show Brand, green, ink, mint, muted;
import 'goong_map.dart';
import 'ride_locations.dart';

class RiderHome extends StatefulWidget {
  const RiderHome({
    super.key,
    required this.fullName,
    required this.account,
    this.recentDestinations = const [],
  });

  final String fullName;
  final Widget account;
  final List<RidePlace> recentDestinations;

  @override
  State<RiderHome> createState() => _RiderHomeState();
}

class _RiderHomeState extends State<RiderHome>
    with SingleTickerProviderStateMixin {
  final _map = GlobalKey<GoongMapState>();
  int _tab = 0;
  bool _mapBlocked = false;
  late final _panelAnimation = AnimationController(
    vsync: this,
    value: 1,
    duration: const Duration(milliseconds: 220),
  )..addListener(() => setState(() {}));
  bool _locating = false;
  RidePlace _pickup = defaultPickup;
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
  void dispose() {
    _panelAnimation.dispose();
    super.dispose();
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
                destination: destination,
                recentDestinations: widget.recentDestinations,
                vehicleType: _vehicleType,
                paymentMethod: _paymentMethod,
                onLocate: _locate,
              ),
            ),
          ),
        ),
      ),
    );
    if (result == null || !mounted) return;
    setState(() {
      _pickup = result.pickup;
      _vehicleType = result.vehicleType;
      _paymentMethod = result.paymentMethod;
    });
    final point = result.destination.point;
    if (point != null) _map.currentState?.focus(point);
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    backgroundColor: Colors.white,
    body: IndexedStack(
      index: _tab,
      children: [
        _home(),
        const SafeArea(
          child: Center(
            child: _EmptyPanel(
              icon: Icons.history_rounded,
              title: 'Chuyến đi',
              message: 'Lịch sử chuyến đi sẽ xuất hiện tại đây.',
            ),
          ),
        ),
        const SafeArea(
          child: Center(
            child: _EmptyPanel(
              icon: Icons.account_balance_wallet_outlined,
              title: 'Ví của tôi',
              message: 'Thông tin ví sẽ được cập nhật sau.',
            ),
          ),
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
              _nav(2, Icons.account_balance_wallet_outlined, 'Ví của tôi'),
              _nav(3, Icons.person_outline_rounded, 'Tài khoản'),
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
        onTap: () => setState(() => _tab = index),
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

  Future<RidePlace?> _locate() async {
    if (_locating) return null;
    setState(() => _locating = true);
    void message(String text) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));
    }

    try {
      var permission = await Geolocator.checkPermission();
      if (permission == LocationPermission.denied) {
        permission = await Geolocator.requestPermission();
      }
      if (permission == LocationPermission.denied ||
          permission == LocationPermission.deniedForever) {
        message(
          'Hãy cho phép truy cập vị trí trong trình duyệt hoặc cài đặt thiết bị.',
        );
        return null;
      }
      final position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          accuracy: LocationAccuracy.high,
          timeLimit: Duration(seconds: 15),
        ),
      );
      if (!mounted) return null;
      final point = LatLng(position.latitude, position.longitude);
      setState(() => _pickup = RidePlace('Vị trí hiện tại', '', point));
      await _map.currentState?.showCurrentLocation(point);
      return _pickup;
    } on TimeoutException {
      message('Chưa lấy được vị trí. Vui lòng thử lại.');
    } on LocationServiceDisabledException {
      message('Vui lòng bật dịch vụ vị trí trên thiết bị.');
    } catch (_) {
      message(
        'Không thể lấy vị trí hiện tại. Kiểm tra quyền truy cập và thử lại.',
      );
    } finally {
      if (mounted) setState(() => _locating = false);
    }
    return null;
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
                    Positioned(
                      right: 16,
                      bottom: 16,
                      child: PointerInterceptor(
                        child: _locating
                            ? const Material(
                                shape: CircleBorder(),
                                color: Colors.white,
                                child: Padding(
                                  padding: EdgeInsets.all(14),
                                  child: SizedBox(
                                    width: 20,
                                    height: 20,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                      color: green,
                                    ),
                                  ),
                                ),
                              )
                            : _roundButton(
                                Icons.my_location_outlined,
                                'Về vị trí hiện tại',
                                _locate,
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

  Widget _roundButton(IconData icon, String tooltip, VoidCallback onTap) =>
      Material(
        color: Colors.white,
        shape: const CircleBorder(),
        elevation: 2,
        shadowColor: ink.withValues(alpha: .12),
        child: IconButton(
          onPressed: onTap,
          tooltip: tooltip,
          icon: Icon(icon, color: ink, size: 23),
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

class _EmptyPanel extends StatelessWidget {
  const _EmptyPanel({
    required this.icon,
    required this.title,
    required this.message,
  });
  final IconData icon;
  final String title;
  final String message;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.all(24),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        CircleAvatar(
          radius: 32,
          backgroundColor: mint,
          child: Icon(icon, color: green, size: 30),
        ),
        const SizedBox(height: 18),
        Text(
          title,
          style: const TextStyle(
            fontSize: 21,
            fontWeight: FontWeight.w700,
            color: ink,
          ),
        ),
        const SizedBox(height: 8),
        Text(
          message,
          textAlign: TextAlign.center,
          style: const TextStyle(color: muted, fontSize: 12, height: 1.6),
        ),
      ],
    ),
  );
}
