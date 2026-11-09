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
    if (approaching || moving) {
      return _ActiveRiderTrip(
        trip: trip,
        progress: progress,
        route: route,
        approaching: approaching,
        busy: busy,
        error: error,
        onCancel: onCancel,
      );
    }
    return TripSheet(
      title: review ? 'Xác nhận tài xế và chuyến đi' : s.label,
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
        if (p != null && p.status != ProposalStatus.searching && !moving) ...[
          TripPersonCard(
            name: p.driverName ?? 'Tài xế SwiftRide',
            subtitle: p.vehicleName,
            plate: p.plateNumber,
            avatar: p.driverAvatarImageKey,
            vehicleType: trip.vehicleType,
          ),
          const SizedBox(height: 12),
        ],
        if (searching)
          TripJourneyCard(trip: trip, compact: true)
        else if (review)
          TripJourneyCard(trip: trip, fullAddress: true)
        else if (waiting)
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
        else if (approaching)
          TripStopCard(
            label: 'Điểm đón',
            address: trip.pickupAddress,
            icon: Icons.trip_origin_rounded,
          )
        else if (moving)
          TripStopCard(label: 'Điểm đến', address: trip.destinationAddress),
        if (p?.amount != null) ...[
          const SizedBox(height: 12),
          if (approaching || moving)
            ExpansionTile(
              tilePadding: EdgeInsets.zero,
              leading: const Icon(Icons.receipt_long_rounded, color: green),
              title: const Text('Chi tiết chuyến đi'),
              children: [
                if (moving && p != null) ...[
                  TripPersonCard(
                    name: p.driverName ?? 'Tài xế SwiftRide',
                    subtitle: p.vehicleName,
                    plate: p.plateNumber,
                    avatar: p.driverAvatarImageKey,
                    vehicleType: trip.vehicleType,
                  ),
                  const SizedBox(height: 8),
                ],
                TripFareSummary(trip: trip),
              ],
            ),
          if (!approaching && !moving) ...[
            if (review) _ReviewMetrics(trip: trip) else TripMetrics(trip: trip),
            const SizedBox(height: 8),
            if (review)
              TripCard(
                padding: 12,
                child: Row(
                  children: [
                    Icon(
                      trip.isCash
                          ? Icons.payments_rounded
                          : Icons.account_balance_wallet_rounded,
                      color: green,
                    ),
                    const SizedBox(width: 12),
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        const Text(
                          'Thanh toán',
                          style: TextStyle(color: muted, fontSize: 11),
                        ),
                        Text(
                          trip.paymentMethodLabel,
                          style: const TextStyle(
                            color: ink,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                      ],
                    ),
                  ],
                ),
              )
            else
              TripPaymentMethodLabel(trip: trip),
          ],
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
                ? _failureMessage(trip)
                : 'Chuyến đi đã được hủy.',
            style: const TextStyle(color: muted, fontSize: 13),
          ),
          const SizedBox(height: 12),
          TripAction(label: 'Về trang chủ', onTap: onDismiss),
        ],
        if (review) ...[
          const SizedBox(height: 16),
          TripAction(
            label: 'Đồng ý · ${moneyVnd(p?.amount)}',
            onTap: onApprove,
            busy: busy,
          ),
          const SizedBox(height: 8),
          TripAction(
            label: 'Từ chối và tìm tài xế khác',
            onTap: onReject,
            busy: busy,
            secondary: true,
          ),
          const Padding(
            padding: EdgeInsets.only(top: 6),
            child: Text(
              'Hệ thống sẽ tìm tài xế khác nếu còn lượt đề xuất.',
              textAlign: TextAlign.center,
              style: TextStyle(color: muted, fontSize: 11),
            ),
          ),
        ],
        if (cancel) ...[
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

class _ActiveRiderTrip extends StatelessWidget {
  const _ActiveRiderTrip({
    required this.trip,
    required this.approaching,
    required this.busy,
    required this.onCancel,
    this.progress,
    this.route,
    this.error,
  });

  final TripSnapshot trip;
  final TripProgress? progress;
  final TripRouteSnapshot? route;
  final bool approaching, busy;
  final String? error;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    final proposal = trip.currentProposal;
    final eta =
        progress?.label ??
        (approaching
            ? (route?.pickupEtaMinutes != null
                  ? 'Khoảng ${route!.pickupEtaMinutes} phút tới điểm đón'
                  : 'Đang cập nhật thời gian đến')
            : (route?.toDestination != null
                  ? 'Khoảng ${(route!.toDestination!.durationSeconds / 60).ceil()} phút tới điểm đến'
                  : 'Đang cập nhật thời gian đến'));
    return TripSheet(
      title: approaching ? 'Tài xế đang đến' : 'Đang trên đường',
      children: [
        Row(
          children: [
            const Icon(Icons.schedule_rounded, color: green, size: 19),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                eta,
                style: const TextStyle(
                  color: green,
                  fontSize: 14,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 16),
        if (proposal != null) ...[
          Row(
            children: [
              const CircleAvatar(
                radius: 22,
                backgroundColor: tripMint,
                child: Icon(Icons.person_rounded, color: green),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      proposal.driverName ?? 'Tài xế SwiftRide',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        color: ink,
                        fontWeight: FontWeight.w700,
                        fontSize: 15,
                      ),
                    ),
                    if (proposal.vehicleName?.isNotEmpty == true)
                      Text(
                        proposal.vehicleName!,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(color: muted, fontSize: 12),
                      ),
                  ],
                ),
              ),
              if (proposal.plateNumber?.isNotEmpty == true)
                Flexible(
                  child: Text(
                    proposal.plateNumber!,
                    textAlign: TextAlign.end,
                    style: const TextStyle(
                      color: ink,
                      fontWeight: FontWeight.w800,
                      fontSize: 15,
                    ),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 14),
          const Divider(height: 1, color: tripBorder),
          const SizedBox(height: 14),
        ],
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(
              approaching
                  ? Icons.trip_origin_rounded
                  : Icons.location_on_rounded,
              color: green,
              size: 21,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    approaching ? 'Điểm đón' : 'Điểm đến',
                    style: const TextStyle(color: muted, fontSize: 12),
                  ),
                  const SizedBox(height: 3),
                  Text(
                    approaching ? trip.pickupAddress : trip.destinationAddress,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      color: ink,
                      fontWeight: FontWeight.w600,
                      fontSize: 13,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
        const SizedBox(height: 14),
        const Divider(height: 1, color: tripBorder),
        const SizedBox(height: 6),
        Row(
          children: [
            const Icon(Icons.receipt_long_outlined, color: muted, size: 19),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                moneyVnd(trip.agreedAmount ?? proposal?.amount),
                style: const TextStyle(
                  color: ink,
                  fontWeight: FontWeight.w700,
                  fontSize: 14,
                ),
              ),
            ),
            TripPaymentMethodLabel(trip: trip),
          ],
        ),
        if (error != null) ...[
          const SizedBox(height: 8),
          Text(
            error!,
            key: const Key('trip-error'),
            style: const TextStyle(color: Color(0xFFB42318), fontSize: 12),
          ),
        ],
        if (approaching) ...[
          const SizedBox(height: 12),
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

String _failureMessage(TripSnapshot trip) {
  final rejected = trip.proposals.any(
    (p) => p.status == ProposalStatus.riderRejected,
  );
  return switch (trip.failureReason) {
    'driver_attempt_limit_reached' =>
      'Đã hết lượt đề xuất tài xế. Bạn có thể đặt chuyến mới.',
    'no_driver_available' when rejected =>
      'Đã từ chối đề xuất trước. Hiện chưa tìm được tài xế khác; bạn có thể thử đặt lại.',
    'no_driver_available' =>
      'Chưa tìm được tài xế phù hợp. Bạn có thể thử đặt lại.',
    _ =>
      'Không thể tiếp tục tìm tài xế do lỗi hệ thống. Bạn có thể thử đặt lại.',
  };
}

class _ReviewMetrics extends StatelessWidget {
  const _ReviewMetrics({required this.trip});
  final TripSnapshot trip;
  @override
  Widget build(BuildContext context) {
    final p = trip.currentProposal;
    return TripCard(
      color: tripMint,
      padding: 12,
      child: Row(
        children: [
          const Icon(Icons.route_rounded, color: green),
          const SizedBox(width: 8),
          Expanded(
            child: _metric(
              'Quãng đường dự kiến',
              p?.distanceMeters == null
                  ? 'Đang cập nhật'
                  : '${(p!.distanceMeters! / 1000).toStringAsFixed(1)} km',
            ),
          ),
          const SizedBox(height: 36, child: VerticalDivider(width: 20)),
          const Icon(Icons.schedule_rounded, color: green),
          const SizedBox(width: 8),
          Expanded(
            child: _metric(
              'Thời gian dự kiến',
              p?.estimatedDurationSeconds == null
                  ? 'Đang cập nhật'
                  : '${(p!.estimatedDurationSeconds! / 60).ceil()} phút',
            ),
          ),
        ],
      ),
    );
  }

  Widget _metric(String label, String value) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(label, style: const TextStyle(color: muted, fontSize: 11)),
      const SizedBox(height: 3),
      Text(
        value,
        style: const TextStyle(
          color: ink,
          fontWeight: FontWeight.w800,
          fontSize: 14,
        ),
      ),
    ],
  );
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
                separatorBuilder: (_, _) => const SizedBox(height: 12),
                itemBuilder: (context, index) {
                  final trip = trips[index];
                  return TripHistoryCard(
                    trip: trip,
                    onTap: () => onSelected(trip),
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
