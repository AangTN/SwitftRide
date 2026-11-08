import 'package:flutter/material.dart';
import 'app.dart' show green, ink, muted;
import 'trip_models.dart';
import 'trip_widgets.dart';

class RiderTripPanel extends StatelessWidget {
  const RiderTripPanel({
    super.key,
    required this.trip,
    required this.onApprove,
    required this.onReject,
    required this.onCancel,
    required this.onDismiss,
    this.route,
    this.progress,
    this.busy = false,
    this.error,
    this.now,
  });
  final TripSnapshot trip;
  final TripRouteSnapshot? route;
  final TripProgress? progress;
  final bool busy;
  final String? error;
  final DateTime? now;
  final VoidCallback onApprove, onReject, onCancel, onDismiss;
  @override
  Widget build(BuildContext context) {
    final p = trip.currentProposal, s = trip.status;
    final review = s == TripStatus.awaitingRiderApproval,
        waiting = s == TripStatus.awaitingDriverResponse;
    final approaching =
        s == TripStatus.enRoute || s == TripStatus.driverAccepted;
    final moving = s == TripStatus.pickedUp;
    final searching = s == TripStatus.matching || s == TripStatus.requested;
    final remaining = p?.responseDeadline
        ?.difference(now ?? DateTime.now())
        .inSeconds
        .clamp(0, 9999);
    final cancel = {
      TripStatus.matching,
      TripStatus.requested,
      TripStatus.awaitingRiderApproval,
      TripStatus.awaitingDriverResponse,
      TripStatus.driverAccepted,
      TripStatus.enRoute,
    }.contains(s);
    return TripSheet(
      title: s.label,
      trailing: (review || waiting) && remaining != null
          ? TripCountdown(
              key: const Key('trip-countdown'),
              seconds: remaining,
              total: review ? 90 : 30,
              size: 54,
            )
          : null,
      children: [
        if (searching) ...[
          const LinearProgressIndicator(
            color: green,
            backgroundColor: tripMint,
          ),
          const SizedBox(height: 12),
          const Text(
            'Đang tìm tài xế phù hợp gần điểm đón của bạn',
            style: TextStyle(color: muted, fontSize: 13),
          ),
          const SizedBox(height: 16),
        ],
        if (p != null && p.status != ProposalStatus.searching) ...[
          TripPersonCard(
            name: p.driverName ?? 'Tài xế SwiftRide',
            subtitle: p.vehicleName,
            plate: p.plateNumber,
            avatar: p.driverAvatarImageKey,
            vehicleType: trip.vehicleType,
          ),
          const SizedBox(height: 12),
        ],
        if (approaching) ...[
          TripEta(
            text:
                progress?.label ??
                (route?.pickupEtaMinutes != null
                    ? 'Tài xế tới đón trong ${route!.pickupEtaMinutes} phút'
                    : 'Tài xế đang di chuyển tới điểm đón'),
          ),
          const SizedBox(height: 12),
        ],
        if (moving) ...[
          TripEta(
            text:
                progress?.label ??
                (route?.toDestination != null
                    ? '${(route!.toDestination!.durationSeconds / 60).ceil()} phút tới điểm đến'
                    : 'Đang trên đường tới điểm đến'),
          ),
          const SizedBox(height: 12),
        ],
        if (searching)
          TripJourneyCard(trip: trip, compact: true)
        else if (review || waiting)
          ExpansionTile(
            dense: true,
            visualDensity: VisualDensity.compact,
            tilePadding: EdgeInsets.zero,
            leading: const Icon(Icons.route_rounded, color: green, size: 20),
            title: Text(
              '${trip.pickupAddress.split(',').first} → ${trip.destinationAddress.split(',').first}',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
            ),
            children: [TripJourneyCard(trip: trip)],
          )
        else if (approaching || moving)
          ExpansionTile(
            tilePadding: EdgeInsets.zero,
            childrenPadding: const EdgeInsets.only(bottom: 12),
            title: Text(
              moving ? trip.destinationAddress : trip.pickupAddress,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                color: ink,
                fontWeight: FontWeight.w600,
                fontSize: 13,
              ),
            ),
            subtitle: Text(
              moving ? 'Điểm đến' : 'Điểm đón',
              style: const TextStyle(color: muted, fontSize: 11),
            ),
            leading: const Icon(Icons.location_on_rounded, color: green),
            children: [TripJourneyCard(trip: trip)],
          ),
        if (p?.amount != null) ...[
          const SizedBox(height: 12),
          TripMetrics(trip: trip),
          const SizedBox(height: 8),
          TripPaymentMethodLabel(trip: trip),
        ],
        if (error != null) ...[
          const SizedBox(height: 10),
          Text(
            error!,
            key: const Key('trip-error'),
            style: const TextStyle(color: Color(0xFFB42318), fontSize: 12),
          ),
        ],
        if (s == TripStatus.failed || s == TripStatus.cancelled) ...[
          const SizedBox(height: 12),
          Text(
            s == TripStatus.failed
                ? 'Chưa tìm được tài xế phù hợp. Bạn có thể thử đặt lại.'
                : 'Chuyến đi đã được hủy.',
            style: const TextStyle(color: muted, fontSize: 13),
          ),
          const SizedBox(height: 12),
          TripAction(label: 'Về trang chủ', onTap: onDismiss),
        ],
        if (review) ...[
          const SizedBox(height: 16),
          TripAction(label: 'Đồng ý', onTap: onApprove, busy: busy),
          const SizedBox(height: 8),
          TripAction(
            label: 'Từ chối',
            onTap: onReject,
            busy: busy,
            secondary: true,
          ),
        ],
        if (cancel && !review) ...[
          const SizedBox(height: 10),
          TripAction(
            label: 'Hủy chuyến',
            onTap: onCancel,
            busy: busy,
            secondary: true,
          ),
        ],
      ],
    );
  }
}

