import 'dart:async';
import 'dart:convert';
import 'dart:math' show Point;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:maplibre_gl/maplibre_gl.dart';

const downtown = LatLng(10.7801, 106.6999);

/// Only the public map-display key belongs in the mobile build.
const goongMaptilesKey = String.fromEnvironment('GOONG_MAPTILES_KEY');

class GoongMap extends StatefulWidget {
  const GoongMap({
    super.key,
    this.interactive = true,
    this.routePoints = const [],
    this.initialTarget,
    this.initialZoom = 14.5,
    this.pickupPoint,
    this.destinationPoint,
    this.driverPoint,
  });

  final bool interactive;
  final List<LatLng> routePoints;
  final LatLng? initialTarget;
  final double initialZoom;
  final LatLng? pickupPoint;
  final LatLng? destinationPoint;
  final LatLng? driverPoint;

  @override
  State<GoongMap> createState() => GoongMapState();
}

class GoongMapState extends State<GoongMap> {
  MapLibreMapController? _controller;
  final _client = http.Client();
  String? _style;
  String? _error;
  Timer? _loadTimer;
  bool _ready = false;
  int _attempt = 0;
  LatLng _target = downtown;
  bool _focusPending = false;
  bool _hasCurrentLocation = false;
  Circle? _marker;
  Circle? _pulse;
  Line? _tripLine;
  final List<Circle> _tripMarkers = [];
  Timer? _pulseTimer;
  bool _reduceMotion = false;

  @override
  void initState() {
    super.initState();
    _target = widget.initialTarget ?? downtown;
    _load();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _reduceMotion = MediaQuery.disableAnimationsOf(context);
    _restartPulse();
  }

