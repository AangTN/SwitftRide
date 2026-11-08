import 'dart:async';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:maplibre_gl/maplibre_gl.dart';

import 'app.dart' show ink, muted;
import 'auth.dart' show AuthFailure;
import 'places_api.dart';
export 'places_api.dart' show RidePlace;

const _selectionGreen = Color(0xFF00B53E);
const _bookingAccent = Color(0xFF00A83B);

// Canonical addresses/coordinates verified against Goong V2. No fake history.
const suggestedDestinations = [
  RidePlace(
    'Chợ Bến Thành',
    'Chợ Bến Thành, Bến Thành, Hồ Chí Minh',
    LatLng(10.77253621500006, 106.69798153800008),
    'vYi6sWSfKrJwsnANYU1CNgGU5j8SqSm_haRliJr1ze8Vko4g5hRSmxFKZVUS3F5yCZ5lnD6oXrINWgE0uhsCwh36aVSQUgZzmZKBWQoBwgOllmkUWthaQgGeyTT2IF_uA',
  ),
  RidePlace(
    'Nhà thờ Đức Bà',
    'Nhà thờ Sài Gòn, 1 Công Xã Paris, Sài Gòn, Hồ Chí Minh',
    LatLng(10.779761528000051, 106.69907017200006),
    'RaqIuKFMpB51unBruwyS414qXXRy6KISKb6RdaaFJbOmAzEEdv09p2GHNfB6_LLLXaWpBuI4X-9BYQl1o8xPz8nW0e3q2Svf0bKhegIjhiHhtkk0evjWYiG-6RR-XMYjI',
  ),
  RidePlace(
    'Phố đi bộ Nguyễn Huệ',
    'Phố đi bộ Nguyễn Huệ, Nguyễn Huệ, Sài Gòn, Hồ Chí Minh',
    LatLng(10.774097806000043, 106.70363123800007),
    'anSmTAp0irBdzlM1um7D8R-4cTuMa9WKW84kFLl5wo5HbkcuoXnhuGjOUzO9lI31HalQT7R44qxjtnK4B0POpGiUfa6C9R-yKbqpcSIp644JvkE8cvBya8W1HuDeKHcqV',
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

  Map<String, Object> toTripRequest() {
    if (!pickup.isResolved || !destination.isResolved) {
      throw StateError('Điểm đón và điểm đến phải có địa chỉ và tọa độ.');
    }
    return {
      'pickupAddress': pickup.address,
      'pickupLatitude': pickup.point!.latitude,
      'pickupLongitude': pickup.point!.longitude,
      'destinationAddress': destination.address,
      'destinationLatitude': destination.point!.latitude,
      'destinationLongitude': destination.point!.longitude,
      'paymentMethod': paymentMethod == RidePaymentMethod.cash
          ? 'Cash'
          : 'Wallet',
      'vehicleType': switch (vehicleType) {
        RideVehicleType.motorcycle => 'Motorbike',
        RideVehicleType.car4Seats => 'Car4Seats',
        RideVehicleType.car7Seats => 'Car7Seats',
      },
    };
  }
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
    this.places,
  });
  final RidePlace pickup;
  final RidePlace? destination;
  final List<RidePlace> recentDestinations;
  final RideVehicleType vehicleType;
  final RidePaymentMethod paymentMethod;
  final PlaceLookup? places;

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
  Timer? _debounce;
  int _requestVersion = 0;
  bool _searchPickup = false;
  bool _searching = false;
  bool _resolving = false;
  String? _lookupError;
  List<PlacePrediction> _predictions = [];
  late RidePlace _pickup = widget.pickup;
  late RidePlace? _destination = widget.destination;
  late RideVehicleType _vehicleType = widget.vehicleType;
  late RidePaymentMethod _paymentMethod = widget.paymentMethod;

  @override
  void dispose() {
    _debounce?.cancel();
    _requestVersion++;
    _pickupText.dispose();
    _destinationText.dispose();
    _pickupFocus.dispose();
    _destinationFocus.dispose();
    super.dispose();
  }

  void _changed(String value, bool pickup) {
    _debounce?.cancel();
    final version = ++_requestVersion;
    setState(() {
      _searchPickup = pickup;
      _query = value.trim();
      if (pickup) {
        _pickup = defaultPickup;
      } else {
        _destination = null;
      }
      _predictions = [];
      _lookupError = null;
      _resolving = false;
      _searching = _query.length >= 2;
    });
    if (_searching) {
      _debounce = Timer(
        const Duration(milliseconds: 350),
        () => _search(version),
      );
    }
  }

  void _activate(bool pickup) {
    if (_searchPickup == pickup) return;
    _debounce?.cancel();
    final version = ++_requestVersion;
    final text = pickup ? _pickupText.text : _destinationText.text;
    final resolved = pickup
        ? _pickup.isResolved
        : _destination?.isResolved == true;
    setState(() {
      _searchPickup = pickup;
      _query = resolved ? '' : text.trim();
      _predictions = [];
      _lookupError = null;
      _resolving = false;
      _searching = _query.length >= 2;
    });
    if (_searching) {
      _debounce = Timer(
        const Duration(milliseconds: 350),
        () => _search(version),
      );
    }
  }

  Future<void> _search(int version) async {
    try {
      final api = widget.places;
      if (api == null) {
        throw const AuthFailure('Không kết nối được dịch vụ tìm địa điểm.');
      }
      final results = await api.search(_query);
      if (!mounted || version != _requestVersion) return;
      setState(() {
        _predictions = results;
        _searching = false;
      });
    } catch (error) {
      if (!mounted || version != _requestVersion) return;
      setState(() {
        _lookupError = _errorMessage(error);
        _searching = false;
      });
    }
  }

  String _errorMessage(Object error) => error is AuthFailure
      ? error.message
      : 'Không tải được địa điểm. Vui lòng thử lại.';

  Future<void> _selectPrediction(PlacePrediction prediction) async {
    _debounce?.cancel();
    final version = ++_requestVersion;
    final pickup = _searchPickup;
    setState(() {
      _resolving = true;
      _lookupError = null;
      _searching = false;
    });
    try {
      final place = await widget.places!.details(prediction.placeId);
      if (!mounted || version != _requestVersion) return;
      if (!place.isResolved) {
        throw const AuthFailure('Địa điểm chưa có tọa độ hợp lệ.');
      }
      _savePlace(place, pickup);
    } catch (error) {
      if (!mounted || version != _requestVersion) return;
      setState(() {
        _lookupError = _errorMessage(error);
        _resolving = false;
      });
    }
  }

  void _savePlace(RidePlace place, bool pickup) {
    _debounce?.cancel();
    _requestVersion++;
    setState(() {
      if (pickup) {
        _pickup = place;
        _pickupText.text = place.name;
      } else {
        _destination = place;
        _destinationText.text = place.name;
      }
      _query = '';
      _predictions = [];
      _resolving = false;
      _searching = false;
      _lookupError = null;
    });
    FocusScope.of(context).unfocus();
  }

  void _apply() {
    if (!_form.currentState!.validate() || _resolving || _searching) return;
    Navigator.pop(
      context,
      RideLocations(_pickup, _destination!, _vehicleType, _paymentMethod),
    );
  }

  @override
  Widget build(BuildContext context) {
    final recent = widget.recentDestinations;
    final defaults = recent.isEmpty ? suggestedDestinations : recent;
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
                            ? (_searchPickup
                                  ? 'Kết quả điểm đón'
                                  : 'Kết quả điểm đến')
                            : _searchPickup
                            ? 'Tìm điểm đón'
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
                      if (_searching || _resolving)
                        Padding(
                          padding: const EdgeInsets.symmetric(vertical: 16),
                          child: Row(
                            children: [
                              const SizedBox(
                                width: 18,
                                height: 18,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                  color: _bookingAccent,
                                ),
                              ),
                              const SizedBox(width: 10),
                              Expanded(
                                child: Text(
                                  _resolving
                                      ? 'Đang lấy địa chỉ và tọa độ…'
                                      : 'Đang tìm địa điểm…',
                                ),
                              ),
                            ],
                          ),
                        )
                      else if (_lookupError != null)
                        Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              _lookupError!,
                              style: const TextStyle(color: muted),
                            ),
                            TextButton(
                              onPressed: () {
                                setState(() {
                                  _lookupError = null;
                                  _searching = true;
                                });
                                _search(++_requestVersion);
                              },
                              child: const Text('Thử lại'),
                            ),
                          ],
                        )
                      else if (_query.isNotEmpty)
                        if (_query.length < 2)
                          const Text(
                            'Nhập ít nhất 2 ký tự để tìm địa điểm.',
                            style: TextStyle(color: muted),
                          )
                        else if (_predictions.isEmpty)
                          const Text(
                            'Không tìm thấy địa điểm. Hãy thử tên hoặc địa chỉ cụ thể hơn.',
                            style: TextStyle(color: muted),
                          )
                        else
                          for (final prediction in _predictions)
                            _predictionTile(prediction)
                      else if (_searchPickup)
                        const Text(
                          'Nhập tên địa điểm hoặc địa chỉ điểm đón, rồi chọn kết quả phù hợp.',
                          style: TextStyle(color: muted),
                        )
                      else
                        for (final place in defaults) _placeTile(place),
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
                          Expanded(child: _paymentCard(RidePaymentMethod.cash)),
                          const SizedBox(width: 10),
                          Expanded(
                            child: _paymentCard(
                              RidePaymentMethod.swiftRideWallet,
                            ),
                          ),
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
                  onPressed: _resolving || _searching ? null : _apply,
                  child: const Text('Đặt xe'),
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

  Widget _predictionTile(PlacePrediction prediction) => Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: ListTile(
      key: ValueKey('place-${prediction.placeId}'),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(17),
        side: const BorderSide(color: Color(0xFFECEEF2)),
      ),
      leading: const Icon(Icons.location_on_rounded, color: _selectionGreen),
      title: Text(
        prediction.description,
        style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600),
      ),
      onTap: () => _selectPrediction(prediction),
    ),
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
        onTap: () => _savePlace(place, false),
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
                    child: BookingVehicleArt(index: option.index + 1),
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
                color: _bookingAccent,
                borderRadius: BorderRadius.circular(7),
              ),
              child: Icon(option.icon, size: 21, color: Colors.white),
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
          helperText:
              (pickup ? _pickup : _destination)?.address.isNotEmpty == true
              ? (pickup ? _pickup : _destination)!.address
              : null,
          helperMaxLines: 2,
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
        ),
        onTap: () => _activate(pickup),
        onChanged: (value) => _changed(value, pickup),
        validator: (value) {
          if (value == null || value.trim().isEmpty) {
            return 'Vui lòng nhập ${label.toLowerCase()}.';
          }
          final selected = pickup ? _pickup : _destination;
          if (selected == null ||
              !selected.isResolved ||
              selected.name != value.trim()) {
            return 'Vui lòng chọn ${label.toLowerCase()} từ kết quả tìm kiếm.';
          }
          return null;
        },
        maxLength: 200,
        buildCounter:
            (_, {required currentLength, required isFocused, maxLength}) =>
                null,
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
class BookingVehicleArt extends StatefulWidget {
  const BookingVehicleArt({super.key, required this.index});
  final int index;
  @override
  State<BookingVehicleArt> createState() => BookingVehicleArtState();
}

class BookingVehicleArtState extends State<BookingVehicleArt> {
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
  Widget build(BuildContext context) => CustomPaint(
    painter: BookingVehicleArtPainter(_image?.image, widget.index),
  );
}

class BookingVehicleArtPainter extends CustomPainter {
  const BookingVehicleArtPainter(this.image, this.index);
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
  bool shouldRepaint(BookingVehicleArtPainter oldDelegate) =>
      image != oldDelegate.image || index != oldDelegate.index;
}
