import 'package:maplibre_gl/maplibre_gl.dart';
import 'route_polyline.dart';

enum TripStatus {
  requested,
  matching,
  awaitingRiderApproval,
  awaitingDriverResponse,
  driverAccepted,
  enRoute,
  pickedUp,
  droppedOff,
  paymentPending,
  paid,
  cancelled,
  failed,
  unknown;

  static TripStatus parse(Object? value) => switch (value) {
    'Requested' => requested,
    'Matching' => matching,
    'AwaitingRiderApproval' => awaitingRiderApproval,
    'AwaitingDriverResponse' => awaitingDriverResponse,
    'DriverAccepted' => driverAccepted,
    'EnRoute' => enRoute,
    'PickedUp' => pickedUp,
    'DroppedOff' => droppedOff,
    'PaymentPending' => paymentPending,
    'Paid' => paid,
    'Cancelled' => cancelled,
    'Failed' => failed,
    _ => unknown,
  };

  bool get isActive => switch (this) {
    paid || cancelled || failed => false,
    _ => true,
  };

  String get label => switch (this) {
    requested || matching => 'Đang tìm tài xế',
    awaitingRiderApproval => 'Xác nhận tài xế và giá',
    awaitingDriverResponse => 'Chờ tài xế xác nhận',
    driverAccepted => 'Tài xế đang đến',
    enRoute => 'Tài xế đang đến',
    pickedUp => 'Đang di chuyển',
    droppedOff || paymentPending => 'Đang xử lý thanh toán',
    paid => 'Đã thanh toán',
    cancelled => 'Đã hủy chuyến',
    failed => 'Không tìm được tài xế',
    unknown => 'Đang cập nhật chuyến',
  };
}

enum ProposalStatus {
  searching,
  awaitingRiderApproval,
  riderRejected,
  riderTimedOut,
  awaitingDriverResponse,
  driverRejected,
  driverTimedOut,
  driverAccepted,
  cancelled,
  noDriverFound,
  unknown;

  static ProposalStatus parse(Object? value) => switch (value) {
    'Searching' => searching,
    'AwaitingRiderApproval' => awaitingRiderApproval,
    'RiderRejected' => riderRejected,
    'RiderTimedOut' => riderTimedOut,
    'AwaitingDriverResponse' => awaitingDriverResponse,
    'DriverRejected' => driverRejected,
    'DriverTimedOut' => driverTimedOut,
    'DriverAccepted' => driverAccepted,
    'Cancelled' => cancelled,
    'NoDriverFound' => noDriverFound,
    _ => unknown,
  };
}

class TripProposalSnapshot {
  const TripProposalSnapshot({
    required this.id,
    required this.status,
    this.driverId,
    this.amount,
    this.currency = 'VND',
    this.distanceMeters,
    this.estimatedDurationSeconds,
    this.riderApprovalDeadline,
    this.driverResponseDeadline,
    this.driverName,
    this.driverAvatarImageKey,
    this.plateNumber,
    this.vehicleName,
    this.vehicleImageKey,
  });

  factory TripProposalSnapshot.fromJson(Map<String, dynamic> json) =>
      TripProposalSnapshot(
        id: _string(json, 'id'),
        status: ProposalStatus.parse(json['status']),
        driverId: _optionalString(json, 'driverId'),
        amount: _optionalNumber(json, 'amount'),
        currency: _optionalString(json, 'currency') ?? 'VND',
        distanceMeters: _optionalNumber(json, 'distanceMeters'),
        estimatedDurationSeconds: _optionalInteger(
          json,
          'estimatedDurationSeconds',
        ),
        riderApprovalDeadline: _date(json['riderApprovalDeadline']),
        driverResponseDeadline: _date(json['driverResponseDeadline']),
        driverName: _optionalString(json, 'driverNameSnapshot'),
        driverAvatarImageKey: _optionalString(
          json,
          'driverAvatarImageKeySnapshot',
        ),
        plateNumber: _optionalString(json, 'plateNumberSnapshot'),
        vehicleName: _optionalString(json, 'vehicleNameSnapshot'),
        vehicleImageKey: _optionalString(json, 'vehicleImageKeySnapshot'),
      );

  final String id;
  final ProposalStatus status;
  final String? driverId;
  final double? amount;
  final String currency;
  final double? distanceMeters;
  final int? estimatedDurationSeconds;
  final DateTime? riderApprovalDeadline;
  final DateTime? driverResponseDeadline;
  final String? driverName;
  final String? driverAvatarImageKey;
  final String? plateNumber;
  final String? vehicleName;
  final String? vehicleImageKey;

  DateTime? get responseDeadline => switch (status) {
    ProposalStatus.awaitingRiderApproval => riderApprovalDeadline,
    ProposalStatus.awaitingDriverResponse => driverResponseDeadline,
    _ => null,
  };
}

