import 'package:flutter/material.dart';
import 'app.dart' show Brand, green, ink, muted;
import 'trip_models.dart';
import 'trip_api.dart';
import 'ride_locations.dart' show BookingVehicleArt;

const tripMint = Color(0xFFEDF9F1);
const tripBorder = Color(0xFFE3EBE7);
bool tripFinished(TripSnapshot t) => {
  TripStatus.droppedOff,
  TripStatus.paymentPending,
  TripStatus.paid,
}.contains(t.status);
String moneyVnd(num? amount) {
  if (amount == null) return 'Đang tính giá';
  final digits = amount.round().toString();
  return '${digits.replaceAllMapped(RegExp(r'(\d)(?=(\d{3})+(?!\d))'), (m) => '${m[1]}.')} ₫';
}

class TripHistoryCard extends StatelessWidget {
  const TripHistoryCard({super.key, required this.trip, required this.onTap});
  final TripSnapshot trip;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final date = trip.createdAt.toLocal();
    const weekdays = [
      'Thứ Hai',
      'Thứ Ba',
      'Thứ Tư',
      'Thứ Năm',
      'Thứ Sáu',
      'Thứ Bảy',
      'Chủ Nhật',
    ];
    String two(int value) => value.toString().padLeft(2, '0');
    final dateText =
        '${weekdays[date.weekday - 1]}, ${two(date.day)}/${two(date.month)}/${date.year} · ${two(date.hour)}:${two(date.minute)}';
    final (status, statusColor, statusBackground, icon) = switch (trip.status) {
      TripStatus.paid => (
        'Đã hoàn thành',
        const Color(0xFF087834),
        const Color(0xFFDDF5E3),
        Icons.check_rounded,
      ),
      TripStatus.cancelled => (
        'Đã hủy',
        muted,
        const Color(0xFFF0F2F4),
        Icons.close_rounded,
      ),
      TripStatus.failed => (
        'Thất bại',
        const Color(0xFFB3261E),
        const Color(0xFFFFEBEE),
        Icons.error_outline_rounded,
      ),
      TripStatus.paymentPending when trip.paymentStatus == 'Failed' => (
        'Thanh toán lỗi',
        const Color(0xFFB42318),
        const Color(0xFFFFEBEE),
        Icons.error_outline_rounded,
      ),
      TripStatus.droppedOff || TripStatus.paymentPending => (
        'Đang xử lý',
        const Color(0xFF996000),
        const Color(0xFFFFF3E0),
        Icons.schedule_rounded,
      ),
      _ => ('Đang thực hiện', green, tripMint, Icons.schedule_rounded),
    };
    return Material(
      color: Colors.white,
      elevation: 1,
      shadowColor: const Color(0x180D3721),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(15),
        side: const BorderSide(color: tripBorder),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(14, 10, 10, 6),
              child: Row(
                children: [
                  const Icon(
                    Icons.calendar_today_outlined,
                    size: 15,
                    color: muted,
                  ),
                  const SizedBox(width: 7),
                  Expanded(
                    child: Text(
                      dateText,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        color: muted,
                        fontSize: 11,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                  const SizedBox(width: 5),
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 8,
                      vertical: 4,
                    ),
                    decoration: BoxDecoration(
                      color: statusBackground,
                      borderRadius: BorderRadius.circular(20),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(icon, color: statusColor, size: 14),
                        const SizedBox(width: 3),
                        Text(
                          status,
                          style: TextStyle(
                            color: statusColor,
                            fontSize: 10,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(14, 3, 14, 9),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Column(
                    children: [
                      Icon(Icons.trip_origin_rounded, size: 17, color: green),
                      _HistoryDots(),
                      Icon(Icons.location_on_rounded, size: 17, color: green),
                    ],
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        _HistoryStop(
                          label: 'Điểm đón',
                          address: trip.pickupAddress,
                        ),
                        const SizedBox(height: 6),
                        _HistoryStop(
                          label: 'Điểm đến',
                          address: trip.destinationAddress,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
              decoration: const BoxDecoration(
                color: Color(0xFFF0FAF3),
                border: Border(top: BorderSide(color: tripBorder)),
              ),
              child: Row(
                children: [
                  Icon(
                    trip.isCash
                        ? Icons.payments_outlined
                        : Icons.account_balance_wallet_outlined,
                    color: const Color(0xFF087834),
                    size: 21,
                  ),
                  const SizedBox(width: 9),
                  Expanded(
                    child: Text(
                      moneyVnd(
                        trip.agreedAmount ?? trip.currentProposal?.amount,
                      ),
                      style: const TextStyle(
                        color: Color(0xFF087834),
                        fontSize: 16,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ),
                  Container(
                    decoration: BoxDecoration(
                      color: const Color(0xFFDDF5E3),
                      borderRadius: BorderRadius.circular(7),
                    ),
                    child: const Icon(
                      Icons.chevron_right_rounded,
                      color: Color(0xFF087834),
                      size: 24,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _HistoryStop extends StatelessWidget {
  const _HistoryStop({required this.label, required this.address});
  final String label, address;
  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        label,
        style: const TextStyle(
          color: muted,
          fontSize: 10,
          fontWeight: FontWeight.w600,
        ),
      ),
      Text(
        address,
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: const TextStyle(
          color: ink,
          fontSize: 12,
          fontWeight: FontWeight.w700,
        ),
      ),
    ],
  );
}

class _HistoryDots extends StatelessWidget {
  const _HistoryDots();
  @override
  Widget build(BuildContext context) => SizedBox(
    height: 20,
    width: 17,
    child: Column(
      mainAxisAlignment: MainAxisAlignment.spaceEvenly,
      children: [
        for (var i = 0; i < 5; i++)
          Container(
            width: 2,
            height: 2,
            decoration: const BoxDecoration(
              color: green,
              shape: BoxShape.circle,
            ),
          ),
      ],
    ),
  );
}

class TripCard extends StatelessWidget {
  const TripCard({
    super.key,
    required this.child,
    this.color = Colors.white,
    this.padding = 16,
  });
  final Widget child;
  final Color color;
  final double padding;
  @override
  Widget build(BuildContext c) =>
      Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: child);
}

class TripAction extends StatelessWidget {
  const TripAction({
    super.key,
    required this.label,
    required this.onTap,
    this.busy = false,
    this.secondary = false,
  });
  final String label;
  final VoidCallback? onTap;
  final bool busy, secondary;
  @override
  Widget build(BuildContext c) => SizedBox(
    width: double.infinity,
    height: 48,
    child: secondary
        ? OutlinedButton(
            onPressed: busy ? null : onTap,
            style: OutlinedButton.styleFrom(
              foregroundColor: green,
              side: const BorderSide(color: green),
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(14),
              ),
            ),
            child: Text(
              label,
              style: const TextStyle(fontWeight: FontWeight.w700),
            ),
          )
        : FilledButton(
            onPressed: busy ? null : onTap,
            style: FilledButton.styleFrom(
              backgroundColor: green,
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(14),
              ),
            ),
            child: busy
                ? const SizedBox(
                    width: 20,
                    height: 20,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: Colors.white,
                    ),
                  )
                : Text(
                    label,
                    style: const TextStyle(
                      fontSize: 15,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
          ),
  );
}

class TripJourneyCard extends StatelessWidget {
  const TripJourneyCard({
    super.key,
    required this.trip,
    this.compact = false,
    this.fullAddress = false,
  });
  final TripSnapshot trip;
  final bool compact;
  final bool fullAddress;
  @override
  Widget build(BuildContext c) => TripCard(
    padding: 14,
    child: Column(
      children: [
        _stop('Điểm đón', trip.pickupAddress, false),
        const Padding(
          padding: EdgeInsets.symmetric(vertical: 10),
          child: Divider(height: 1, indent: 30),
        ),
        _stop('Điểm đến', trip.destinationAddress, true),
      ],
    ),
  );
  Widget _stop(String label, String value, bool destination) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Padding(
        padding: const EdgeInsets.only(top: 5),
        child: Icon(
          destination ? Icons.location_on_rounded : Icons.trip_origin_rounded,
          color: green,
          size: 21,
        ),
      ),
      const SizedBox(width: 10),
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: const TextStyle(
                color: muted,
                fontSize: 11,
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 3),
            Text(
              value,
              maxLines: fullAddress ? null : (compact ? 1 : 2),
              overflow: fullAddress
                  ? TextOverflow.visible
                  : TextOverflow.ellipsis,
              style: const TextStyle(
                color: ink,
                fontSize: 13,
                fontWeight: FontWeight.w600,
              ),
            ),
          ],
        ),
      ),
    ],
  );
}

class TripStopCard extends StatelessWidget {
  const TripStopCard({
    super.key,
    required this.label,
    required this.address,
    this.icon = Icons.location_on_rounded,
  });
  final String label;
  final String address;
  final IconData icon;
  @override
  Widget build(BuildContext c) => TripCard(
    padding: 12,
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 2),
          child: Icon(icon, color: green, size: 21),
        ),
        const SizedBox(width: 9),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                label,
                style: const TextStyle(
                  color: muted,
                  fontSize: 11,
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(height: 3),
              Text(
                address,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  color: ink,
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

class TripPersonCard extends StatelessWidget {
  const TripPersonCard({
    super.key,
    required this.name,
    this.subtitle,
    this.plate,
    this.avatar,
    this.vehicleType,
    this.onCall,
  });
  final String name;
  final String? subtitle, plate, avatar, vehicleType;
  final VoidCallback? onCall;
  @override
  Widget build(BuildContext c) => TripCard(
    padding: 12,
    child: Row(
      children: [
        CircleAvatar(
          radius: 24,
          backgroundColor: tripMint,
          child: ClipOval(
            child:
                avatar != null &&
                    (avatar!.startsWith('https://') ||
                        avatar!.startsWith('http://'))
                ? Image.network(
                    avatar!,
                    width: 48,
                    height: 48,
                    fit: BoxFit.cover,
                    errorBuilder: (_, error, stack) =>
                        const Icon(Icons.person_rounded, color: muted),
                  )
                : const Icon(Icons.person_rounded, color: muted, size: 29),
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                name,
                style: const TextStyle(
                  color: ink,
                  fontWeight: FontWeight.w700,
                  fontSize: 14,
                ),
              ),
              if (subtitle?.isNotEmpty == true) ...[
                const SizedBox(height: 3),
                Text(
                  subtitle!,
                  style: const TextStyle(color: muted, fontSize: 12),
                ),
              ],
              if (plate?.isNotEmpty == true) ...[
                const SizedBox(height: 5),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 8,
                    vertical: 3,
                  ),
                  decoration: BoxDecoration(
                    color: tripMint,
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Text(
                    plate!,
                    style: const TextStyle(
                      color: ink,
                      fontSize: 12,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
              ],
            ],
          ),
        ),
        if (vehicleType != null)
          SizedBox(
            width: 76,
            height: 58,
            child: BookingVehicleArt(
              index: vehicleType == 'Motorbike'
                  ? 1
                  : vehicleType == 'Car4Seats'
                  ? 2
                  : 3,
            ),
          ),
        if (onCall != null)
          IconButton.filledTonal(
            style: IconButton.styleFrom(
              backgroundColor: tripMint,
              foregroundColor: green,
            ),
            onPressed: onCall,
            icon: const Icon(Icons.call_rounded),
          ),
      ],
    ),
  );
}

class TripMetrics extends StatelessWidget {
  const TripMetrics({super.key, required this.trip, this.showFare = true});
  final TripSnapshot trip;
  final bool showFare;
  @override
  Widget build(BuildContext c) {
    final p = trip.currentProposal;
    final actual = trip.pickedUpAt != null && trip.droppedOffAt != null
        ? trip.droppedOffAt!.difference(trip.pickedUpAt!).inMinutes
        : null;
    return TripCard(
      color: tripMint,
      padding: 13,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text(
            'Quãng đường chuyến',
            style: TextStyle(
              color: muted,
              fontSize: 11,
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 5),
          Row(
            children: [
              const Icon(Icons.route_rounded, color: green, size: 18),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  '${p?.distanceMeters == null ? 'Đang cập nhật' : '${(p!.distanceMeters! / 1000).toStringAsFixed(1)} km'}${actual != null
                      ? ' · $actual phút'
                      : p?.estimatedDurationSeconds != null
                      ? ' · dự kiến ${(p!.estimatedDurationSeconds! / 60).ceil()} phút'
                      : ''}',
                  style: const TextStyle(
                    color: ink,
                    fontSize: 12,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
              if (showFare)
                Text(
                  moneyVnd(trip.agreedAmount ?? p?.amount),
                  style: const TextStyle(
                    fontWeight: FontWeight.w800,
                    fontSize: 19,
                    color: green,
                  ),
                ),
            ],
          ),
        ],
      ),
    );
  }
}

class TripFareSummary extends StatelessWidget {
  const TripFareSummary({super.key, required this.trip});
  final TripSnapshot trip;
  @override
  Widget build(BuildContext c) => Row(
    children: [
      const Icon(Icons.receipt_long_outlined, color: muted, size: 19),
      const SizedBox(width: 12),
      Expanded(
        child: Text(
          moneyVnd(trip.agreedAmount ?? trip.currentProposal?.amount),
          style: const TextStyle(
            color: ink,
            fontSize: 14,
            fontWeight: FontWeight.w700,
          ),
        ),
      ),
      TripPaymentMethodLabel(trip: trip),
    ],
  );
}

class TripEta extends StatelessWidget {
  const TripEta({super.key, required this.text});
  final String text;
  @override
  Widget build(BuildContext c) => Row(
    children: [
      const Icon(Icons.schedule_rounded, color: green, size: 19),
      const SizedBox(width: 8),
      Expanded(
        child: Text(
          text,
          style: const TextStyle(
            color: green,
            fontWeight: FontWeight.w700,
            fontSize: 14,
          ),
        ),
      ),
    ],
  );
}

class TripCountdown extends StatelessWidget {
  const TripCountdown({
    super.key,
    required this.seconds,
    required this.total,
    this.size = 76,
  });
  final int seconds, total;
  final double size;
  @override
  Widget build(BuildContext c) => SizedBox(
    width: size,
    height: size,
    child: Stack(
      alignment: Alignment.center,
      children: [
        SizedBox.expand(
          child: CircularProgressIndicator(
            value: (seconds / total).clamp(0, 1),
            strokeWidth: 4,
            backgroundColor: const Color(0xFFDDF4E5),
            color: green,
          ),
        ),
        Text(
          '${(seconds ~/ 60).toString().padLeft(2, '0')}:${(seconds % 60).toString().padLeft(2, '0')}',
          style: TextStyle(
            fontSize: size < 70 ? 14 : 18,
            fontWeight: FontWeight.w800,
            color: green,
          ),
        ),
      ],
    ),
  );
}

class TripPaymentBadge extends StatelessWidget {
  const TripPaymentBadge({super.key, required this.status, this.reason});
  final String? status, reason;
  @override
  Widget build(BuildContext c) {
    final success = status?.toLowerCase() == 'succeeded',
        failed = status?.toLowerCase() == 'failed';
    final color = success
        ? green
        : failed
        ? const Color(0xFFB42318)
        : const Color(0xFFB66B00);
    final message = success
        ? 'Thanh toán thành công'
        : failed
        ? switch (reason) {
            'insufficient_funds' => 'Ví không đủ số dư',
            'wallet_not_found' => 'Ví SwiftRide chưa được khởi tạo',
            _ => 'Thanh toán chưa thành công',
          }
        : 'Đang xử lý thanh toán';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
      decoration: BoxDecoration(
        color: color.withValues(alpha: .08),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            success
                ? Icons.check_circle_rounded
                : failed
                ? Icons.error_outline_rounded
                : Icons.schedule_rounded,
            color: color,
            size: 18,
          ),
          const SizedBox(width: 7),
          Flexible(
            child: Text(
              message,
              style: TextStyle(
                color: color,
                fontSize: 12,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class TripSheet extends StatefulWidget {
  const TripSheet({
    super.key,
    required this.title,
    required this.children,
    this.trailing,
  });
  final String title;
  final List<Widget> children;
  final Widget? trailing;
  @override
  State<TripSheet> createState() => _TripSheetState();
}

class _TripSheetState extends State<TripSheet>
    with SingleTickerProviderStateMixin {
  final _sheetKey = GlobalKey();
  final _headerKey = GlobalKey();
  late final AnimationController _animation = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 220),
  )..addListener(_tick);

  bool collapsed = false;
  bool dragging = false;
  double? expandedHeight;
  double? headerHeight;
  double? height;
  double dragHeight = 0;
  double animationFrom = 0;
  double animationTo = 0;

  double get minHeight => (headerHeight ?? 46) + 24;

  void _tick() {
    if (!mounted) return;
    setState(() {
      height =
          animationFrom +
          (animationTo - animationFrom) *
              Curves.easeOutCubic.transform(_animation.value);
    });
  }

  @override
  void dispose() {
    _animation.dispose();
    super.dispose();
  }

  @override
  void didUpdateWidget(TripSheet old) {
    super.didUpdateWidget(old);
    if (old.title != widget.title) {
      _animation.stop();
      collapsed = false;
      height = null;
    }
  }

  void _measure() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final header =
          _headerKey.currentContext?.findRenderObject() as RenderBox?;
      if (header != null) {
        final value = header.size.height;
        if (headerHeight == null || (headerHeight! - value).abs() > .5) {
          setState(() => headerHeight = value);
        }
      }
      final sheet = _sheetKey.currentContext?.findRenderObject() as RenderBox?;
      if (sheet != null && !collapsed && height == null && !dragging) {
        final value = sheet.size.height;
        if (expandedHeight == null || (expandedHeight! - value).abs() > .5) {
          setState(() => expandedHeight = value);
        }
      }
    });
  }

  void _dragStart(DragStartDetails _) {
    _animation.stop();
    dragging = true;
    if (!collapsed && expandedHeight == null) {
      final box = _sheetKey.currentContext?.findRenderObject() as RenderBox?;
      if (box != null) expandedHeight = box.size.height;
    }
    dragHeight = collapsed
        ? minHeight
        : (height ?? expandedHeight ?? minHeight);
    if (collapsed) {
      setState(() {
        collapsed = false;
        height = minHeight;
      });
    }
  }

  void _dragUpdate(DragUpdateDetails details) {
    final max = expandedHeight;
    if (max == null) return;
    dragHeight = (dragHeight - details.delta.dy).clamp(minHeight, max);
    setState(() => height = dragHeight);
  }

  void _dragEnd(DragEndDetails details) {
    dragging = false;
    final max = expandedHeight;
    if (max == null) return;
    final velocity = details.primaryVelocity ?? 0;
    final target = velocity > 350
        ? minHeight
        : velocity < -350
        ? max
        : dragHeight < (minHeight + max) / 2
        ? minHeight
        : max;
    _animateTo(target);
  }

  void _animateTo(double target) {
    animationFrom =
        height ?? (collapsed ? minHeight : expandedHeight ?? target);
    animationTo = target;
    _animation.forward(from: 0).whenComplete(() {
      if (!mounted) return;
      setState(() {
        collapsed = target <= minHeight + .5;
        height = null;
      });
      _measure();
    });
  }

  @override
  Widget build(BuildContext c) {
    _measure();
    final maxHeight = MediaQuery.sizeOf(c).height * .76;
    final header = GestureDetector(
      behavior: HitTestBehavior.opaque,
      onVerticalDragStart: _dragStart,
      onVerticalDragUpdate: _dragUpdate,
      onVerticalDragEnd: _dragEnd,
      child: Column(
        key: _headerKey,
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Center(
            child: Container(
              width: 38,
              height: 4,
              margin: const EdgeInsets.only(top: 4, bottom: 12),
              decoration: BoxDecoration(
                color: const Color(0xFFCAD2CE),
                borderRadius: BorderRadius.circular(4),
              ),
            ),
          ),
          Row(
            children: [
              Expanded(
                child: Text(
                  widget.title,
                  key: const Key('trip-status'),
                  style: const TextStyle(
                    color: ink,
                    fontSize: 21,
                    fontWeight: FontWeight.w800,
                  ),
                ),
              ),
              ?widget.trailing,
            ],
          ),
          const SizedBox(height: 4),
        ],
      ),
    );
    return Material(
      color: Colors.white,
      elevation: 8,
      shadowColor: const Color(0x220D3721),
      borderRadius: const BorderRadius.vertical(top: Radius.circular(26)),
      child: Container(
        key: _sheetKey,
        width: double.infinity,
        height: height?.clamp(0.0, maxHeight),
        constraints: BoxConstraints(maxHeight: maxHeight),
        padding: const EdgeInsets.fromLTRB(18, 6, 18, 18),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            header,
            if (!collapsed)
              Flexible(
                child: SingleChildScrollView(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [const SizedBox(height: 12), ...widget.children],
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class TripCompletionPage extends StatefulWidget {
  const TripCompletionPage({
    super.key,
    required this.trip,
    required this.onContinue,
    this.driver = false,
    this.api,
  });
  final TripSnapshot trip;
  final VoidCallback onContinue;
  final bool driver;
  final TripApi? api;
  @override
  State<TripCompletionPage> createState() => _TripCompletionPageState();
}

class _TripCompletionPageState extends State<TripCompletionPage> {
  Future<Map<String, dynamic>?>? details;
  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(TripCompletionPage old) {
    super.didUpdateWidget(old);
    if (old.trip.id != widget.trip.id ||
        old.trip.paymentStatus != widget.trip.paymentStatus) {
      _load();
    }
  }

  void _load() {
    details = widget.trip.paymentStatus == 'Failed'
        ? widget.api?.payment(widget.trip.id)
        : null;
  }

  @override
  Widget build(BuildContext context) {
    final t = widget.trip, p = t.currentProposal;
    return ColoredBox(
      color: const Color(0xFFF6FAF7),
      child: SafeArea(
        child: Column(
          children: [
            const Padding(
              padding: EdgeInsets.fromLTRB(20, 16, 20, 8),
              child: Align(
                alignment: Alignment.centerLeft,
                child: Brand(size: 25),
              ),
            ),
            Expanded(
              child: SingleChildScrollView(
                padding: const EdgeInsets.fromLTRB(20, 8, 20, 18),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Center(
                      child: Container(
                        width: 64,
                        height: 64,
                        decoration: const BoxDecoration(
                          color: Color(0xFFDDF8E6),
                          shape: BoxShape.circle,
                        ),
                        child: const Icon(
                          Icons.check_rounded,
                          color: green,
                          size: 40,
                        ),
                      ),
                    ),
                    const SizedBox(height: 12),
                    const Text(
                      'Chuyến đi hoàn tất',
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        color: ink,
                        fontSize: 25,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    const SizedBox(height: 7),
                    Text(
                      widget.driver
                          ? 'Bạn đã đưa khách tới điểm đến'
                          : 'Cảm ơn bạn đã đồng hành cùng SwiftRide',
                      textAlign: TextAlign.center,
                      style: const TextStyle(color: muted, fontSize: 12),
                    ),
                    const SizedBox(height: 16),
                    const Text(
                      'Giá chuyến',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: muted, fontSize: 12),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      moneyVnd(t.agreedAmount ?? p?.amount),
                      textAlign: TextAlign.center,
                      style: const TextStyle(
                        color: green,
                        fontSize: 36,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    const SizedBox(height: 6),
                    Center(child: TripPaymentMethodLabel(trip: t)),
                    const SizedBox(height: 10),
                    Center(
                      child: FutureBuilder<Map<String, dynamic>?>(
                        future: details,
                        builder: (c, s) => TripPaymentBadge(
                          status: t.status == TripStatus.paid
                              ? 'Succeeded'
                              : t.paymentStatus,
                          reason: s.data?['failureCode'] as String?,
                        ),
                      ),
                    ),
                    const SizedBox(height: 20),
                    TripJourneyCard(trip: t),
                    const SizedBox(height: 12),
                    TripPersonCard(
                      name: widget.driver
                          ? t.riderName ?? 'Khách hàng'
                          : p?.driverName ?? 'Tài xế SwiftRide',
                      subtitle: widget.driver ? 'Khách hàng' : p?.vehicleName,
                      plate: widget.driver ? null : p?.plateNumber,
                      avatar: widget.driver ? null : p?.driverAvatarImageKey,
                      vehicleType: widget.driver ? null : t.vehicleType,
                    ),
                    const SizedBox(height: 12),
                    TripMetrics(trip: t, showFare: false),
                    const SizedBox(height: 14),
                  ],
                ),
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 16),
              child: TripAction(
                label: widget.driver ? 'Tiếp tục nhận chuyến' : 'Về trang chủ',
                onTap: widget.onContinue,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class TripNavigation extends StatelessWidget {
  const TripNavigation({
    super.key,
    required this.index,
    required this.onChanged,
    this.driver = false,
  });
  final int index;
  final ValueChanged<int> onChanged;
  final bool driver;
  @override
  Widget build(BuildContext context) {
    final labels = [
      'Trang chủ',
      'Chuyến đi',
      if (driver) 'Thu nhập',
      'Tài khoản',
    ];
    final icons = [
      Icons.home_rounded,
      Icons.history_rounded,
      if (driver) Icons.bar_chart_rounded,
      Icons.person_outline_rounded,
    ];
    return Container(
      decoration: const BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        boxShadow: [
          BoxShadow(
            color: Color(0x100D3721),
            blurRadius: 18,
            offset: Offset(0, -3),
          ),
        ],
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 10),
          child: Row(
            children: [
              for (var i = 0; i < labels.length; i++)
                Expanded(
                  child: Semantics(
                    selected: index == i,
                    child: InkWell(
                      onTap: () => onChanged(i),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(
                            icons[i],
                            color: i == index ? green : muted,
                            size: 25,
                          ),
                          const SizedBox(height: 5),
                          Text(
                            labels[i],
                            style: TextStyle(
                              color: i == index ? green : muted,
                              fontWeight: i == index
                                  ? FontWeight.w700
                                  : FontWeight.w500,
                              fontSize: 10,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class TripPaymentMethodLabel extends StatelessWidget {
  const TripPaymentMethodLabel({super.key, required this.trip});
  final TripSnapshot trip;
  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Icon(
        trip.isCash
            ? Icons.payments_rounded
            : Icons.account_balance_wallet_rounded,
        color: green,
        size: 16,
      ),
      const SizedBox(width: 6),
      Text(
        trip.paymentMethodLabel,
        style: const TextStyle(
          color: muted,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    ],
  );
}