class RiderTripHistory extends StatelessWidget {
  const RiderTripHistory({
    super.key,
    required this.trips,
    required this.loading,
    required this.onRetry,
    required this.onSelected,
    this.error,
  });

  final List<TripSnapshot> trips;
  final bool loading;
  final String? error;
  final Future<void> Function() onRetry;
  final ValueChanged<TripSnapshot> onSelected;

  @override
  Widget build(BuildContext context) => SafeArea(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Padding(
          padding: EdgeInsets.fromLTRB(20, 20, 20, 14),
          child: Text(
            'Chuyến đi',
            style: TextStyle(fontSize: 22, fontWeight: FontWeight.w700),
          ),
        ),
        if (error != null)
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(error!, style: const TextStyle(color: Color(0xFFB3261E))),
                TextButton(onPressed: onRetry, child: const Text('Thử lại')),
              ],
            ),
          ),
        if (loading && trips.isEmpty)
          const Expanded(child: Center(child: CircularProgressIndicator()))
        else if (trips.isEmpty && error == null)
          const Expanded(
            child: Center(
              child: Text(
                'Chưa có chuyến đi nào.',
                style: TextStyle(color: muted),
              ),
            ),
          )
        else
          Expanded(
            child: RefreshIndicator(
              onRefresh: onRetry,
              child: ListView.separated(
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
                itemCount: trips.length,
                separatorBuilder: (_, _) => const SizedBox(height: 8),
                itemBuilder: (context, index) {
                  final trip = trips[index];
                  return Card(
                    color: const Color(0xFFF9FBF9),
                    elevation: 0,
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(16),
                      side: const BorderSide(color: Color(0xFFE6ECE8)),
                    ),
                    child: InkWell(
                      borderRadius: BorderRadius.circular(16),
                      onTap: () => onSelected(trip),
                      child: Padding(
                        padding: const EdgeInsets.all(14),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              '${_formatDate(trip.createdAt)} · ${trip.paymentMethodLabel}',
                              style: const TextStyle(
                                color: muted,
                                fontSize: 11,
                              ),
                            ),
                            const SizedBox(height: 9),
                            _LocationRow(
                              icon: Icons.trip_origin_rounded,
                              title: 'Điểm đón',
                              address: trip.pickupAddress,
                            ),
                            const SizedBox(height: 7),
                            _LocationRow(
                              icon: Icons.place_rounded,
                              title: 'Điểm đến',
                              address: trip.destinationAddress,
                            ),
                            const Divider(height: 24),
                            Row(
                              children: [
                                Text(
                                  formatVnd(
                                    trip.agreedAmount ??
                                        trip.currentProposal?.amount,
                                  ),
                                  style: const TextStyle(
                                    fontSize: 16,
                                    fontWeight: FontWeight.w700,
                                  ),
                                ),
                                const Spacer(),
                                _StatusBadge(
                                  status: trip.status,
                                  paymentStatus: trip.paymentStatus,
                                ),
                                const Icon(
                                  Icons.chevron_right_rounded,
                                  color: muted,
                                ),
                              ],
                            ),
                          ],
                        ),
                      ),
                    ),
                  );
                },
              ),
            ),
          ),
      ],
    ),
  );
}