class TripSnapshot {
  const TripSnapshot({
    required this.id,
    required this.status,
    required this.stateVersion,
    required this.pickupAddress,
    required this.pickupLatitude,
    required this.pickupLongitude,
    required this.destinationAddress,
    required this.destinationLatitude,
    required this.destinationLongitude,
    required this.vehicleType,
    required this.createdAt,
    required this.updatedAt,
    required this.proposals,
    this.driverId,
    this.riderName,
    this.riderPhone,
    this.agreedAmount,
    this.currency = 'VND',
    this.paymentMethod = 'Wallet',
    this.paymentId,
    this.pickedUpAt,
    this.droppedOffAt,
    this.paymentStatus,
    this.failureReason,
  });

  factory TripSnapshot.fromJson(Map<String, dynamic> json) {
    final proposals = json['proposals'];
    if (proposals is! List) {
      throw const FormatException('Trip proposals are missing.');
    }
    return TripSnapshot(
      id: _string(json, 'id'),
      status: TripStatus.parse(json['status']),
      stateVersion: _integer(json, 'stateVersion'),
      pickupAddress: _string(json, 'pickupAddress'),
      pickupLatitude: _number(json, 'pickupLatitude'),
      pickupLongitude: _number(json, 'pickupLongitude'),
      destinationAddress: _string(json, 'destinationAddress'),
      destinationLatitude: _number(json, 'destinationLatitude'),
      destinationLongitude: _number(json, 'destinationLongitude'),
      vehicleType: _optionalString(json, 'vehicleType') ?? 'Motorbike',
      createdAt: _requiredDate(json, 'createdAt'),
      updatedAt: _requiredDate(json, 'updatedAt'),
      proposals: proposals
          .map((item) {
            if (item is! Map<String, dynamic>) {
              throw const FormatException('Invalid trip proposal.');
            }
            return TripProposalSnapshot.fromJson(item);
          })
          .toList(growable: false),
      driverId: _optionalString(json, 'driverId'),
      riderName: _optionalString(json, 'riderNameSnapshot'),
      riderPhone: _optionalString(json, 'riderPhoneSnapshot'),
      agreedAmount: _optionalNumber(json, 'agreedAmount'),
      currency: _optionalString(json, 'currency') ?? 'VND',
      paymentMethod: _optionalString(json, 'paymentMethod') ?? 'Wallet',
      paymentId: _optionalString(json, 'paymentId'),
      pickedUpAt: _date(json['pickedUpAt']),
      droppedOffAt: _date(json['droppedOffAt']),
      paymentStatus: _optionalString(json, 'paymentStatus'),
      failureReason: _optionalString(json, 'failureReason'),
    );
  }

  final String id;
  final TripStatus status;
  final int stateVersion;
  final String pickupAddress;
  final double pickupLatitude;
  final double pickupLongitude;
  final String destinationAddress;
  final double destinationLatitude;
  final double destinationLongitude;
  final String vehicleType;
  final DateTime createdAt;
  final DateTime updatedAt;
  final List<TripProposalSnapshot> proposals;
  final String? driverId;
  final String? riderName;
  final String? riderPhone;
  final double? agreedAmount;
  final String currency;
  final String paymentMethod;
  bool get isCash => paymentMethod == 'Cash';
  String get paymentMethodLabel => isCash ? 'Tiền mặt' : 'Ví SwiftRide';
  final String? paymentId;
  final DateTime? pickedUpAt;
  final DateTime? droppedOffAt;
  final String? paymentStatus;
  final String? failureReason;

  TripProposalSnapshot? get currentProposal {
    const active = {
      ProposalStatus.searching,
      ProposalStatus.awaitingRiderApproval,
      ProposalStatus.awaitingDriverResponse,
      ProposalStatus.driverAccepted,
    };
    for (final proposal in proposals.reversed) {
      if (active.contains(proposal.status)) return proposal;
    }
    return null;
  }
}

class TripRouteLeg {
  const TripRouteLeg({
    required this.polyline,
    required this.distanceMeters,
    required this.durationSeconds,
    this.calculatedAt,
  });

  factory TripRouteLeg.fromJson(Map<String, dynamic> json) => TripRouteLeg(
    polyline: _string(json, 'polyline'),
    distanceMeters: _number(json, 'distanceMeters'),
    durationSeconds: _integer(json, 'durationSeconds'),
    calculatedAt: _date(json['calculatedAt']),
  );

  final String polyline;
  final double distanceMeters;
  final int durationSeconds;
  final DateTime? calculatedAt;

  List<LatLng> decode() => decodeRoutePolyline(
    polyline,
  ).map((p) => LatLng(p.$1, p.$2)).toList(growable: false);
}

class TripRouteSnapshot {
  const TripRouteSnapshot({
    required this.tripId,
    required this.proposalId,
    this.driverId,
    this.stage,
    this.stageVersion,
    this.toPickup,
    this.toDestination,
    this.pickupEtaMinutes,
  });

  factory TripRouteSnapshot.fromJson(Map<String, dynamic> json) =>
      TripRouteSnapshot(
        tripId: _string(json, 'tripId'),
        proposalId: _string(json, 'proposalId'),
        driverId: _optionalString(json, 'driverId'),
        stage: switch (json['stage']) {
          0 || 'ToPickup' => 'ToPickup',
          1 || 'ToDestination' => 'ToDestination',
          null => null,
          _ => throw const FormatException('Invalid route stage.'),
        },
        stageVersion: _optionalInteger(json, 'stageVersion'),
        toPickup: _optionalLeg(json['toPickup']),
        toDestination: _optionalLeg(json['toDestination']),
        pickupEtaMinutes: _optionalInteger(json, 'pickupEtaMinutes'),
      );