  @override
  void didUpdateWidget(GoongMap oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.interactive != widget.interactive) _restartPulse();
    if (!listEquals(oldWidget.routePoints, widget.routePoints) ||
        oldWidget.pickupPoint != widget.pickupPoint ||
        oldWidget.destinationPoint != widget.destinationPoint ||
        oldWidget.driverPoint != widget.driverPoint) {
      _syncTripAnnotations();
    }
    if (!_samePoint(oldWidget.initialTarget, widget.initialTarget) &&
        widget.initialTarget != null) {
      _target = widget.initialTarget!;
      focus(_target, zoom: widget.initialZoom);
    }
  }

  Future<void> _syncTripAnnotations() async {
    final controller = _controller;
    if (!_ready || controller == null) return;
    try {
      final oldLine = _tripLine;
      _tripLine = null;
      if (oldLine != null) await controller.removeLine(oldLine);
      for (final marker in _tripMarkers) {
        await controller.removeCircle(marker);
      }
      _tripMarkers.clear();
      if (widget.routePoints.length > 1) {
        _tripLine = await controller.addLine(
          LineOptions(
            geometry: widget.routePoints,
            lineColor: '#00B650',
            lineWidth: 5,
            lineOpacity: .92,
          ),
        );
      }
      for (final point in [
        if (widget.pickupPoint != null)
          (widget.pickupPoint!, '#00B650'),
        if (widget.destinationPoint != null)
          (widget.destinationPoint!, '#00B650'),
        if (widget.driverPoint != null) (widget.driverPoint!, '#1687FF'),
      ]) {
        _tripMarkers.add(
          await controller.addCircle(
            CircleOptions(
              geometry: point.$1,
              circleRadius: 8,
              circleColor: point.$2,
              circleStrokeColor: '#ffffff',
              circleStrokeWidth: 3,
            ),
          ),
        );
      }
    } catch (_) {
      // MapLibre can finish a style update after the platform view is disposed.
    }
  }

  void _restartPulse() {
    _pulseTimer?.cancel();
    final controller = _controller;
    final pulse = _pulse;
    if (controller == null ||
        pulse == null ||
        !_ready ||
        !widget.interactive ||
        _reduceMotion) {
      return;
    }
    final attempt = _attempt;
    final started = DateTime.now();
    var updating = false;
    // Animate only the halo; the solid position dot stays fixed and readable.
    _pulseTimer = Timer.periodic(const Duration(milliseconds: 80), (_) async {
      if (!mounted || attempt != _attempt || updating) return;
      updating = true;
      final progress =
          (DateTime.now().difference(started).inMilliseconds % 1600) / 1600;
      try {
        await controller.updateCircle(
          pulse,
          CircleOptions(
            geometry: _target,
            circleRadius: 12 + 19 * Curves.easeOut.transform(progress),
            circleOpacity: .24 * (1 - progress),
          ),
        );
      } catch (_) {
        // A pending frame may finish after the map has been reloaded/disposed.
      } finally {
        updating = false;
      }
    });
  }

  Future<void> _load() async {
    _loadTimer?.cancel();
    _pulseTimer?.cancel();
    _pulse = null;
    final attempt = ++_attempt;
    _controller = null;
    _marker = null;
    setState(() {
      _style = null;
      _error = null;
      _ready = false;
    });
    if (goongMaptilesKey.isEmpty) {
      setState(() => _error = 'Chưa có cấu hình bản đồ Goong.');
      return;
    }
    try {
      final response = await _client
          .get(
            Uri.https('tiles.goong.io', '/assets/goong_map_web.json', {
              'api_key': goongMaptilesKey,
            }),
          )
          .timeout(const Duration(seconds: 15));
      if (!mounted || attempt != _attempt) return;
      if (response.statusCode != 200) throw const FormatException();
      final style = jsonDecode(response.body) as Map<String, dynamic>;
      if (style['version'] != 8 || style['sources'] is! Map) {
        throw const FormatException();
      }
      setState(() => _style = response.body);
      _loadTimer = Timer(const Duration(seconds: 25), () {
        if (mounted && !_ready && attempt == _attempt) {
          setState(() => _error = 'Không thể tải bản đồ. Vui lòng thử lại.');
        }
      });
    } catch (_) {
      // Never surface provider URLs or exceptions containing the key.
      if (mounted && attempt == _attempt) {
        setState(
          () => _error = 'Không thể tải bản đồ. Kiểm tra kết nối và thử lại.',
        );
      }
    }
  }

  Future<void> focus(LatLng target, {double? zoom}) async {
    _target = target;
    final controller = _controller;
    if (!_ready || controller == null) {
      _focusPending = true;
      return;
    }
    try {
      await controller.animateCamera(
        CameraUpdate.newLatLngZoom(target, zoom ?? widget.initialZoom),
      );
    } catch (_) {
      // The map may be disposed during an animation.
    }
  }

  Future<void> showCurrentLocation(LatLng target) async {
    _target = target;
    _hasCurrentLocation = true;
    final controller = _controller;
    if (!_ready || controller == null) {
      _focusPending = true;
      return;
    }
    try {
      if (_marker == null) await _onLoaded(_attempt);
      for (final circle in [_marker, _pulse]) {
        if (circle != null) {
          await controller.updateCircle(
            circle,
            CircleOptions(geometry: target),
          );
        }
      }
      await focus(target);
    } catch (_) {
      // The platform view may have closed during the location request.
    }
  }

  Future<void> _onLoaded(int attempt) async {
    if (!mounted || attempt != _attempt) return;
    _loadTimer?.cancel();
    setState(() {
      _ready = true;
      _error = null;
    });
    final controller = _controller;
    if (controller == null) return;
    if (_focusPending) {
      _focusPending = false;
      try {
        await controller.animateCamera(
          CameraUpdate.newLatLngZoom(_target, widget.initialZoom),
        );
      } catch (_) {
        // A camera animation can outlive the platform view.
      }
    }
    if (!_hasCurrentLocation) {
      await _syncTripAnnotations();
      return;
    }
    try {
      final pulse = await controller.addCircle(
        CircleOptions(
          geometry: _target,
          circleRadius: 17,
          circleColor: '#00B650',
          circleOpacity: .18,
        ),
      );
      if (!mounted || attempt != _attempt) return;
      _pulse = pulse;
      _marker = await controller.addCircle(
        CircleOptions(
          geometry: _target,
          circleRadius: 9,
          circleColor: '#00B650',
          circleStrokeColor: '#ffffff',
          circleStrokeWidth: 4,
        ),
      );
      if (!mounted || attempt != _attempt) return;
      _restartPulse();
      await _syncTripAnnotations();
    } catch (_) {
      // Map remains usable if an annotation cannot be added.
    }
  }

  @override
  void dispose() {
    _attempt++;
    _loadTimer?.cancel();
    _pulseTimer?.cancel();
    _client.close();
    // MapLibreMap owns and disposes its controller.
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final attempt = _attempt;
    return ColoredBox(
      color: const Color(0xFFEAF3EF),
      child: Stack(
        fit: StackFit.expand,
        children: [
          if (_style != null && _error == null)
            MapLibreMap(
              key: ValueKey(attempt),
              styleString: _style!,
              initialCameraPosition: CameraPosition(
                target: _target,
                zoom: widget.initialZoom,
              ),
              onMapCreated: (controller) => _controller = controller,
              onStyleLoadedCallback: () => _onLoaded(attempt),
              compassEnabled: false,
              scrollGesturesEnabled: widget.interactive,
              zoomGesturesEnabled: widget.interactive,
              rotateGesturesEnabled: widget.interactive,
              tiltGesturesEnabled: widget.interactive,
              doubleClickZoomEnabled: widget.interactive,
              attributionButtonMargins: const Point(8, 8),
            ),
          if (!_ready || _error != null)
            Center(
              child: SingleChildScrollView(
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(32, 140, 32, 24),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(
                        Icons.map_outlined,
                        size: 38,
                        color: Colors.green.shade600,
                      ),
                      const SizedBox(height: 12),
                      Text(
                        _error ?? 'Đang tải bản đồ Goong…',
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          color: Color(0xFF53627A),
                          fontSize: 12,
                        ),
                      ),
                      if (_error != null)
                        TextButton.icon(
                          onPressed: _load,
                          icon: const Icon(Icons.refresh, size: 18),
                          label: const Text('Thử lại'),
                        ),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }

  bool _samePoint(LatLng? first, LatLng? second) =>
      first == null
          ? second == null
          : second != null &&
                first.latitude == second.latitude &&
                first.longitude == second.longitude;
}
