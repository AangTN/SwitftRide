import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;
import 'dart:math' show Point;
import 'dart:ui' as ui;

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
    this.driverApproaching = false,
    this.bottomInset = 0,
  });

  final bool interactive;
  final List<LatLng> routePoints;
  final LatLng? initialTarget;
  final double initialZoom;
  final LatLng? pickupPoint;
  final LatLng? destinationPoint;
  final LatLng? driverPoint;
  final bool driverApproaching;
  final double bottomInset;

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
  List<LatLng> _fittedRoute = const [];
  int _attempt = 0;
  LatLng _target = downtown;
  bool _focusPending = false;
  bool _hasCurrentLocation = false;
  Circle? _marker;
  Circle? _pulse;
  Line? _tripLine;
  Symbol? _driverVehicle;
  bool _vehicleIconReady = false;
  bool _syncingTrip = false;
  bool _tripSyncPending = false;
  final List<Circle> _tripMarkers = [];
  final List<Symbol> _placeSymbols = [];
  bool _placeIconsReady = false;
  Timer? _pulseTimer;
  bool _reduceMotion = false;
  LatLng? _headingPoint;

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
    if (widget.driverPoint != null &&
        widget.driverPoint != oldWidget.driverPoint &&
        widget.routePoints.length > 1) {
      _followDriver(widget.driverPoint!);
    }
    if (oldWidget.interactive != widget.interactive) _restartPulse();
    if (oldWidget.bottomInset != widget.bottomInset) _fittedRoute = const [];
    if (oldWidget.bottomInset != widget.bottomInset ||
        !listEquals(oldWidget.routePoints, widget.routePoints) ||
        oldWidget.pickupPoint != widget.pickupPoint ||
        oldWidget.destinationPoint != widget.destinationPoint ||
        oldWidget.driverPoint != widget.driverPoint ||
        oldWidget.driverApproaching != widget.driverApproaching) {
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
    if (_syncingTrip) {
      _tripSyncPending = true;
      return;
    }
    _syncingTrip = true;
    try {
      final oldLine = _tripLine;
      _tripLine = null;
      if (oldLine != null) await controller.removeLine(oldLine);
      for (final marker in _tripMarkers) {
        await controller.removeCircle(marker);
      }
      _tripMarkers.clear();
      for (final symbol in _placeSymbols) {
        await controller.removeSymbol(symbol);
      }
      _placeSymbols.clear();
      if (_driverVehicle != null) {
        await controller.removeSymbol(_driverVehicle!);
        _driverVehicle = null;
      }
      if (widget.routePoints.length > 1) {
        if (!listEquals(_fittedRoute, widget.routePoints)) {
          var south = widget.routePoints.first.latitude, north = south;
          var west = widget.routePoints.first.longitude, east = west;
          for (final point in widget.routePoints) {
            if (point.latitude < south) south = point.latitude;
            if (point.latitude > north) north = point.latitude;
            if (point.longitude < west) west = point.longitude;
            if (point.longitude > east) east = point.longitude;
          }
          if (!mounted) return;
          await controller.animateCamera(
            CameraUpdate.newLatLngBounds(
              LatLngBounds(
                southwest: LatLng(south - .0003, west - .0003),
                northeast: LatLng(north + .0003, east + .0003),
              ),
              left: 32,
              top: 90,
              right: 32,
              bottom: (widget.bottomInset + 24).clamp(
                0,
                ((context.findRenderObject() as RenderBox).size.height - 150)
                    .clamp(0, double.infinity),
              ),
            ),
          );
          _fittedRoute = List.of(widget.routePoints);
        }
        _tripLine = await controller.addLine(
          LineOptions(
            geometry: _remainingRoute(widget.routePoints, widget.driverPoint),
            lineColor: '#00B650',
            lineWidth: 5,
            lineOpacity: .92,
          ),
        );
      }
      if (!_placeIconsReady) {
        for (final entry in [
          ('pickup-icon', Icons.my_location_rounded, const Color(0xff00b650)),
          (
            'destination-icon',
            Icons.location_on_rounded,
            const Color(0xff1687ff),
          ),
        ]) {
          final recorder = ui.PictureRecorder();
          final canvas = Canvas(recorder);
          canvas.drawCircle(
            const Offset(32, 32),
            31,
            Paint()..color = Colors.white,
          );
          final painter = TextPainter(
            text: TextSpan(
              text: String.fromCharCode(entry.$2.codePoint),
              style: TextStyle(
                fontFamily: entry.$2.fontFamily,
                fontSize: 44,
                color: entry.$3,
              ),
            ),
            textDirection: TextDirection.ltr,
          )..layout();
          painter.paint(
            canvas,
            Offset((64 - painter.width) / 2, (64 - painter.height) / 2),
          );
          final picture = recorder.endRecording();
          final image = await picture.toImage(64, 64);
          final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
          await controller.addImage(entry.$1, bytes!.buffer.asUint8List());
          painter.dispose();
          image.dispose();
          picture.dispose();
        }
        _placeIconsReady = true;
      }
      for (final place in [
        if (widget.pickupPoint != null) (widget.pickupPoint!, 'pickup-icon'),
        if (widget.destinationPoint != null)
          (widget.destinationPoint!, 'destination-icon'),
      ]) {
        _placeSymbols.add(
          await controller.addSymbol(
            SymbolOptions(
              geometry: place.$1,
              iconImage: place.$2,
              iconSize: .6,
            ),
          ),
        );
      }
      if (widget.driverPoint != null) {
        if (!_vehicleIconReady) {
          final recorder = ui.PictureRecorder();
          final canvas = Canvas(recorder);
          canvas.drawCircle(
            const Offset(32, 32),
            30,
            Paint()..color = Colors.white,
          );
          final painter = TextPainter(
            text: TextSpan(
              text: String.fromCharCode(Icons.two_wheeler_rounded.codePoint),
              style: TextStyle(
                fontFamily: Icons.two_wheeler_rounded.fontFamily,
                fontSize: 46,
                color: const Color(0xFF1687FF),
              ),
            ),
            textDirection: TextDirection.ltr,
          )..layout();
          painter.paint(
            canvas,
            Offset((64 - painter.width) / 2, (64 - painter.height) / 2),
          );
          final picture = recorder.endRecording();
          final image = await picture.toImage(64, 64);
          final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
          await controller.addImage(
            'swiftride-driver',
            bytes!.buffer.asUint8List(),
          );
          painter.dispose();
          image.dispose();
          picture.dispose();
          _vehicleIconReady = true;
        }
        _driverVehicle = await controller.addSymbol(
          SymbolOptions(
            geometry: widget.driverPoint,
            iconImage: 'swiftride-driver',
            iconSize: .6,
          ),
        );
      }
    } catch (_) {
      // MapLibre can finish a style update after the platform view is disposed.
    } finally {
      _syncingTrip = false;
      if (_tripSyncPending && mounted) {
        _tripSyncPending = false;
        unawaited(_syncTripAnnotations());
      }
    }
  }

  List<LatLng> _remainingRoute(List<LatLng> points, LatLng? position) {
    if (position == null || points.length < 2) return points;
    final scale = math.cos(position.latitude * math.pi / 180);
    var best = double.infinity, segment = 0, fraction = 0.0;
    for (var i = 0; i < points.length - 1; i++) {
      final a = points[i], b = points[i + 1];
      final ax = (a.longitude - position.longitude) * scale * 111195;
      final ay = (a.latitude - position.latitude) * 111195;
      final dx = (b.longitude - a.longitude) * scale * 111195;
      final dy = (b.latitude - a.latitude) * 111195;
      final length = dx * dx + dy * dy;
      final t = length == 0
          ? 0.0
          : (-(ax * dx + ay * dy) / length).clamp(0.0, 1.0);
      final distance = math.pow(ax + dx * t, 2) + math.pow(ay + dy * t, 2);
      if (distance < best) {
        best = distance.toDouble();
        segment = i;
        fraction = t;
      }
    }
    if (best > 3600) return points;
    final a = points[segment], b = points[segment + 1];
    return [
      LatLng(
        a.latitude + (b.latitude - a.latitude) * fraction,
        a.longitude + (b.longitude - a.longitude) * fraction,
      ),
      ...points.skip(segment + 1),
    ];
  }

  void _followDriver(LatLng point) {
    final previous = _headingPoint;
    if (previous == null) {
      _headingPoint = point;
      return;
    }
    final dy = (point.latitude - previous.latitude) * 111195;
    final dx =
        (point.longitude - previous.longitude) *
        111195 *
        math.cos(point.latitude * math.pi / 180);
    if (dx * dx + dy * dy < 100) return;
    _headingPoint = point;
    final controller = _controller;
    if (!_ready || controller == null) return;
    final bearing = (math.atan2(dx, dy) * 180 / math.pi + 360) % 360;
    unawaited(
      controller
          .animateCamera(
            CameraUpdate.newCameraPosition(
              CameraPosition(target: point, zoom: 16, bearing: bearing),
            ),
          )
          .catchError((_) => false),
    );
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
      _fittedRoute = const [];
      _vehicleIconReady = false;
      _placeIconsReady = false;
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

  bool _samePoint(LatLng? first, LatLng? second) => first == null
      ? second == null
      : second != null &&
            first.latitude == second.latitude &&
            first.longitude == second.longitude;
}