  final TripRouteLeg? toPickup;
  final String tripId;
  final String proposalId;
  final String? driverId;
  final String? stage;
  final int? stageVersion;
  final TripRouteLeg? toDestination;
  final int? pickupEtaMinutes;

  bool matches(TripSnapshot trip) {
    if (tripId != trip.id || proposalId != trip.currentProposal?.id) {
      return false;
    }
    return switch (trip.status) {
      TripStatus.matching ||
      TripStatus.awaitingRiderApproval ||
      TripStatus.awaitingDriverResponse => stage == null,
      TripStatus.driverAccepted || TripStatus.enRoute =>
        stage == 'ToPickup' && stageVersion == 1 && driverId == trip.driverId,
      TripStatus.pickedUp =>
        stage == 'ToDestination' &&
            stageVersion == 2 &&
            driverId == trip.driverId,
      _ => false,
    };
  }

  DateTime? forTripTimestamp(TripSnapshot trip) => forTrip(trip)?.calculatedAt;

  TripRouteLeg? forTrip(TripSnapshot trip) =>
      trip.status == TripStatus.enRoute ||
          trip.status == TripStatus.driverAccepted
      ? toPickup
      : toDestination;
}

TripRouteLeg? _optionalLeg(Object? value) {
  if (value == null) return null;
  if (value is! Map<String, dynamic>) {
    throw const FormatException('Invalid trip route leg.');
  }
  return TripRouteLeg.fromJson(value);
}

String _string(Map<String, dynamic> json, String key) {
  final value = json[key];
  if (value is! String || value.isEmpty) {
    throw FormatException('Missing trip field: $key.');
  }
  return value;
}

double _number(Map<String, dynamic> json, String key) {
  final value = json[key];
  if (value is! num || !value.toDouble().isFinite) {
    throw FormatException('Invalid trip field: $key.');
  }
  return value.toDouble();
}

int _integer(Map<String, dynamic> json, String key) {
  final value = json[key];
  if (value is! int) throw FormatException('Invalid trip field: $key.');
  return value;
}

String? _optionalString(Map<String, dynamic> json, String key) {
  final value = json[key];
  if (value == null) return null;
  if (value is! String) throw FormatException('Invalid trip field: $key.');
  return value;
}

double? _optionalNumber(Map<String, dynamic> json, String key) {
  final value = json[key];
  if (value == null) return null;
  if (value is! num || !value.toDouble().isFinite) {
    throw FormatException('Invalid trip field: $key.');
  }
  return value.toDouble();
}

int? _optionalInteger(Map<String, dynamic> json, String key) {
  final value = json[key];
  if (value == null) return null;
  if (value is! int) throw FormatException('Invalid trip field: $key.');
  return value;
}

DateTime? _date(Object? value) =>
    value is String ? DateTime.tryParse(value)?.toLocal() : null;

DateTime _requiredDate(Map<String, dynamic> json, String key) {
  final value = _date(json[key]);
  if (value == null) throw FormatException('Invalid trip field: $key.');
  return value;
}

class TripProgress {
  TripProgress(
    this.tripId,
    this.proposalId,
    this.driverId,
    this.stageVersion,
    this.recordedAt,
    this.distance,
    this.seconds,
  );
  final String tripId, proposalId, driverId;
  final int stageVersion;
  final DateTime recordedAt;
  final double? distance;
  final int? seconds;

  static TripProgress? parse(
    Map<String, dynamic> data,
    TripSnapshot trip,
    TripProgress? old,
  ) {
    final version = data['stageVersion'];
    final date = DateTime.tryParse('${data['recordedAt']}');
    final expected = trip.status == TripStatus.pickedUp
        ? 2
        : (trip.status == TripStatus.enRoute ||
                  trip.status == TripStatus.driverAccepted
              ? 1
              : 0);
    if (data['tripId'] != trip.id ||
        data['proposalId'] != trip.currentProposal?.id ||
        data['driverId'] != trip.driverId ||
        version != expected ||
        date == null ||
        (old != null &&
            old.tripId == trip.id &&
            old.stageVersion == version &&
            !date.isAfter(old.recordedAt))) {
      return null;
    }
    final meters = data['remainingMeters'];
    final seconds = data['remainingSeconds'];
    return TripProgress(
      trip.id,
      data['proposalId'] as String,
      data['driverId'] as String,
      version as int,
      date,
      meters is num && meters >= 0 ? meters.toDouble() : null,
      seconds is num && seconds >= 0 ? seconds.toInt() : null,
    );
  }

  String get label => distance == null || seconds == null
      ? 'Đang cập nhật tuyến đường'
      : 'Còn khoảng ${(distance! / 1000).toStringAsFixed(1)} km · ${(seconds! / 60).ceil()} phút';
}
