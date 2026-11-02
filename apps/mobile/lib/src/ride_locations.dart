import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:maplibre_gl/maplibre_gl.dart';

import 'app.dart' show ink, muted;

const _selectionGreen = Color(0xFF00B53E);
const _bookingAccent = Color(0xFF00A83B);

class RidePlace {
  const RidePlace(this.name, this.address, [this.point]);
  final String name;
  final String address;
  final LatLng? point;
}

const mockRecentDestinations = [
  RidePlace(
    'Landmark 81',
    '720A Điện Biên Phủ, Bình Thạnh, TP. HCM',
    LatLng(10.7952, 106.7218),
  ),
  RidePlace(
    'Sân bay Tân Sơn Nhất',
    'Đường Trường Sơn, Tân Bình, TP. HCM',
    LatLng(10.8134, 106.6627),
  ),
  RidePlace(
    'Crescent Mall',
    '101 Tôn Dật Tiên, Quận 7, TP. HCM',
    LatLng(10.7286, 106.7186),
  ),
];

const suggestedDestinations = [
  RidePlace(
    'Chợ Bến Thành',
    'Đường Lê Lợi, Quận 1, TP. HCM',
    LatLng(10.7725, 106.6980),
  ),
  RidePlace(
    'Nhà thờ Đức Bà',
    'Công xã Paris, Quận 1, TP. HCM',
    LatLng(10.7798, 106.6990),
  ),
  RidePlace(
    'Phố đi bộ Nguyễn Huệ',
    'Đường Nguyễn Huệ, Quận 1, TP. HCM',
    LatLng(10.7743, 106.7031),
  ),
];

const defaultPickup = RidePlace('', '');

class RideLocations {
  const RideLocations(
    this.pickup,
    this.destination,
    this.vehicleType,
    this.paymentMethod,
  );
  final RidePlace pickup;
  final RidePlace destination;
  final RideVehicleType vehicleType;
  final RidePaymentMethod paymentMethod;
}

enum RideVehicleType {
  motorcycle('Xe máy', Icons.two_wheeler_rounded),
  car4Seats('4 chỗ', Icons.directions_car_rounded),
  car7Seats('7 chỗ', Icons.airport_shuttle_rounded);

  const RideVehicleType(this.label, this.icon);
  final String label;
  final IconData icon;
}

enum RidePaymentMethod {
  cash('Tiền mặt', Icons.payments_rounded),
  swiftRideWallet('Ví SwiftRide', Icons.account_balance_wallet_rounded);

  const RidePaymentMethod(this.label, this.icon);
  final String label;
  final IconData icon;
}

class RideLocationPicker extends StatefulWidget {
  const RideLocationPicker({
    super.key,
    required this.pickup,
    this.destination,
    required this.recentDestinations,
    required this.vehicleType,
    required this.paymentMethod,
    this.onLocate,
  });
  final RidePlace pickup;
  final RidePlace? destination;
  final List<RidePlace> recentDestinations;
  final RideVehicleType vehicleType;
  final RidePaymentMethod paymentMethod;
  final Future<RidePlace?> Function()? onLocate;

  @override
  State<RideLocationPicker> createState() => _RideLocationPickerState();
}

class _RideLocationPickerState extends State<RideLocationPicker> {
  late final _pickupText = TextEditingController(text: widget.pickup.name);
  late final _destinationText = TextEditingController(
    text: widget.destination?.name ?? '',
  );
  final _form = GlobalKey<FormState>();
  final _pickupFocus = FocusNode();
  final _destinationFocus = FocusNode();
  String _query = '';
  bool _locating = false;
  late RidePlace _pickup = widget.pickup;
  late RidePlace? _destination = widget.destination;
  late RideVehicleType _vehicleType = widget.vehicleType;
  late RidePaymentMethod _paymentMethod = widget.paymentMethod;

  @override
  void dispose() {
    _pickupText.dispose();
    _destinationText.dispose();
    _pickupFocus.dispose();
    _destinationFocus.dispose();
    super.dispose();
  }

  void _select(RidePlace place) {
    setState(() {
      _destination = place;
      _destinationText.text = place.name;
      FocusScope.of(context).unfocus();
      _query = '';
    });
  }

  Future<void> _useCurrentLocation() async {
    if (_locating || widget.onLocate == null) return;
    setState(() => _locating = true);
    try {
      final place = await widget.onLocate!();
      if (place == null || !mounted) return;
      setState(() {
        _pickup = place;
        _pickupText.text = place.name;
      });
    } finally {
      if (mounted) setState(() => _locating = false);
    }
  }

