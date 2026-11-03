import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'auth.dart';
import 'rider_home.dart';
import 'places_api.dart';

const green = Color(0xFF00B650);
const ink = Color(0xFF102237);
const muted = Color(0xFF748095);
const mint = Color(0xFFE5F8EE);

class SwiftRideApp extends StatelessWidget {
  const SwiftRideApp({super.key, required this.role, this.api});
  final AppRole role;
  final AuthApi? api;
  @override
  Widget build(BuildContext context) => MaterialApp(
    title: role == AppRole.driver ? 'SwiftRide Driver' : 'SwiftRide',
    debugShowCheckedModeBanner: false,
    theme: ThemeData(
      useMaterial3: true,
      fontFamily: 'BeVietnamPro',
      colorScheme: ColorScheme.fromSeed(
        seedColor: green,
        primary: green,
        surface: Colors.white,
      ),
      scaffoldBackgroundColor: Colors.white,
      bottomSheetTheme: const BottomSheetThemeData(
        dragHandleColor: Color(0xFFC6CBD3),
        dragHandleSize: Size(38, 4),
      ),
      textTheme: const TextTheme(
        bodyMedium: TextStyle(color: ink),
        bodyLarge: TextStyle(color: ink),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: Colors.white,
        hintStyle: const TextStyle(color: Color(0xFFAFBAC9), fontSize: 14),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 18,
        ),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: const BorderSide(color: Color(0xFFDCE3EB)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: const BorderSide(color: Color(0xFFDCE3EB)),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: const BorderSide(color: green, width: 1.5),
        ),
      ),
    ),
    home: AuthScreen(role: role, api: api),
  );
}

class Brand extends StatelessWidget {
  const Brand({super.key, this.size = 32});
  final double size;
  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Icon(Icons.share_location_rounded, color: green, size: size * 1.55),
      const SizedBox(width: 5),
      Text.rich(
        TextSpan(
          children: [
            const TextSpan(
              text: 'Swift',
              style: TextStyle(color: ink),
            ),
            const TextSpan(
              text: 'Ride',
              style: TextStyle(color: green),
            ),
          ],
        ),
        style: TextStyle(
          fontSize: size,
          fontWeight: FontWeight.w700,
          fontStyle: FontStyle.italic,
          letterSpacing: -1.8,
        ),
      ),
    ],
  );
}

class AuthScreen extends StatefulWidget {
  const AuthScreen({super.key, required this.role, this.api});
  final AppRole role;
  final AuthApi? api;
  @override
  State<AuthScreen> createState() => _AuthScreenState();
}

class _AuthScreenState extends State<AuthScreen> {
  late final AuthApi api = widget.api ?? AuthApi(role: widget.role);
  final phone = TextEditingController();
  final name = TextEditingController();
  final otp = TextEditingController();
  final form = GlobalKey<FormState>();
  OtpChallenge? challenge;
  UserProfile? user;
  bool registering = false;
  bool busy = false;
  bool restoring = true;
  bool otpBlocked = false;
  String otpUnavailableMessage = 'Mã không còn hiệu lực';
  String? error;
  Timer? timer;
  bool get driver => widget.role == AppRole.driver;
  int remaining(DateTime date) =>
      (date.difference(DateTime.now()).inMilliseconds / 1000).ceil().clamp(
        0,
        99999,
      );

