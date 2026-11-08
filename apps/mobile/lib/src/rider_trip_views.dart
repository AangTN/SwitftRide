import 'package:flutter/material.dart';

import 'app.dart' show green, ink, muted;
import 'trip_models.dart';

class RiderTripPanel extends StatelessWidget {
  const RiderTripPanel({
    super.key,
    required this.trip,
    required this.onApprove,
    required this.onReject,
    required this.onCancel,
    required this.onDismiss,
    this.route,
    this.busy = false,
    this.error,
    this.now,
  });

  final TripSnapshot trip;
  final TripRouteSnapshot? route;
  final bool busy;
  final String? error;
  final DateTime? now;
  final VoidCallback onApprove;
  final VoidCallback onReject;
  final VoidCallback onCancel;
  final VoidCallback onDismiss;

  @override
  Widget build(BuildContext context) {
    final proposal = trip.currentProposal;
    final deadline = proposal?.responseDeadline;
    final remaining = deadline == null
        ? null
        : deadline
              .difference(now ?? DateTime.now())
              .inSeconds
              .clamp(0, 9999)
              .toInt();
    final canReview = trip.status == TripStatus.awaitingRiderApproval;
    final canCancel = trip.status == TripStatus.matching ||
        trip.status == TripStatus.awaitingRiderApproval ||
        trip.status == TripStatus.awaitingDriverResponse ||
        trip.status == TripStatus.driverAccepted;
    final duration = proposal?.estimatedDurationSeconds;
    final distance = proposal?.distanceMeters;

    return Container(
      key: const Key('active-trip-panel'),
      width: double.infinity,
      constraints: const BoxConstraints(maxHeight: 470),
      padding: const EdgeInsets.fromLTRB(20, 12, 20, 18),
      decoration: const BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.vertical(top: Radius.circular(26)),
        boxShadow: [
          BoxShadow(
            color: Color(0x22000000),
            blurRadius: 22,
            offset: Offset(0, -4),
          ),
        ],
      ),
      child: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Center(
              child: Container(
                width: 38,
                height: 4,
                decoration: BoxDecoration(
                  color: const Color(0xFFC6CBD3),
                  borderRadius: BorderRadius.circular(4),
                ),
              ),
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: Text(
                    trip.status.label,
                    key: const Key('trip-status'),
                    style: const TextStyle(
                      color: ink,
                      fontSize: 20,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (remaining != null &&
                    (canReview ||
                        trip.status == TripStatus.awaitingDriverResponse))
                  _Countdown(seconds: remaining),
              ],
            ),
            const SizedBox(height: 14),
            _LocationRow(
              icon: Icons.trip_origin_rounded,
              title: 'Điểm đón',
              address: trip.pickupAddress,
            ),
            const SizedBox(height: 10),
            _LocationRow(
              icon: Icons.place_rounded,
              title: 'Điểm đến',
              address: trip.destinationAddress,
            ),
            if (proposal != null &&
                proposal.status != ProposalStatus.searching) ...[
              const Divider(height: 26),
              Row(
                children: [
                  _DriverAvatar(imageKey: proposal.driverAvatarImageKey),
                  const SizedBox(width: 11),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          proposal.driverName ?? 'Tài xế SwiftRide',
                          style: const TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        Text(
                          [
                            if (proposal.vehicleName?.isNotEmpty == true)
                              proposal.vehicleName!,
                            if (proposal.plateNumber?.isNotEmpty == true)
                              proposal.plateNumber!,
                          ].join(' · ').isEmpty
                              ? 'Thông tin tài xế'
                              : [
                                  if (proposal.vehicleName?.isNotEmpty == true)
                                    proposal.vehicleName!,
                                  if (proposal.plateNumber?.isNotEmpty == true)
                                    proposal.plateNumber!,
                                ].join(' · '),
                          style: const TextStyle(color: muted, fontSize: 12),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  if (distance != null)
                    _TripMetric(
                      icon: Icons.route_outlined,
                      text: '${(distance / 1000).toStringAsFixed(1)} km',
                    ),
                  if (duration != null)
                    _TripMetric(
                      icon: Icons.schedule_rounded,
                      text: '${(duration / 60).ceil()} phút',
                    ),
                  const Spacer(),
                  Text(
                    formatVnd(proposal.amount),
                    style: const TextStyle(
                      color: green,
                      fontSize: 19,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ],
              ),
            ],
            if (trip.status == TripStatus.enRoute &&
                route?.pickupEtaMinutes != null) ...[
              const SizedBox(height: 12),
              _EtaBanner(
                text: 'Tài xế tới đón trong ${route!.pickupEtaMinutes} phút',
              ),
            ],
            if (trip.status == TripStatus.pickedUp &&
                route?.toDestination != null) ...[
              const SizedBox(height: 12),
              _EtaBanner(
                text:
                    '${(route!.toDestination!.durationSeconds / 60).ceil()} phút tới điểm đến',
              ),
            ],
            if (trip.status == TripStatus.paymentPending)
              _PaymentState(status: trip.paymentStatus),
            if (trip.status == TripStatus.paid)
              _PaymentState(status: 'Succeeded'),
            if (trip.status == TripStatus.failed ||
                trip.status == TripStatus.cancelled)
              Padding(
                padding: const EdgeInsets.only(top: 12),
                child: Text(
                  trip.failureReason == 'matching_delivery_failed'
                      ? 'Không thể gửi yêu cầu tìm tài xế. Vui lòng đặt chuyến mới.'
                      : trip.status == TripStatus.failed
                      ? 'Chưa tìm được tài xế phù hợp. Bạn có thể thử lại sau.'
                      : 'Chuyến đi đã được hủy.',
                  style: const TextStyle(color: muted, fontSize: 13),
                ),
              ),
            if (error != null) ...[
              const SizedBox(height: 10),
              Text(
                error!,
                key: const Key('trip-error'),
                style: const TextStyle(color: Color(0xFFB3261E), fontSize: 12),
              ),
            ],
            const SizedBox(height: 14),
            if (canReview) ...[
              _ActionButton(
                label: 'Đồng ý',
                busy: busy,
                onPressed: onApprove,
              ),
              const SizedBox(height: 8),
              _ActionButton(
                label: 'Từ chối',
                busy: busy,
                outlined: true,
                onPressed: onReject,
              ),
              if (canCancel) ...[
                const SizedBox(height: 8),
                _ActionButton(
                  label: 'Hủy chuyến',
                  busy: busy,
                  outlined: true,
                  onPressed: onCancel,
                ),
              ],
            ] else if (canCancel) ...[
              _ActionButton(
                label: 'Hủy chuyến',
                busy: busy,
                outlined: true,
                onPressed: onCancel,
              ),
            ] else if (trip.status == TripStatus.paid ||
                trip.status == TripStatus.failed ||
                trip.status == TripStatus.cancelled) ...[
              _ActionButton(
                label: trip.status == TripStatus.paid
                    ? 'Xem lịch sử chuyến'
                    : 'Về trang chủ',
                busy: false,
                onPressed: onDismiss,
              ),
            ] else if (trip.status == TripStatus.paymentPending) ...[
              _ActionButton(
                label: 'Cập nhật trạng thái thanh toán',
                busy: busy,
                outlined: true,
                onPressed: onDismiss,
              ),
            ] else
              const Center(
                child: Padding(
                  padding: EdgeInsets.symmetric(vertical: 8),
                  child: Text(
                    'Ứng dụng sẽ tự cập nhật trạng thái chuyến.',
                    style: TextStyle(color: muted, fontSize: 12),
                  ),
                ),
              ),
          ],
        ),
      ),
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
                              _formatDate(trip.createdAt),
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
                                _StatusBadge(status: trip.status),
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
  Widget build(BuildContext context) {
    final proposal = trip.currentProposal;
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              trip.status.label,
              style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 16),
            _LocationRow(
              icon: Icons.trip_origin_rounded,
              title: 'Điểm đón',
              address: trip.pickupAddress,
            ),
            const SizedBox(height: 12),
            _LocationRow(
              icon: Icons.place_rounded,
              title: 'Điểm đến',
              address: trip.destinationAddress,
            ),
            const Divider(height: 28),
            Text(
              'Tài xế: ${proposal?.driverName ?? 'Chưa được phân công'}',
              style: const TextStyle(fontWeight: FontWeight.w600),
            ),
            if (proposal?.vehicleName != null)
              Text('Xe: ${proposal!.vehicleName}'),
            if (proposal?.plateNumber != null)
              Text('Biển số: ${proposal!.plateNumber}'),
            const SizedBox(height: 12),
            Text(
              'Giá chuyến: ${formatVnd(trip.agreedAmount ?? proposal?.amount)}',
              style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w700),
            ),
            if (trip.status == TripStatus.paymentPending)
              _PaymentState(status: trip.paymentStatus),
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

class _DriverAvatar extends StatelessWidget {
  const _DriverAvatar({this.imageKey});
  final String? imageKey;

  @override
  Widget build(BuildContext context) => CircleAvatar(
    radius: 25,
    backgroundColor: const Color(0xFFEFF3F1),
    child: Icon(
      imageKey == null ? Icons.person_rounded : Icons.directions_bike_rounded,
      color: muted,
    ),
  );
}

class _TripMetric extends StatelessWidget {
  const _TripMetric({required this.icon, required this.text});
  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(right: 12),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 15, color: muted),
        const SizedBox(width: 4),
        Text(text, style: const TextStyle(fontSize: 12, color: muted)),
      ],
    ),
  );
}