  void _apply() {
    if (!_form.currentState!.validate()) return;
    RidePlace value(TextEditingController text, RidePlace? selected) =>
        selected?.name == text.text.trim()
        ? selected!
        : RidePlace(text.text.trim(), '');
    Navigator.pop(
      context,
      RideLocations(
        value(_pickupText, _pickup),
        value(_destinationText, _destination),
        _vehicleType,
        _paymentMethod,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final recent = widget.recentDestinations;
    final defaults = recent.isEmpty ? suggestedDestinations : recent;
    final choices = <RidePlace>[...recent, ...suggestedDestinations];
    final visible = _query.isEmpty
        ? defaults
        : choices
              .where(
                (place) => '${place.name} ${place.address}'
                    .toLowerCase()
                    .contains(_query.toLowerCase()),
              )
              .toList();
    return ScrollConfiguration(
      behavior: ScrollConfiguration.of(context).copyWith(scrollbars: false),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Row(
                  children: [
                    const Expanded(
                      child: Text(
                        'Bạn muốn đi đâu?',
                        style: TextStyle(
                          fontSize: 23,
                          fontWeight: FontWeight.w700,
                          color: ink,
                        ),
                      ),
                    ),
                    IconButton.filledTonal(
                      tooltip: 'Đóng',
                      style: IconButton.styleFrom(
                        backgroundColor: const Color(0xFFEEF2F7),
                        foregroundColor: ink,
                      ),
                      onPressed: () => Navigator.pop(context),
                      icon: const Icon(Icons.close_rounded, size: 24),
                    ),
                  ],
                ),
              ),
              Expanded(
                child: SingleChildScrollView(
                  key: const Key('booking-scroll'),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const SizedBox(height: 8),
                      Form(
                        key: _form,
                        child: Column(
                          children: [
                            _field(
                              'Điểm đón',
                              const Key('pickup-field'),
                              _pickupText,
                              _pickupFocus,
                              true,
                              Icons.my_location_rounded,
                            ),
                            const SizedBox(
                              height: 22,
                              child: Align(
                                alignment: Alignment.centerLeft,
                                child: Padding(
                                  padding: EdgeInsets.only(left: 17),
                                  child: SizedBox(
                                    width: 16,
                                    height: 18,
                                    child: CustomPaint(
                                      painter: _RouteArrowPainter(),
                                    ),
                                  ),
                                ),
                              ),
                            ),
                            _field(
                              'Điểm đến',
                              const Key('destination-field'),
                              _destinationText,
                              _destinationFocus,
                              false,
                              Icons.location_on_rounded,
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(height: 20),
                      Text(
                        _query.isNotEmpty
                            ? 'Kết quả tìm kiếm'
                            : recent.isEmpty
                            ? 'Điểm đến đề xuất'
                            : 'Điểm đến gần đây',
                        style: const TextStyle(
                          fontSize: 17,
                          fontWeight: FontWeight.w700,
                          color: ink,
                        ),
                      ),
                      const SizedBox(height: 12),
                      if (visible.isEmpty)
                        const Padding(
                          padding: EdgeInsets.symmetric(vertical: 24),
                          child: Text(
                            'Không có địa điểm phù hợp.\nBạn có thể nhập địa chỉ và chọn Áp dụng.',
                            style: TextStyle(color: muted, height: 1.6),
                          ),
                        )
                      else
                        for (var i = 0; i < visible.length; i++)
                          _placeTile(visible[i]),
                      const SizedBox(height: 18),
                      _sectionTitle(Icons.directions_car_outlined, 'Loại xe'),
                      const SizedBox(height: 12),
                      Row(
                        children: [
                          for (final option in RideVehicleType.values) ...[
                            if (option.index > 0) const SizedBox(width: 9),
                            Expanded(child: _vehicleCard(option)),
                          ],
                        ],
                      ),
                      const SizedBox(height: 22),
                      _sectionTitle(
                        Icons.credit_card_rounded,
                        'Phương thức thanh toán',
                      ),
                      const SizedBox(height: 12),
                      Row(
                        children: [
                          for (final option in RidePaymentMethod.values) ...[
                            if (option.index > 0) const SizedBox(width: 10),
                            Expanded(child: _paymentCard(option)),
                          ],
                        ],
                      ),
                      const SizedBox(height: 24),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              SizedBox(
                width: double.infinity,
                height: 48,
                child: FilledButton(
                  style: FilledButton.styleFrom(
                    backgroundColor: _bookingAccent,
                    foregroundColor: Colors.white,
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(18),
                    ),
                    textStyle: const TextStyle(
                      fontFamily: 'BeVietnamPro',
                      fontSize: 18,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  onPressed: _apply,
                  child: const Text('Áp dụng'),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _sectionTitle(IconData icon, String title) => Row(
    children: [
      Icon(icon, color: ink, size: 24),
      const SizedBox(width: 9),
      Expanded(
        child: Text(
          title,
          style: const TextStyle(
            fontSize: 16,
            fontWeight: FontWeight.w700,
            color: ink,
          ),
        ),
      ),
    ],
  );

  Widget _placeTile(RidePlace place) => Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Material(
      color: Colors.white,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(17),
        side: BorderSide(color: const Color(0xFFECEEF2), width: 1),
      ),
      clipBehavior: Clip.antiAlias,
      child: ListTile(
        dense: true,
        visualDensity: const VisualDensity(vertical: -2),
        minVerticalPadding: 8,
        contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 1),
        minLeadingWidth: 26,
        horizontalTitleGap: 12,
        leading: Container(
          width: 34,
          height: 34,
          decoration: const BoxDecoration(
            color: Color(0xFFF1FAF4),
            shape: BoxShape.circle,
          ),
          child: const Icon(
            Icons.location_on_rounded,
            color: _selectionGreen,
            size: 28,
          ),
        ),
        title: Text(
          place.name,
          style: const TextStyle(
            color: ink,
            fontSize: 14,
            fontWeight: FontWeight.w700,
          ),
        ),
        subtitle: Text(
          place.address,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(fontSize: 12, color: muted),
        ),
        onTap: () => _select(place),
      ),
    ),
  );

  Widget _optionCard({
    required String label,
    required bool selected,
    required VoidCallback onTap,
    required Widget child,
  }) => Semantics(
    button: true,
    selected: selected,
    label: label,
    child: Material(
      color: selected ? const Color(0xFFEDFCF1) : Colors.white,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(
          color: selected ? _selectionGreen : const Color(0xFFE9ECF1),
          width: 1.3,
        ),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(onTap: onTap, child: child),
    ),
  );

  Widget _vehicleCard(RideVehicleType option) {
    final selected = _vehicleType == option;
    return _optionCard(
      label: option.label,
      selected: selected,
      onTap: () => setState(() => _vehicleType = option),
      child: SizedBox(
        height: 92,
        child: Stack(
          children: [
            Positioned.fill(
              child: Column(
                children: [
                  SizedBox(
                    height: 63,
                    width: 94,
                    child: _BookingArt(index: option.index + 1),
                  ),
                  Text(
                    option.label,
                    style: const TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w700,
                      color: ink,
                    ),
                  ),
                ],
              ),
            ),
            if (selected)
              const Positioned(
                top: 5,
                right: 5,
                child: _SelectedMark(size: 19),
              ),
          ],
        ),
      ),
    );
  }

  Widget _paymentCard(RidePaymentMethod option) {
    final selected = _paymentMethod == option;
    final cash = option == RidePaymentMethod.cash;
    return _optionCard(
      label: option.label,
      selected: selected,
      onTap: () => setState(() => _paymentMethod = option),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 13),
        child: Row(
          children: [
            Container(
              width: 26,
              height: 26,
              decoration: BoxDecoration(
                color: cash ? const Color(0xFFDDF5CA) : _bookingAccent,
                borderRadius: BorderRadius.circular(7),
              ),
              child: Icon(
                option.icon,
                size: 21,
                color: cash ? const Color(0xFF158C43) : Colors.white,
              ),
            ),
            const SizedBox(width: 9),
            Expanded(
              child: Text(
                option.label,
                maxLines: 1,
                style: TextStyle(
                  fontSize: 13,
                  color: ink,
                  fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                ),
              ),
            ),
            if (selected) const _SelectedMark(size: 18),
          ],
        ),
      ),
    );
  }

  Widget _field(
    String label,
    Key key,
    TextEditingController text,
    FocusNode focus,
    bool pickup,
    IconData icon,
  ) => Stack(
    clipBehavior: Clip.none,
    children: [
      TextFormField(
        key: key,
        controller: text,
        focusNode: focus,
        style: TextStyle(
          fontSize: 16,
          fontWeight: pickup ? FontWeight.w600 : FontWeight.w400,
          color: ink,
        ),
        decoration: InputDecoration(
          hintText: pickup ? 'Nhập điểm đón' : 'Điểm đến',
          hintStyle: const TextStyle(
            color: Color(0xFF87938D),
            fontWeight: FontWeight.w400,
          ),
          filled: true,
          fillColor: Colors.white,
          focusColor: Colors.transparent,
          hoverColor: Colors.transparent,
          contentPadding: const EdgeInsets.symmetric(
            horizontal: 16,
            vertical: 18,
          ),
          border: OutlineInputBorder(
            borderRadius: BorderRadius.circular(17),
            borderSide: const BorderSide(color: Color(0xFFDFE4EC)),
          ),
          enabledBorder: OutlineInputBorder(
            borderRadius: BorderRadius.circular(17),
            borderSide: const BorderSide(color: Color(0xFFDFE4EC), width: 1.2),
          ),
          focusedBorder: OutlineInputBorder(
            borderRadius: BorderRadius.circular(17),
            borderSide: const BorderSide(color: _bookingAccent, width: 1.5),
          ),
          prefixIcon: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12),
            child: Icon(icon, color: _selectionGreen, size: 27),
          ),
          suffixIcon: pickup
              ? IconButton(
                  tooltip: 'Dùng vị trí hiện tại',
                  icon: _locating
                      ? const SizedBox(
                          width: 20,
                          height: 20,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: _bookingAccent,
                            semanticsLabel: 'Đang lấy vị trí',
                          ),
                        )
                      : const Icon(
                          Icons.my_location_rounded,
                          color: _bookingAccent,
                          size: 23,
                        ),
                  onPressed: _locating || widget.onLocate == null
                      ? null
                      : _useCurrentLocation,
                )
              : null,
        ),
        onChanged: (value) {
          if (!pickup) setState(() => _query = value.trim());
        },
        validator: (value) => value == null || value.trim().isEmpty
            ? 'Vui lòng nhập ${label.toLowerCase()}.'
            : null,
        textInputAction: pickup ? TextInputAction.next : TextInputAction.done,
        onFieldSubmitted: (_) {
          if (pickup) {
            _destinationFocus.requestFocus();
          } else {
            _apply();
          }
        },
      ),
      Positioned(
        left: 36,
        top: -8,
        child: IgnorePointer(
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 5),
            color: Colors.white,
            child: Text(
              label,
              style: const TextStyle(
                color: muted,
                fontWeight: FontWeight.w700,
                fontSize: 11.5,
              ),
            ),
          ),
        ),
      ),
    ],
  );
}

class _SelectedMark extends StatelessWidget {
  const _SelectedMark({required this.size});
  final double size;
  @override
  Widget build(BuildContext context) => Container(
    width: size,
    height: size,
    decoration: const BoxDecoration(
      color: _selectionGreen,
      shape: BoxShape.circle,
    ),
    child: Icon(Icons.check_rounded, size: size * .78, color: Colors.white),
  );
}

class _RouteArrowPainter extends CustomPainter {
  const _RouteArrowPainter();

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = _bookingAccent
      ..strokeWidth = 1.2
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round
      ..style = PaintingStyle.stroke;
    final center = size.width / 2;
    canvas.drawPath(
      Path()
        ..moveTo(center, 2)
        ..lineTo(center, 15)
        ..moveTo(center - 3.5, 11.5)
        ..lineTo(center, 15)
        ..lineTo(center + 3.5, 11.5),
      paint,
    );
  }

