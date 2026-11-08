class TripProposal {
  const TripProposal({
    required this.id,
    this.driverId,
    this.amount,
    this.currency = 'VND',
    this.distanceMeters,
    this.estimatedDurationSeconds,
    this.status = '',
    this.riderApprovalDeadline,
    this.driverResponseDeadline,
    this.driverName,
    this.driverAvatarKey,
    this.plateNumber,
    this.vehicleName,
    this.vehicleImageKey,
  });

  factory TripProposal.fromJson(Map<String, dynamic> json) => TripProposal(
    id: '${json['id'] ?? ''}',
    driverId: json['driverId']?.toString(),
    amount: (json['amount'] as num?)?.toDouble(),
    currency: json['currency'] as String? ?? 'VND',
    distanceMeters: (json['distanceMeters'] as num?)?.toDouble(),
    estimatedDurationSeconds: json['estimatedDurationSeconds'] as int?,
    status: json['status'] as String? ?? '',
    riderApprovalDeadline: _date(json['riderApprovalDeadline']),
    driverResponseDeadline: _date(json['driverResponseDeadline']),
    driverName: json['driverNameSnapshot'] as String?,
    driverAvatarKey: json['driverAvatarImageKeySnapshot'] as String?,
    plateNumber: json['plateNumberSnapshot'] as String?,
    vehicleName: json['vehicleNameSnapshot'] as String?,
    vehicleImageKey: json['vehicleImageKeySnapshot'] as String?,
  );

  final String id;
  final String? driverId;
  final double? amount;
  final String currency;
  final double? distanceMeters;
  final int? estimatedDurationSeconds;
  final String status;
  final DateTime? riderApprovalDeadline;
  final DateTime? driverResponseDeadline;
  final String? driverName;
  final String? driverAvatarKey;
  final String? plateNumber;
  final String? vehicleName;
  final String? vehicleImageKey;
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
    required this.createdAt,
    this.riderName,
    this.riderPhone,
    this.driverName,
    this.driverAvatarKey,
    this.plateNumber,
    this.vehicleName,
    this.vehicleImageKey,
    this.vehicleType = 'Motorbike',
    this.agreedAmount,
    this.currency = 'VND',
    this.paymentStatus,
    this.failureReason,
    this.proposals = const [],
  });

  factory TripSnapshot.fromJson(Map<String, dynamic> json) => TripSnapshot(
    id: '${json['id'] ?? ''}',
    status: json['status'] as String? ?? 'Matching',
    stateVersion: json['stateVersion'] as int? ?? 0,
    pickupAddress: json['pickupAddress'] as String? ?? '',
    pickupLatitude: (json['pickupLatitude'] as num?)?.toDouble() ?? 0,
    pickupLongitude: (json['pickupLongitude'] as num?)?.toDouble() ?? 0,
    destinationAddress: json['destinationAddress'] as String? ?? '',
    destinationLatitude: (json['destinationLatitude'] as num?)?.toDouble() ?? 0,
    destinationLongitude: (json['destinationLongitude'] as num?)?.toDouble() ?? 0,
    createdAt: _date(json['createdAt']) ?? DateTime.now(),
    riderName: json['riderNameSnapshot'] as String?,
    riderPhone: json['riderPhoneSnapshot'] as String?,
    driverName: json['driverNameSnapshot'] as String?,
    driverAvatarKey: json['driverAvatarImageKeySnapshot'] as String?,
    plateNumber: json['plateNumberSnapshot'] as String?,
    vehicleName: json['vehicleNameSnapshot'] as String?,
    vehicleImageKey: json['vehicleImageKeySnapshot'] as String?,
    vehicleType: json['vehicleType'] as String? ?? 'Motorbike',
    agreedAmount: (json['agreedAmount'] as num?)?.toDouble(),
    currency: json['currency'] as String? ?? 'VND',
    paymentStatus: json['paymentStatus'] as String?,
    failureReason: json['failureReason'] as String?,
    proposals: (json['proposals'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(TripProposal.fromJson)
        .toList(),
  );

  final String id, status, pickupAddress, destinationAddress, currency, vehicleType;
  final int stateVersion;
  final double pickupLatitude, pickupLongitude, destinationLatitude, destinationLongitude;
  final DateTime createdAt;
  final String? riderName, riderPhone, driverName, driverAvatarKey, plateNumber;
  final String? vehicleName, vehicleImageKey, paymentStatus, failureReason;
  final double? agreedAmount;
  final List<TripProposal> proposals;

  TripProposal? get activeProposal {
    for (final proposal in proposals.reversed) {
      if (proposal.status == 'AwaitingRiderApproval' ||
          proposal.status == 'AwaitingDriverResponse' ||
          proposal.status == 'DriverAccepted') {
        return proposal;
      }
    }
    return proposals.isEmpty ? null : proposals.last;
  }
}

class TripRoute {
  const TripRoute({this.polyline, this.distanceMeters, this.durationSeconds, this.pickupEtaMinutes});
  factory TripRoute.fromJson(Map<String, dynamic> json) => TripRoute(
    polyline: json['toPickup']?['polyline'] as String? ?? json['toDestination']?['polyline'] as String?,
    distanceMeters: (json['toDestination']?['distanceMeters'] as num?)?.toDouble(),
    durationSeconds: json['toDestination']?['durationSeconds'] as int?,
    pickupEtaMinutes: json['pickupEtaMinutes'] as int?,
  );
  final String? polyline;
  final double? distanceMeters;
  final int? durationSeconds, pickupEtaMinutes;
}

DateTime? _date(Object? value) => value is String ? DateTime.tryParse(value)?.toLocal() : null;