  @override
  void initState() {
    super.initState();
    _restore();
    timer = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted && challenge != null) setState(() {});
    });
  }

  Future<void> _restore() async {
    await api.restore();
    if (mounted) {
      setState(() {
        user = api.currentProfile;
        restoring = false;
      });
    }
  }

  @override
  void dispose() {
    timer?.cancel();
    phone.dispose();
    name.dispose();
    otp.dispose();
    if (widget.api == null) api.dispose();
    super.dispose();
  }

  Future<void> _run(Future<void> Function() action) async {
    if (busy) return;
    setState(() {
      busy = true;
      error = null;
    });
    try {
      await action();
    } catch (e) {
      if (mounted) {
        setState(() {
          error = e is AuthFailure
              ? e.message
              : 'Có lỗi xảy ra. Vui lòng thử lại.';
          user = api.session == null ? null : user;
          if (e is AuthFailure &&
              (e.code == 'otp_attempts_exceeded' ||
                  e.code == 'invalid_challenge')) {
            otpBlocked = true;
            otpUnavailableMessage = e.code == 'otp_attempts_exceeded'
                ? 'Mã này đã bị khóa'
                : 'Mã không còn hiệu lực';
            otp.clear();
            FocusScope.of(context).unfocus();
          }
          if (e is AuthFailure && e.code == 'rider_activation_required') {
            registering = true;
            challenge = null;
            otp.clear();
          }
          if (e is AuthFailure &&
              (e.code == 'profile_load_failed' || e.code == 'role_required')) {
            challenge = null;
            registering = false;
            otp.clear();
          }
        });
      }
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  Future<void> _request() async {
    if (!form.currentState!.validate()) return;
    FocusScope.of(context).unfocus();
    await _run(() async {
      final result = await api.requestOtp(
        phone.text,
        fullName: registering ? name.text : null,
      );
      if (mounted) {
        setState(() {
          challenge = result;
          otpBlocked = false;
          otp.clear();
        });
      }
    });
  }

  Future<void> _verify() async {
    if (busy ||
        challenge == null ||
        otpBlocked ||
        remaining(challenge!.expiresAt) == 0) {
      return;
    }
    if (!form.currentState!.validate()) return;
    FocusScope.of(context).unfocus();
    await _run(() async {
      final result = await api.verify(
        challenge!,
        otp.text,
        register: registering,
      );
      if (mounted) {
        setState(() {
          user = api.currentProfile ?? result.user;
          challenge = null;
          otp.clear();
        });
      }
    });
  }

  void _backToLogin() {
    setState(() {
      challenge = null;
      registering = false;
      otpBlocked = false;
      error = null;
      otp.clear();
    });
  }

  Future<void> _logout() => _run(() async {
    try {
      await api.logout();
    } finally {
      if (mounted) {
        setState(() {
          user = null;
          challenge = null;
          registering = false;
          otp.clear();
        });
      }
    }
  });

  Future<void> _editName() async {
    var value = user!.fullName;
    final editForm = GlobalKey<FormState>();
    final changed = await showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Tên hiển thị khách hàng'),
        content: Form(
          key: editForm,
          child: TextFormField(
            initialValue: value,
            autofocus: true,
            maxLength: 150,
            onChanged: (text) => value = text,
            validator: (text) => text == null || text.trim().isEmpty
                ? 'Vui lòng nhập tên.'
                : null,
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () {
              if (editForm.currentState!.validate()) {
                Navigator.pop(dialogContext, value.trim());
              }
            },
            child: const Text('Lưu'),
          ),
        ],
      ),
    );
    if (changed == null || !mounted) return;
    await _run(() async {
      final result = await api.updateRiderName(changed);
      if (mounted) setState(() => user = result);
    });
  }

  @override
  Widget build(BuildContext context) {
    if (!restoring && user != null && !driver) {
      return Scaffold(
        backgroundColor: const Color(0xFFF0F6F2),
        body: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 430),
            // Keep modal barriers inside the same phone frame as the home page.
            child: ClipRect(
              child: Navigator(
                pages: [
                  MaterialPage<void>(
                    key: const ValueKey('rider-home'),
                    child: RiderHome(
                      places: PlacesApi(api),
                      fullName: user!.fullName,
                      account: _profileContent(),
                    ),
                  ),
                ],
                onDidRemovePage: (_) {},
              ),
            ),
          ),
        ),
      );
    }
    return Scaffold(
      backgroundColor: const Color(0xFFF0F6F2),
      body: restoring
          ? const Center(child: CircularProgressIndicator())
          : LayoutBuilder(
              builder: (context, constraints) => Align(
                alignment: Alignment.topCenter,
                child: SizedBox(
                  width: constraints.maxWidth.clamp(0, 430),
                  child: ColoredBox(
                    color: Colors.white,
                    child: user == null
                        ? SafeArea(
                            child: LayoutBuilder(
                              builder: (context, phoneConstraints) =>
                                  _authContent(phoneConstraints.maxHeight),
                            ),
                          )
                        : SingleChildScrollView(
                            child: ConstrainedBox(
                              constraints: BoxConstraints(
                                minHeight: constraints.maxHeight,
                              ),
                              child: _profileContent(),
                            ),
                          ),
                  ),
                ),
              ),
            ),
    );
  }

  Widget _authContent(double height) {
    final compact = height < 700;
    return SingleChildScrollView(
      key: const Key('auth-scroll'),
      child: ConstrainedBox(
        constraints: BoxConstraints(minHeight: height),
        child: IntrinsicHeight(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Expanded(
                child: ConstrainedBox(
                  constraints: BoxConstraints(minHeight: compact ? 90 : 110),
                  child: Stack(
                    clipBehavior: Clip.none,
                    children: [
                      Positioned(
                        top: 0,
                        left: 0,
                        right: 0,
                        bottom: -24,
                        child: Image.asset(
                          'assets/images/${driver ? 'driver' : 'rider'}-hero.webp',
                          fit: BoxFit.cover,
                          alignment: Alignment.topCenter,
                          errorBuilder: (_, error, stack) => Container(
                            color: const Color(0xFFBCE9F7),
                            child: const Icon(
                              Icons.two_wheeler,
                              size: 100,
                              color: green,
                            ),
                          ),
                        ),
                      ),
                      Positioned(
                        top: compact ? 18 : 38,
                        left: 0,
                        right: 0,
                        child: Column(
                          children: [
                            Brand(size: compact ? 28 : 34),
                            const SizedBox(height: 2),
                            Text(
                              driver
                                  ? 'Dành cho Tài xế'
                                  : 'Đặt xe nhanh, đi đâu cũng tiện',
                              style: TextStyle(
                                color: ink,
                                fontSize: 14,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                          ],
                        ),
                      ),
                      if (registering || challenge != null)
                        Positioned(
                          top: 14,
                          left: 12,
                          child: IconButton.filledTonal(
                            tooltip: 'Quay lại',
                            style: IconButton.styleFrom(
                              backgroundColor: Colors.white,
                            ),
                            onPressed: busy
                                ? null
                                : () => setState(() {
                                    if (challenge != null) {
                                      challenge = null;
                                      otp.clear();
                                    } else {
                                      registering = false;
                                    }
                                    error = null;
                                  }),
                            icon: const Icon(
                              Icons.arrow_back_rounded,
                              size: 21,
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              ),
              Container(
                decoration: const BoxDecoration(
                  color: Colors.white,
                  borderRadius: BorderRadius.vertical(top: Radius.circular(30)),
                ),
                padding: EdgeInsets.fromLTRB(
                  24,
                  compact ? (registering && challenge == null ? 8 : 12) : 27,
                  24,
                  compact ? 12 : 20,
                ),
                child: Form(
                  key: form,
                  child: Column(
                    children: [
                      if (challenge != null)
                        ..._otpForm(compact)
                      else
                        ..._phoneForm(compact),
                      if (error != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 14),
                          child: Container(
                            width: double.infinity,
                            padding: const EdgeInsets.all(12),
                            decoration: BoxDecoration(
                              color: const Color(0xFFFFF2EF),
                              borderRadius: BorderRadius.circular(12),
                            ),
                            child: Text(
                              error!,
                              style: const TextStyle(
                                color: Color(0xFFAE392E),
                                fontSize: 12,
                                height: 1.6,
                              ),
                            ),
                          ),
                        ),
                      if (challenge == null) ...[
                        SizedBox(height: compact ? 8 : 24),
                        _benefits(compact),
                      ],
                      if (challenge != null && !compact) ...[
                        const SizedBox(height: 12),
                        const Text(
                          'Không chia sẻ mã xác thực với bất kỳ ai.',
                          textAlign: TextAlign.center,
                          style: TextStyle(
                            color: muted,
                            fontSize: 10,
                            height: 1.6,
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  List<Widget> _phoneForm(bool compact) => [
    Text(
      registering
          ? 'Đăng ký tài khoản'
          : driver
          ? 'Đăng nhập tài xế'
          : 'Đăng nhập',
      textAlign: TextAlign.center,
      style: const TextStyle(
        color: ink,
        fontSize: 25,
        fontWeight: FontWeight.w700,
        letterSpacing: -.9,
      ),
    ),
    const SizedBox(height: 9),
    Text(
      registering
          ? (compact
                ? 'Bắt đầu trải nghiệm SwiftRide'
                : 'Tạo tài khoản để bắt đầu trải nghiệm SwiftRide')
          : 'Nhập số điện thoại để tiếp tục',
      textAlign: TextAlign.center,
      style: const TextStyle(color: muted, fontSize: 13, height: 1.7),
    ),
    SizedBox(height: compact ? (registering ? 8 : 14) : 24),
    if (registering) ...[
      TextFormField(
        controller: name,
        enabled: !busy,
        textCapitalization: TextCapitalization.words,
        autofillHints: const [AutofillHints.name],
        maxLength: 150,
        decoration: InputDecoration(
          hintText: 'Họ và tên',
          counterText: '',
          prefixIcon: const Icon(Icons.person_outline, size: 20),
          contentPadding: EdgeInsets.symmetric(
            horizontal: 16,
            vertical: compact ? 12 : 18,
          ),
        ),
        validator: (value) => value == null || value.trim().isEmpty
            ? 'Vui lòng nhập họ và tên.'
            : null,
        textInputAction: TextInputAction.next,
      ),
      const SizedBox(height: 12),
    ],
    TextFormField(
      key: const Key('phone'),
      controller: phone,
      enabled: !busy,
      keyboardType: TextInputType.phone,
      autofillHints: const [AutofillHints.telephoneNumberNational],
      textInputAction: TextInputAction.done,
      onFieldSubmitted: (_) => _request(),
      decoration: InputDecoration(
        hintText: 'Nhập số điện thoại',
        contentPadding: EdgeInsets.symmetric(
          horizontal: 16,
          vertical: compact ? 12 : 18,
        ),
        prefixIcon: Padding(
          padding: const EdgeInsets.only(left: 15, right: 13),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                decoration: BoxDecoration(
                  color: const Color(0xFFEE2529),
                  borderRadius: BorderRadius.circular(3),
                ),
                child: const Icon(
                  Icons.star_rounded,
                  color: Colors.yellow,
                  size: 14,
                ),
              ),
              const SizedBox(width: 8),
              const Text('+84', style: TextStyle(color: ink, fontSize: 14)),
              const SizedBox(width: 10),
              Container(height: 26, width: 1, color: const Color(0xFFE0E5ED)),
            ],
          ),
        ),
      ),
      validator: (value) {
        try {
          normalizePhone(value ?? '');
          return null;
        } on AuthFailure catch (e) {
          return e.message;
        }
      },
    ),
    const SizedBox(height: 16),
    _primary(registering ? 'Đăng ký' : 'Đăng nhập', _request),
    if (!driver) ...[
      SizedBox(height: compact ? 8 : 20),
      if (!registering) ...[
        const Row(
          children: [
            Expanded(child: Divider(color: Color(0xFFE1E6ED))),
            Padding(
              padding: EdgeInsets.symmetric(horizontal: 13),
              child: Text('hoặc', style: TextStyle(fontSize: 11, color: muted)),
            ),
            Expanded(child: Divider(color: Color(0xFFE1E6ED))),
          ],
        ),
        SizedBox(height: compact ? 8 : 16),
        SizedBox(
          width: double.infinity,
          height: 53,
          child: OutlinedButton(
            onPressed: busy
                ? null
                : () => setState(() {
                    registering = true;
                    error = null;
                    form.currentState?.reset();
                  }),
            style: OutlinedButton.styleFrom(
              foregroundColor: green,
              side: const BorderSide(color: Color(0xFF00B957), width: 1.5),
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(15),
              ),
            ),
            child: const Text(
              'Đăng ký tài khoản',
              style: TextStyle(fontWeight: FontWeight.w600),
            ),
          ),
        ),
      ] else
        SizedBox(
          width: double.infinity,
          height: 48,
          child: OutlinedButton.icon(
            onPressed: busy ? null : _backToLogin,
            style: OutlinedButton.styleFrom(
              backgroundColor: const Color(0xFFF3FAF6),
              side: const BorderSide(color: Color(0xFFB8DEC9)),
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(14),
              ),
            ),
            icon: const Icon(Icons.arrow_back_rounded, size: 19),
            label: const Text(
              'Quay về đăng nhập',
              style: TextStyle(fontWeight: FontWeight.w700, fontSize: 13),
            ),
          ),
        ),
    ],
  ];

  List<Widget> _otpForm(bool compact) {
    final expiry = remaining(challenge!.expiresAt);
    final resend = remaining(challenge!.resendAt);
    return [
      if (!compact)
        Container(
          padding: const EdgeInsets.all(14),
          decoration: const BoxDecoration(color: mint, shape: BoxShape.circle),
          child: const Icon(Icons.sms_outlined, color: green, size: 26),
        ),
      if (!compact) const SizedBox(height: 16),
      Text(
        'Xác thực số điện thoại',
        style: TextStyle(
          fontSize: compact ? 20 : 23,
          fontWeight: FontWeight.w700,
          color: ink,
          letterSpacing: -.8,
        ),
      ),
      SizedBox(height: compact ? 6 : 10),
      Text(
        compact
            ? normalizePhone(phone.text)
            : 'Nhập mã OTP gồm 6 chữ số cho\n${normalizePhone(phone.text)}',
        textAlign: TextAlign.center,
        style: const TextStyle(color: muted, fontSize: 13, height: 1.8),
      ),
      SizedBox(height: compact ? 14 : 25),
      TextFormField(
        key: const Key('otp'),
        controller: otp,
        enabled: !busy && expiry > 0 && !otpBlocked,
        autofocus: true,
        maxLength: 6,
        keyboardType: TextInputType.number,
        textAlign: TextAlign.center,
        inputFormatters: [FilteringTextInputFormatter.digitsOnly],
        autofillHints: const [AutofillHints.oneTimeCode],
        style: TextStyle(
          fontSize: compact ? 24 : 27,
          fontWeight: FontWeight.w600,
          letterSpacing: 12,
          color: ink,
        ),
        decoration: InputDecoration(
          contentPadding: EdgeInsets.symmetric(
            horizontal: 12,
            vertical: compact ? 12 : 18,
          ),
          hintText: '· · · · · ·',
          counterText: '',
          semanticCounterText: 'Mã OTP 6 chữ số',
        ),
        validator: (value) => RegExp(r'^\d{6}$').hasMatch(value ?? '')
            ? null
            : 'Vui lòng nhập đủ 6 chữ số.',
        onFieldSubmitted: (_) {
          if (expiry > 0 && !otpBlocked) _verify();
        },
      ),
      SizedBox(height: compact ? 8 : 12),
      Text(
        otpBlocked
            ? otpUnavailableMessage
            : expiry > 0
            ? 'Mã có hiệu lực trong ${expiry ~/ 60}:${(expiry % 60).toString().padLeft(2, '0')}'
            : 'Mã đã hết hạn. Vui lòng gửi lại mã.',
        style: const TextStyle(color: muted, fontSize: 11),
      ),
      SizedBox(height: compact ? 12 : 22),
      _primary('Xác nhận', expiry > 0 && !otpBlocked ? _verify : null),
      SizedBox(height: compact ? 4 : 14),
      TextButton(
        onPressed: busy || resend > 0
            ? null
            : () => _run(() async {
                final next = await api.requestOtp(
                  phone.text,
                  fullName: registering ? name.text : null,
                );
                if (mounted) {
                  setState(() {
                    challenge = next;
                    otpBlocked = false;
                    otp.clear();
                  });
                }
              }),
        child: Text(
          resend > 0 ? 'Gửi lại mã sau ${resend}s' : 'Gửi lại mã OTP',
          style: const TextStyle(fontSize: 12),
        ),
      ),
      SizedBox(height: compact ? 4 : 8),
      SizedBox(
        width: double.infinity,
        height: 48,
        child: OutlinedButton.icon(
          onPressed: busy ? null : _backToLogin,
          icon: const Icon(Icons.arrow_back_rounded, size: 19),
          label: const Text(
            'Quay về đăng nhập',
            style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700),
          ),
          style: OutlinedButton.styleFrom(
            backgroundColor: const Color(0xFFF3FAF6),
            side: const BorderSide(color: Color(0xFFB8DEC9)),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(14),
            ),
          ),
        ),
      ),
    ];
  }

  Widget _primary(String label, VoidCallback? action) => SizedBox(
    width: double.infinity,
    height: 55,
    child: DecoratedBox(
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.centerLeft,
          end: Alignment.centerRight,
          colors: busy
              ? [green.withValues(alpha: .55), const Color(0xFF008F50)]
              : [
                  const Color(0xFF43D62E),
                  const Color(0xFF00B957),
                  const Color(0xFF008F50),
                ],
        ),
        borderRadius: BorderRadius.circular(16),
      ),
      child: FilledButton(
        onPressed: busy ? null : action,
        style: FilledButton.styleFrom(
          backgroundColor: Colors.transparent,
          disabledBackgroundColor: Colors.transparent,
          shadowColor: Colors.transparent,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(16),
          ),
        ),
        child: busy
            ? const SizedBox(
                width: 22,
                height: 22,
                child: CircularProgressIndicator(
                  color: Colors.white,
                  strokeWidth: 2,
                ),
              )
            : Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Text(
                    label,
                    style: const TextStyle(
                      fontSize: 16,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                  const SizedBox(width: 13),
                  const Icon(Icons.arrow_forward_rounded, size: 21),
                ],
              ),
      ),
    ),
  );

  Widget _benefits(bool compact) {
    final items = driver
        ? [
            (Icons.two_wheeler_rounded, 'Nhận chuyến\ndễ dàng'),
            (Icons.bar_chart_rounded, 'Thu nhập\nổn định'),
            (Icons.verified_user_rounded, 'Hỗ trợ\n24/7'),
          ]
        : [
            (Icons.two_wheeler_rounded, 'Đặt xe nhanh\nchóng'),
            (Icons.verified_user_rounded, 'An toàn,\nđáng tin cậy'),
            (Icons.bolt_rounded, 'Giá cước\nminh bạch'),
          ];
    if (compact) {
      return Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: items
            .map(
              (item) => Expanded(
                child: Column(
                  children: [
                    Container(
                      width: 49,
                      height: 49,
                      decoration: const BoxDecoration(
                        color: mint,
                        shape: BoxShape.circle,
                      ),
                      child: Icon(item.$1, color: green, size: 25),
                    ),
                    const SizedBox(height: 5),
                    Text(
                      item.$2,
                      textAlign: TextAlign.center,
                      style: const TextStyle(
                        color: Color(0xFF53627A),
                        fontSize: 11,
                        height: 1.4,
                      ),
                    ),
                  ],
                ),
              ),
            )
            .toList(),
      );
    }
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: items
          .map(
            (item) => Expanded(
              child: Column(
                children: [
                  Container(
                    width: 49,
                    height: 49,
                    decoration: const BoxDecoration(
                      color: mint,
                      shape: BoxShape.circle,
                    ),
                    child: Icon(item.$1, color: green, size: 25),
                  ),
                  const SizedBox(height: 9),
                  Text(
                    item.$2,
                    textAlign: TextAlign.center,
                    style: const TextStyle(
                      color: Color(0xFF53627A),
                      fontSize: 11,
                      height: 1.65,
                    ),
                  ),
                ],
              ),
            ),
          )
          .toList(),
    );
  }

  Widget _profileContent() => SafeArea(
    child: Padding(
      padding: const EdgeInsets.all(25),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              const Flexible(
                child: FittedBox(
                  fit: BoxFit.scaleDown,
                  alignment: Alignment.centerLeft,
                  child: Brand(size: 25),
                ),
              ),
              const SizedBox(width: 12),
              Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 10,
                  vertical: 6,
                ),
                decoration: BoxDecoration(
                  color: mint,
                  borderRadius: BorderRadius.circular(20),
                ),
                child: Text(
                  driver ? 'TÀI XẾ' : 'KHÁCH HÀNG',
                  style: const TextStyle(
                    color: green,
                    fontSize: 9,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 35),
          const Icon(Icons.check_circle_rounded, size: 48, color: green),
          const SizedBox(height: 18),
          const Text(
            'Đăng nhập thành công',
            style: TextStyle(
              color: green,
              fontSize: 12,
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 12),
          Text(
            'Xin chào,\n${user!.fullName}!',
            style: const TextStyle(
              color: ink,
              fontSize: 29,
              fontWeight: FontWeight.w700,
              height: 1.4,
              letterSpacing: -1,
            ),
          ),
          const SizedBox(height: 10),
          Text(
            driver
                ? 'Chúc bạn một ngày vững tay lái và nhiều niềm vui.'
                : 'Sẵn sàng cho những hành trình mới cùng SwiftRide.',
            style: const TextStyle(color: muted, height: 1.8, fontSize: 13),
          ),
          const SizedBox(height: 25),
          ClipRRect(
            borderRadius: BorderRadius.circular(20),
            child: Image.asset(
              'assets/images/${driver ? 'driver' : 'rider'}-hero.webp',
              height: 155,
              width: double.infinity,
              fit: BoxFit.cover,
              alignment: const Alignment(0, .2),
            ),
          ),
          const SizedBox(height: 24),
          const Text(
            'Thông tin tài khoản',
            style: TextStyle(
              fontSize: 15,
              fontWeight: FontWeight.w700,
              color: ink,
            ),
          ),
          const SizedBox(height: 12),
          _detail(Icons.phone_outlined, 'Số điện thoại', user!.phone),
          if (driver && user!.driver != null) ...[
            _detail(
              Icons.two_wheeler_outlined,
              'Phương tiện',
              user!.driver!['vehicleName'] as String,
            ),
            _detail(
              Icons.badge_outlined,
              'Biển số xe',
              user!.driver!['plateNumber'] as String,
            ),
          ],
          const SizedBox(height: 18),
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: mint,
              borderRadius: BorderRadius.circular(14),
            ),
            child: const Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(Icons.info_outline, size: 19, color: green),
                SizedBox(width: 10),
                Expanded(
                  child: Text(
                    'Bản thử nghiệm đăng nhập. Tính năng đặt xe và nhận chuyến sẽ được bổ sung sau.',
                    style: TextStyle(
                      color: Color(0xFF42705B),
                      fontSize: 11,
                      height: 1.7,
                    ),
                  ),
                ),
              ],
            ),
          ),
          if (error != null)
            Padding(
              padding: const EdgeInsets.only(top: 12),
              child: Text(
                error!,
                style: const TextStyle(color: Colors.red, fontSize: 12),
              ),
            ),
          const SizedBox(height: 14),
          if (!driver)
            SizedBox(
              width: double.infinity,
              child: TextButton.icon(
                onPressed: busy ? null : _editName,
                icon: const Icon(Icons.edit_outlined, size: 18),
                label: const Text(
                  'Đổi tên hiển thị',
                  style: TextStyle(fontSize: 12),
                ),
              ),
            ),
          SizedBox(
            width: double.infinity,
            child: TextButton.icon(
              onPressed: busy
                  ? null
                  : () => _run(() async {
                      final result = await api.profile();
                      if (mounted) setState(() => user = result);
                    }),
              icon: const Icon(Icons.refresh, size: 18),
              label: const Text(
                'Làm mới thông tin',
                style: TextStyle(fontSize: 12),
              ),
            ),
          ),
          SizedBox(
            width: double.infinity,
            height: 49,
            child: OutlinedButton.icon(
              onPressed: busy ? null : _logout,
              icon: const Icon(Icons.logout_rounded, size: 18),
              label: Text(busy ? 'Đang xử lý…' : 'Đăng xuất'),
              style: OutlinedButton.styleFrom(
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(14),
                ),
                side: const BorderSide(color: Color(0xFFDCE3EB)),
              ),
            ),
          ),
          const SizedBox(height: 12),
        ],
      ),
    ),
  );

  Widget _detail(IconData icon, String label, String value) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 11),
    child: Row(
      children: [
        Icon(icon, color: muted, size: 20),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            label,
            style: const TextStyle(color: muted, fontSize: 12),
          ),
        ),
        Flexible(
          child: Text(
            value,
            textAlign: TextAlign.right,
            style: const TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w600,
              color: ink,
            ),
          ),
        ),
      ],
    ),
  );
}