  @override
  bool shouldRepaint(_RouteArrowPainter oldDelegate) => false;
}

/// Source regions keep each illustration separate in the bundled atlas.
class _BookingArt extends StatefulWidget {
  const _BookingArt({required this.index});
  final int index;
  @override
  State<_BookingArt> createState() => _BookingArtState();
}

class _BookingArtState extends State<_BookingArt> {
  ImageStream? _stream;
  ImageInfo? _image;
  late final _listener = ImageStreamListener((image, _) {
    if (mounted) {
      setState(() {
        _image?.dispose();
        _image = image;
      });
    }
  });
  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final stream = const AssetImage(
      'assets/images/booking-vehicles.png',
    ).resolve(createLocalImageConfiguration(context));
    if (_stream?.key == stream.key) return;
    _stream?.removeListener(_listener);
    _stream = stream..addListener(_listener);
  }

  @override
  void dispose() {
    _stream?.removeListener(_listener);
    _image?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      CustomPaint(painter: _BookingArtPainter(_image?.image, widget.index));
}

class _BookingArtPainter extends CustomPainter {
  const _BookingArtPainter(this.image, this.index);
  final ui.Image? image;
  final int index;
  static const regions = [
    Rect.fromLTWH(0, 0, .57, .55),
    Rect.fromLTWH(.57, .09, .43, .46),
    Rect.fromLTWH(0, .60, .52, .35),
    Rect.fromLTWH(.51, .59, .49, .35),
  ];
  @override
  void paint(Canvas canvas, Size size) {
    final bitmap = image;
    if (bitmap == null) return;
    final region = regions[index];
    final source = Rect.fromLTWH(
      region.left * bitmap.width,
      region.top * bitmap.height,
      region.width * bitmap.width,
      region.height * bitmap.height,
    );
    final fitted = applyBoxFit(BoxFit.contain, source.size, size);
    final destination = Alignment.center.inscribe(
      fitted.destination,
      Offset.zero & size,
    );
    canvas.drawImageRect(
      bitmap,
      source,
      destination,
      Paint()..filterQuality = FilterQuality.medium,
    );
  }

  @override
  bool shouldRepaint(_BookingArtPainter oldDelegate) =>
      image != oldDelegate.image || index != oldDelegate.index;
}