class _Countdown extends StatelessWidget {
  const _Countdown({required this.seconds});
  final int seconds;

  @override
  Widget build(BuildContext context) => Container(
    key: const Key('trip-countdown'),
    padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 6),
    decoration: BoxDecoration(
      color: const Color(0xFFE8F8EE),
      borderRadius: BorderRadius.circular(24),
    ),
    child: Text(
      '${(seconds ~/ 60).toString().padLeft(2, '0')}:'
      '${(seconds % 60).toString().padLeft(2, '0')}',
      style: const TextStyle(
        color: green,
        fontWeight: FontWeight.w700,
      ),
    ),
  );
}

class _EtaBanner extends StatelessWidget {
  const _EtaBanner({required this.text});
  final String text;

  @override
  Widget build(BuildContext context) => Container(
    width: double.infinity,
    padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 10),
    decoration: BoxDecoration(
      color: const Color(0xFFE8F8EE),
      borderRadius: BorderRadius.circular(12),
    ),
    child: Text(
      text,
      textAlign: TextAlign.center,
      style: const TextStyle(color: green, fontWeight: FontWeight.w700),
    ),
  );
}

class _PaymentState extends StatelessWidget {
  const _PaymentState({required this.status});
  final String? status;

  @override
  Widget build(BuildContext context) {
    final succeeded = status?.toLowerCase() == 'succeeded';
    final failed = status?.toLowerCase() == 'failed';
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Row(
        children: [
          Icon(
            succeeded ? Icons.check_circle : Icons.schedule_rounded,
            color: succeeded ? green : const Color(0xFFD97900),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              succeeded
                  ? 'Thanh toán thành công'
                  : failed
                  ? 'Thanh toán chưa thành công. Hệ thống đang cập nhật.'
                  : 'Đang xử lý thanh toán...',
              style: TextStyle(
                color: succeeded ? green : const Color(0xFFD97900),
                fontWeight: FontWeight.w600,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _ActionButton extends StatelessWidget {
  const _ActionButton({
    required this.label,
    required this.busy,
    required this.onPressed,
    this.outlined = false,
  });
  final String label;
  final bool busy;
  final bool outlined;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) => SizedBox(
    width: double.infinity,
    height: 44,
    child: outlined
        ? OutlinedButton(
            onPressed: busy ? null : onPressed,
            style: OutlinedButton.styleFrom(
              foregroundColor: green,
              side: const BorderSide(color: green),
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(12),
              ),
            ),
            child: busy
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : Text(label),
          )
        : FilledButton(
            onPressed: busy ? null : onPressed,
            style: FilledButton.styleFrom(
              backgroundColor: green,
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(12),
              ),
            ),
            child: busy
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: Colors.white,
                    ),
                  )
                : Text(label),
          ),
  );
}

class _StatusBadge extends StatelessWidget {
  const _StatusBadge({required this.status});
  final TripStatus status;

  @override
  Widget build(BuildContext context) {
    final (label, color, background) = switch (status) {
      TripStatus.paid => ('Đã thanh toán', green, const Color(0xFFE5F8EE)),
      TripStatus.cancelled => (
        'Đã hủy',
        muted,
        const Color(0xFFF0F2F4),
      ),
      TripStatus.failed => (
        'Thất bại',
        const Color(0xFFB3261E),
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
        style: TextStyle(fontSize: 10, color: color, fontWeight: FontWeight.w600),
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
