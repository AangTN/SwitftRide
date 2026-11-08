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
  const DriverHome({super.key, required this.api, required this.fullName, required this.account});
  final AuthApi api;
  final String fullName;
  final Widget account;
  @override
  State<DriverHome> createState() => _DriverHomeState();
}

class _DriverHomeState extends State<DriverHome> {
  late final TripApi trips = TripApi(widget.api);
  late final TripRealtime realtime = TripRealtime(widget.api);
  StreamSubscription<TripRealtimeEvent>? events;
  StreamSubscription<Position>? gps;
  TripSnapshot? trip;
  TripRoute? route;
  LatLng? driverLocation;
  late final Future<Object?> earningsFuture = trips.earnings();
  bool online = false, busy = false, loading = true;
  String? error;
  DateTime? invitationExpiry;
  Timer? clock;
  int tab = 0;

  @override
  void initState() {
    super.initState();
    events = realtime.events.listen(_onEvent);
    clock = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
      if (invitationExpiry != null && !invitationExpiry!.isAfter(DateTime.now())) {
        invitationExpiry = null;
        trip = null; route = null;
        if (mounted) setState(() {});
      }
    });
    _restore();
  }

  Future<void> _restore() async {
    try {
      await realtime.connect();
      final current = await trips.current();
      if (current != null) {
        trip = current;
        await realtime.subscribeTrip(current.id);
        await _loadRoute();
        await _startGpsStream(requestPermission: false);
      }
      if (mounted) setState(() => loading = false);
    } catch (e) { if (mounted) setState(() { error = '$e'; loading = false; }); }
  }

  void _onEvent(TripRealtimeEvent event) {
    if (event.name == 'DriverInvitation') {
      final id = event.data['tripId']?.toString();
      final expiry = DateTime.tryParse('${event.data['expiresAt']}')?.toLocal();
      if (id != null) {
        invitationExpiry = expiry;
        _loadTrip(id);
      }
    } else if (event.name == 'TripChanged' || event.name == 'RideCompleted' || event.name == 'TripRoutesUpdated') {
      if (event.data['tripId']?.toString() == trip?.id) _refreshTrip();
    } else if (event.name == 'Reconnected') {
      _restore();
    } else if (event.name == 'Disconnected') {
      Future<void>.delayed(const Duration(seconds: 2), () async {
        try { await realtime.connect(); await _restore(); } catch (_) {}
      });
    }
  }

  Future<void> _loadTrip(String id) async {
    try {
      final value = await trips.get(id);
      if (!mounted) return;
      setState(() => trip = value);
      await _loadRoute();
    } catch (e) { if (mounted) setState(() => error = '$e'); }
  }
  Future<void> _refreshTrip() async { if (trip != null) await _loadTrip(trip!.id); }
  Future<void> _loadRoute() async {
    final current = trip; if (current == null) return;
    try { final value = await trips.getRoute(current.id); if (mounted) setState(() => route = value); } catch (_) {}
  }

  Future<Position> _position() async {
    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) permission = await Geolocator.requestPermission();
    if (permission == LocationPermission.denied || permission == LocationPermission.deniedForever) {
      throw Exception('Cần cấp quyền vị trí để bật nhận chuyến.');
    }
    if (!await Geolocator.isLocationServiceEnabled()) throw Exception('Hãy bật GPS trên thiết bị.');
    return Geolocator.getCurrentPosition();
  }

  Future<void> _startGpsStream({required bool requestPermission}) async {
    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied && requestPermission) permission = await Geolocator.requestPermission();
    if (permission == LocationPermission.denied || permission == LocationPermission.deniedForever) return;
    await gps?.cancel();
    gps = Geolocator.getPositionStream(locationSettings: const LocationSettings(accuracy: LocationAccuracy.high, distanceFilter: 15)).listen((position) {
      driverLocation = LatLng(position.latitude, position.longitude);
      if (mounted) setState(() {});
      trips.updateGps(position.latitude, position.longitude).catchError((_) {});
    });
  }

  Future<void> _toggleOnline() async {
    if (busy) return;
    setState(() { busy = true; error = null; });
    try {
      if (!online) {
        final p = await _position();
        await realtime.connect();
        await trips.availability(true, latitude: p.latitude, longitude: p.longitude);
        await trips.updateGps(p.latitude, p.longitude);
        driverLocation = LatLng(p.latitude, p.longitude);
        await _startGpsStream(requestPermission: false);
        online = true;
      } else {
        await trips.availability(false);
        await gps?.cancel(); gps = null; online = false;
      }
    } catch (e) { error = '$e'; }
    if (mounted) setState(() => busy = false);
  }

  Future<void> _answer(bool accept) async {
    final t = trip, proposal = t?.activeProposal;
    if (t == null || proposal == null || busy) return;
    setState(() => busy = true);
    try {
      final updated = await trips.driverAnswer(t.id, proposal.id, t.stateVersion, accept: accept);
      invitationExpiry = null;
      if (accept) {
        trip = updated;
        await realtime.subscribeTrip(t.id);
        await _refreshTrip();
      } else {
        trip = null; route = null;
      }
    } catch (e) { error = '$e'; await _refreshTrip(); }
    if (mounted) setState(() => busy = false);
  }

  Future<void> _step(String step) async {
    final t = trip; if (t == null || busy) return;
    setState(() => busy = true);
    try { trip = await trips.driverStep(t.id, t.stateVersion, step); }
    catch (e) { error = '$e'; await _refreshTrip(); }
    if (mounted) setState(() => busy = false);
  }

  @override
  void dispose() {
    clock?.cancel(); events?.cancel(); gps?.cancel(); realtime.dispose(); super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (loading) return const Scaffold(body: Center(child: CircularProgressIndicator()));
    return Scaffold(
      body: IndexedStack(index: tab, children: [
        _dashboard(),
        _history(),
        _income(),
        SingleChildScrollView(child: widget.account),
      ]),
      bottomNavigationBar: NavigationBar(selectedIndex: tab, onDestinationSelected: (v) => setState(() => tab = v), destinations: const [
        NavigationDestination(icon: Icon(Icons.home_outlined), label: 'Trang chủ'),
        NavigationDestination(icon: Icon(Icons.history), label: 'Chuyến đi'),
        NavigationDestination(icon: Icon(Icons.bar_chart), label: 'Thu nhập'),
        NavigationDestination(icon: Icon(Icons.person_outline), label: 'Tài khoản'),
      ]),
    );
  }

  Widget _dashboard() => Stack(children: [
    Positioned.fill(child: GoongMap(routePolyline: route?.polyline, pickup: trip == null ? null : LatLng(trip!.pickupLatitude, trip!.pickupLongitude), destination: trip == null ? null : LatLng(trip!.destinationLatitude, trip!.destinationLongitude), driverLocation: driverLocation, routeToPickup: trip?.status == 'DriverAccepted' || trip?.status == 'EnRoute')), 
    Positioned(top: 0, left: 0, right: 0, child: SafeArea(child: Padding(padding: const EdgeInsets.all(18), child: Row(children: [const Brand(size: 25), const Spacer(), CircleAvatar(backgroundColor: Colors.white, child: Icon(Icons.person, color: green))])))),
    Align(alignment: Alignment.bottomCenter, child: _panel()),
  ]);

  Widget _panel() {
    final t = trip, p = t?.activeProposal;
    final status = t?.status ?? 'Idle';
    final invite = status == 'AwaitingDriverResponse';
    final accepted = status == 'DriverAccepted';
    final enRoute = status == 'EnRoute';
    final picked = status == 'PickedUp';
    return SafeArea(top: false, child: Container(width: double.infinity, padding: const EdgeInsets.fromLTRB(20, 18, 20, 18), decoration: const BoxDecoration(color: Colors.white, borderRadius: BorderRadius.vertical(top: Radius.circular(28))), child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
      Center(child: Container(width: 40, height: 4, decoration: BoxDecoration(color: const Color(0xFFD2D8DF), borderRadius: BorderRadius.circular(4)))),
      const SizedBox(height: 16),
      if (error != null) Text(error!, style: const TextStyle(color: Colors.red)),
      if (t == null || status == 'Cancelled' || status == 'Failed') ...[
        Text(online ? 'Đang chờ chuyến' : 'Bạn đang ngoại tuyến', style: const TextStyle(fontSize: 21, fontWeight: FontWeight.bold)),
        const SizedBox(height: 14),
        SizedBox(width: double.infinity, child: FilledButton(onPressed: busy ? null : _toggleOnline, child: Text(online ? 'Tắt nhận chuyến' : 'Bật nhận chuyến'))),
      ] else ...[
        Text(switch (status) { 'AwaitingDriverResponse' => 'Lời mời chuyến mới', 'DriverAccepted' => 'Đã nhận chuyến', 'EnRoute' => 'Đang đến đón khách', 'PickedUp' => 'Đang chở khách', 'DroppedOff' || 'PaymentPending' => 'Đã trả khách', _ => 'Đang xử lý chuyến' }, style: const TextStyle(fontSize: 21, fontWeight: FontWeight.bold)),
        const SizedBox(height: 10),
        if (invite && invitationExpiry != null) Text('Thời gian phản hồi: ${invitationExpiry!.difference(DateTime.now()).inSeconds.clamp(0, 30)} giây', style: const TextStyle(color: green, fontWeight: FontWeight.bold)),
        if (t.riderName != null) Text('Khách hàng: ${t.riderName}', style: const TextStyle(fontWeight: FontWeight.w600)),
        if (t.riderPhone != null && (accepted || enRoute || picked)) Row(children: [Expanded(child: Text('SĐT: ${t.riderPhone}')), IconButton(onPressed: () => launchUrl(Uri(scheme: 'tel', path: t.riderPhone)), icon: const Icon(Icons.call, color: green), tooltip: 'Gọi khách')]),
        const SizedBox(height: 8), _address(Icons.trip_origin, 'Điểm đón', t.pickupAddress), _address(Icons.location_on, 'Điểm đến', t.destinationAddress),
        const SizedBox(height: 8),
        Text('Giá chuyến: ${(p?.amount ?? t.agreedAmount ?? 0).toStringAsFixed(0)} đ  •  ${p?.distanceMeters == null ? 'Đang tính tuyến' : '${(p!.distanceMeters! / 1000).toStringAsFixed(1)} km'}'),
        if (route?.pickupEtaMinutes != null && (invite || accepted || enRoute)) Text('Tới đón khoảng ${route!.pickupEtaMinutes} phút', style: const TextStyle(color: green, fontWeight: FontWeight.w600)),
        if (picked && route?.durationSeconds != null) Text('Tới điểm đến khoảng ${(route!.durationSeconds! / 60).ceil()} phút', style: const TextStyle(color: green, fontWeight: FontWeight.w600)),
        const SizedBox(height: 14),
        if (invite) Row(children: [Expanded(child: OutlinedButton(onPressed: busy ? null : () => _answer(false), child: const Text('Từ chối'))), const SizedBox(width: 10), Expanded(child: FilledButton(onPressed: busy ? null : () => _answer(true), child: const Text('Nhận chuyến')))]),
        if (accepted) SizedBox(width: double.infinity, child: FilledButton(onPressed: busy ? null : () => _step('en-route'), child: const Text('Bắt đầu đến đón'))),
        if (enRoute) SizedBox(width: double.infinity, child: FilledButton(onPressed: busy ? null : () => _step('pickup'), child: const Text('Đã đón khách'))),
        if (picked) SizedBox(width: double.infinity, child: FilledButton(onPressed: busy ? null : () => _step('dropoff'), child: const Text('Đã trả khách'))),
        if (status == 'PaymentPending') Text(t.paymentStatus == 'Failed' ? 'Thanh toán chưa thành công' : 'Đang xử lý thanh toán theo hệ thống…', style: const TextStyle(color: green)),
        if (status == 'Paid') const Text('Thanh toán thành công', style: TextStyle(color: green)),
      ],
    ])));
  }

  Widget _address(IconData icon, String label, String value) => Padding(padding: const EdgeInsets.symmetric(vertical: 5), child: Row(children: [Icon(icon, color: green, size: 19), const SizedBox(width: 8), Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(label, style: const TextStyle(color: muted, fontSize: 11)), Text(value, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w600))]))]));
  Widget _history() => FutureBuilder<List<TripSnapshot>>(future: trips.history(), builder: (context, snap) => _listPage('Chuyến đi', snap.data ?? const []));
  Widget _listPage(String title, List<TripSnapshot> items) => Scaffold(appBar: AppBar(title: Text(title)), body: ListView(children: [for (final t in items) ListTile(title: Text(t.destinationAddress), subtitle: Text('${t.pickupAddress}\n${t.status}'), trailing: Text('${(t.agreedAmount ?? 0).toStringAsFixed(0)} đ'), onTap: () => showDialog<void>(context: context, builder: (context) => AlertDialog(title: const Text('Chi tiết chuyến đi'), content: Text('Khách: ${t.riderName ?? 'Khách hàng'}\nĐón: ${t.pickupAddress}\nĐến: ${t.destinationAddress}\nTrạng thái: ${t.status}\nGiá: ${(t.agreedAmount ?? 0).toStringAsFixed(0)} đ'), actions: [TextButton(onPressed: () => Navigator.pop(context), child: const Text('Đóng'))])))]));
  Widget _income() => FutureBuilder<Object?>(future: earningsFuture, builder: (context, snap) {
    final data = snap.data is Map<String, dynamic> ? snap.data as Map<String, dynamic> : const <String, dynamic>{};
    final amount = (data['totalEarnings'] as num?)?.toDouble() ?? 0;
    final items = data['transactions'] as List? ?? const [];
    return Scaffold(appBar: AppBar(title: const Text('Thu nhập')), body: ListView(padding: const EdgeInsets.all(20), children: [const Text('Tổng thu nhập'), Text('${amount.toStringAsFixed(0)} đ', style: const TextStyle(fontSize: 28, fontWeight: FontWeight.bold, color: green)), const SizedBox(height: 16), for (final item in items.whereType<Map>()) ListTile(leading: const Icon(Icons.payments, color: green), title: Text('${item['amount'] ?? 0} đ'), subtitle: Text('${item['paidAt'] ?? ''}'))]));
  });
}