class RiderTripDetailsSheet extends StatelessWidget {
  const RiderTripDetailsSheet({super.key, required this.trip});
  final TripSnapshot trip;
  @override
  Widget build(BuildContext c) {
    final p = trip.currentProposal;
    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              tripFinished(trip) ? 'Chuyến đi hoàn tất' : trip.status.label,
              style: const TextStyle(
                color: ink,
                fontSize: 23,
                fontWeight: FontWeight.w800,
              ),
            ),
            const SizedBox(height: 18),
            Text(
              moneyVnd(trip.agreedAmount ?? p?.amount),
              style: const TextStyle(
                color: green,
                fontSize: 30,
                fontWeight: FontWeight.w800,
              ),
            ),
            const SizedBox(height: 12),
            TripPaymentMethodLabel(trip: trip),
            const SizedBox(height: 8),
            if (tripFinished(trip))
              TripPaymentBadge(
                status: trip.status == TripStatus.paid
                    ? 'Succeeded'
                    : trip.paymentStatus,
              ),
            const SizedBox(height: 16),
            TripJourneyCard(trip: trip),
            const SizedBox(height: 12),
            TripPersonCard(
              name: p?.driverName ?? 'Chưa được phân công',
              subtitle: p?.vehicleName,
              plate: p?.plateNumber,
              avatar: p?.driverAvatarImageKey,
              vehicleType: trip.vehicleType,
            ),
            const SizedBox(height: 12),
            TripMetrics(trip: trip, showFare: false),
          ],
        ),
      ),
    );
  }
}

class _LocationRow extends StatelessWidget {
  const _LocationRow({
    required this.icon,
    required this.title,
    required this.address,
  });

  final IconData icon;
  final String title;
  final String address;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Icon(icon, size: 19, color: green),
      const SizedBox(width: 8),
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: const TextStyle(color: muted, fontSize: 11)),
            Text(
              address,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
            ),
          ],
        ),
      ),
    ],
  );
}

class _StatusBadge extends StatelessWidget {
  const _StatusBadge({required this.status, this.paymentStatus});
  final TripStatus status;
  final String? paymentStatus;

  @override
  Widget build(BuildContext context) {
    final (label, color, background) = switch (status) {
      TripStatus.paid => ('Đã thanh toán', green, const Color(0xFFE5F8EE)),
      TripStatus.cancelled => ('Đã hủy', muted, const Color(0xFFF0F2F4)),
      TripStatus.failed => (
        'Thất bại',
        const Color(0xFFB3261E),
        const Color(0xFFFFEBEE),
      ),
      TripStatus.paymentPending when paymentStatus == 'Failed' => (
        'Thanh toán lỗi',
        const Color(0xFFB42318),
        const Color(0xFFFFEBEE),
      ),
      TripStatus.paymentPending => (
        'Đang xử lý',
        const Color(0xFFD97900),
        const Color(0xFFFFF3E0),
      ),
      _ => ('Đang thực hiện', green, const Color(0xFFE5F8EE)),
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(
        label,
        style: TextStyle(
          fontSize: 10,
          color: color,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

String formatVnd(double? amount) {
  if (amount == null) return '—';
  final rounded = amount.round().toString();
  final grouped = rounded.replaceAllMapped(
    RegExp(r'\B(?=(\d{3})+(?!\d))'),
    (_) => '.',
  );
  return '$grouped ₫';
}

String _formatDate(DateTime date) {
  const weekdays = [
    'Thứ Hai',
    'Thứ Ba',
    'Thứ Tư',
    'Thứ Năm',
    'Thứ Sáu',
    'Thứ Bảy',
    'Chủ Nhật',
  ];
  final local = date.toLocal();
  return '${weekdays[local.weekday - 1]}, ${local.day.toString().padLeft(2, '0')}/'
      '${local.month.toString().padLeft(2, '0')}/${local.year}';
}
