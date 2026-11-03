import 'dart:async';
import 'dart:convert';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:http/http.dart' as http;

enum AppRole {
  rider('RiderApp', 'Rider'),
  driver('DriverApp', 'Driver');

  const AppRole(this.clientApp, this.requiredRole);
  final String clientApp;
  final String requiredRole;
}

class AuthFailure implements Exception {
  const AuthFailure(this.message, [this.code = 'unknown']);
  final String message;
  final String code;
  @override
  String toString() => message;
}

String normalizePhone(String input) {
  var phone = input.replaceAll(RegExp(r'[\s()-]'), '');
  if (phone.startsWith('0')) phone = '+84${phone.substring(1)}';
  if (!phone.startsWith('+')) phone = '+84$phone';
  if (!RegExp(r'^\+84[35789][0-9]{8}$').hasMatch(phone)) {
    throw const AuthFailure('Vui lòng nhập số điện thoại Việt Nam hợp lệ.');
  }
  return phone;
}

class OtpChallenge {
  OtpChallenge.fromJson(Map<String, dynamic> data)
    : id = data['challengeId'] as String,
      expiresAt = DateTime.now().add(
        Duration(seconds: data['expiresInSeconds'] as int),
      ),
      resendAt = DateTime.now().add(
        Duration(seconds: data['retryAfterSeconds'] as int),
      );
  final String id;
  final DateTime expiresAt;
  final DateTime resendAt;
}

class UserProfile {
  UserProfile.withDriver(UserProfile user, Map<String, dynamic> profile)
    : fullName = profile['legalName'] as String,
      phone = user.phone,
      roles = user.roles,
      driver = profile;
  UserProfile.fromJson(Map<String, dynamic> data)
    : fullName = data['fullName'] as String,
      phone = data['phoneNumber'] as String,
      roles = (data['roles'] as List).cast<String>(),
      driver = data['driver'] as Map<String, dynamic>?;
  final String fullName;
  final String phone;
  final List<String> roles;
  final Map<String, dynamic>? driver;
}

class AuthSession {
  AuthSession.fromJson(Map<String, dynamic> data)
    : accessToken = data['accessToken'] as String,
      refreshToken = data['refreshToken'] as String,
      expiresAt = DateTime.parse(data['accessTokenExpiresAt'] as String),
      user = UserProfile.fromJson(data['user'] as Map<String, dynamic>);
  final String accessToken;
  final String refreshToken;
  final DateTime expiresAt;
  final UserProfile user;
}

abstract interface class SessionStore {
  Future<String?> read();
  Future<void> write(String data);
  Future<void> clear();
}

class SecureSessionStore implements SessionStore {
  SecureSessionStore(AppRole role)
    : key = 'swiftride.${role.clientApp}.session';
  final String key;
  final FlutterSecureStorage storage = const FlutterSecureStorage();
  @override
  Future<String?> read() => storage.read(key: key);
  @override
  Future<void> write(String data) => storage.write(key: key, value: data);
  @override
  Future<void> clear() => storage.delete(key: key);
}

class AuthApi {
  AuthApi({
    required this.role,
    http.Client? client,
    SessionStore? store,
    String? baseUrl,
  }) : client = client ?? http.Client(),
       store = store ?? SecureSessionStore(role),
       baseUrl = (baseUrl ?? _defaultBaseUrl()).replaceFirst(RegExp(r'/$'), '');
  final AppRole role;
  final http.Client client;
  final SessionStore store;
  final String baseUrl;
  AuthSession? session;
  UserProfile? currentProfile;
  Future<void>? _refreshing;

  static String _defaultBaseUrl() {
    const configured = String.fromEnvironment('API_BASE_URL');
    return configured.isNotEmpty
        ? configured
        : kIsWeb
        ? Uri.base.origin
        : 'http://127.0.0.1:5000';
  }

  Future<Map<String, dynamic>> _request(
    String path, {
    Map<String, dynamic>? body,
    String? token,
    bool get = false,
    bool patch = false,
    bool matching = false,
  }) async {
    try {
      final uri = Uri.parse(
        '$baseUrl/api/${matching ? 'matching' : 'auth'}/$path',
      );
      final headers = <String, String>{
        'Content-Type': 'application/json',
        if (token != null) 'Authorization': 'Bearer $token',
      };
      final response =
          await (get
                  ? client.get(uri, headers: headers)
                  : patch
                  ? client.patch(
                      uri,
                      headers: headers,
                      body: jsonEncode(body ?? {}),
                    )
                  : client.post(
                      uri,
                      headers: headers,
                      body: jsonEncode(body ?? {}),
                    ))
              .timeout(const Duration(seconds: 20));
      final data = response.body.isEmpty
          ? <String, dynamic>{}
          : jsonDecode(response.body) as Map<String, dynamic>;
      if (response.statusCode >= 400) {
        final code = data['title'] as String? ?? 'unknown';
        throw AuthFailure(
          (code == 'registration_required' && role == AppRole.driver
                  ? 'Chưa tìm thấy tài khoản tài xế với số điện thoại này. Vui lòng kiểm tra lại hoặc liên hệ hỗ trợ.'
                  : _messages[code]) ??
              'Không thể hoàn tất yêu cầu (${response.statusCode}). Vui lòng thử lại.',
          code,
        );
      }
      return data;
    } on AuthFailure {
      rethrow;
    } on TimeoutException {
      throw const AuthFailure('Máy chủ phản hồi chậm. Vui lòng thử lại.');
    } on http.ClientException {
      throw const AuthFailure(
        'Không kết nối được máy chủ. Kiểm tra mạng và địa chỉ API.',
      );
    } on FormatException {
      throw const AuthFailure('Phản hồi máy chủ không hợp lệ.');
    }
  }

  Future<OtpChallenge> requestOtp(String phone, {String? fullName}) async {
    return OtpChallenge.fromJson(
      await _request(
        '${fullName == null ? 'login' : 'register'}/request-otp',
        body: {
          'phoneNumber': normalizePhone(phone),
          if (fullName == null) 'clientApp': role.clientApp,
          if (fullName != null) 'fullName': fullName.trim(),
        },
      ),
    );
  }

  Future<AuthSession> verify(
    OtpChallenge challenge,
    String otp, {
    bool register = false,
  }) async {
    final data = await _request(
      '${register ? 'register' : 'login'}/verify-otp',
      body: {
        'challengeId': challenge.id,
        'otp': otp,
        'clientApp': role.clientApp,
        'deviceName': kIsWeb ? 'SwiftRide Web' : 'SwiftRide Android',
      },
    );
    await _accept(data);
    if (role == AppRole.driver) {
      try {
        currentProfile = await profile();
      } catch (_) {
        try {
          await logout();
        } catch (_) {}
        throw const AuthFailure(
          'Không tải được hồ sơ tài xế từ Matching. Vui lòng thử đăng nhập lại.',
          'profile_load_failed',
        );
      }
    }
    return session!;
  }

  Future<void> _accept(Map<String, dynamic> data) async {
    final candidate = AuthSession.fromJson(data);
    if (!candidate.user.roles.contains(role.requiredRole)) {
      try {
        await _request('logout', token: candidate.accessToken);
      } catch (_) {
        /* Deny the local session regardless of logout connectivity. */
      }
      throw AuthFailure(
        role == AppRole.driver
            ? 'Tài khoản chưa được cấp quyền tài xế. Vui lòng dùng tài khoản tài xế.'
            : 'Bạn đã có tài khoản. Hãy nhập tên và xác thực OTP để kích hoạt khách hàng.',
        role == AppRole.driver ? 'role_required' : 'rider_activation_required',
      );
    }
    try {
      await store.write(jsonEncode(data));
    } catch (_) {
      // The verified session can still be used in memory if browser storage is disabled.
      // Never leave a previously rotated refresh token behind in persistent storage.
      try {
        await store.clear();
      } catch (_) {}
    }
    session = candidate;
    if (role == AppRole.rider) currentProfile = candidate.user;
  }

  Future<void> restore() async {
    try {
      final saved = await store.read();
      if (saved == null) return;
      final candidate = AuthSession.fromJson(
        jsonDecode(saved) as Map<String, dynamic>,
      );
      if (!candidate.user.roles.contains(role.requiredRole)) {
        await store.clear();
        return;
      }
      session = candidate;
      currentProfile = await profile();
    } catch (_) {
      session = null;
      currentProfile = null;
      try {
        await store.clear();
      } catch (_) {}
    }
  }

  Future<void> _refresh() =>
      _refreshing ??= _rotate().whenComplete(() => _refreshing = null);
  Future<void> _rotate() async {
    final refreshToken = session!.refreshToken;
    // A refresh is sent only once: retrying an old token can revoke the whole session.
    try {
      final data = await _request(
        'refresh',
        body: {'refreshToken': refreshToken},
      );
      await _accept(data);
    } catch (_) {
      session = null;
      currentProfile = null;
      try {
        await store.clear();
      } catch (_) {}
      rethrow;
    }
  }

  Future<UserProfile> profile() async {
    if (session == null) throw const AuthFailure('Vui lòng đăng nhập lại.');
    if (session!.expiresAt.isBefore(
      DateTime.now().add(const Duration(seconds: 30)),
    )) {
      await _refresh();
    }
    final data = await _request('me', token: session!.accessToken, get: true);
    final user = UserProfile.fromJson(data);
    if (!user.roles.contains(role.requiredRole)) {
      await logout();
      throw const AuthFailure(
        'Tài khoản không còn quyền truy cập ứng dụng này.',
      );
    }
    currentProfile = role == AppRole.driver
        ? UserProfile.withDriver(
            user,
            await _request(
              'drivers/me',
              token: session!.accessToken,
              get: true,
              matching: true,
            ),
          )
        : user;
    return currentProfile!;
  }

  Future<UserProfile> updateRiderName(String name) async {
    if (role != AppRole.rider || session == null) {
      throw const AuthFailure('Cần đăng nhập khách hàng.');
    }
    if (session!.expiresAt.isBefore(
      DateTime.now().add(const Duration(seconds: 30)),
    )) {
      await _refresh();
    }
    final data = await _request(
      'me',
      token: session!.accessToken,
      patch: true,
      body: {'fullName': name.trim()},
    );
    currentProfile = UserProfile.fromJson(data);
    return currentProfile!;
  }

  Future<void> logout() async {
    final current = session;
    try {
      if (current != null) {
        if (current.expiresAt.isBefore(DateTime.now())) await _refresh();
        if (session != null) {
          await _request('logout', token: session!.accessToken);
        }
      }
    } finally {
      session = null;
      currentProfile = null;
      try {
        await store.clear();
      } catch (_) {}
    }
  }

  /// Authenticated API reads reuse the same token and refresh rotation as login.
  Future<Object?> getAuthenticatedJson(
    String path, {
    Map<String, String>? query,
  }) async {
    try {
      if (session == null) throw const AuthFailure('Vui lòng đăng nhập lại.');
      if (session!.expiresAt.isBefore(
        DateTime.now().add(const Duration(seconds: 30)),
      )) {
        await _refresh();
      }
      final uri = Uri.parse(
        '$baseUrl/api/$path',
      ).replace(queryParameters: query);
      Future<http.Response> send(String token) => client
          .get(
            uri,
            headers: {
              'Accept': 'application/json',
              'Authorization': 'Bearer $token',
            },
          )
          .timeout(const Duration(seconds: 20));
      final token = session!.accessToken;
      var response = await send(token);
      if (response.statusCode == 401) {
        if (session == null) throw const AuthFailure('Vui lòng đăng nhập lại.');
        if (session!.accessToken == token) await _refresh();
        response = await send(session!.accessToken);
      }
      if (response.statusCode >= 400) {
        String? code;
        try {
          final error = jsonDecode(utf8.decode(response.bodyBytes));
          if (error is Map<String, dynamic> && error['title'] is String) {
            code = error['title'] as String;
          }
        } on FormatException {
          /* Never expose a raw gateway/provider response. */
        }
        throw AuthFailure(
          response.statusCode == 401
              ? 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.'
              : code == 'address_not_found'
              ? 'Không tìm thấy địa điểm này. Vui lòng chọn kết quả khác.'
              : code == 'goong_not_configured'
              ? 'Dịch vụ tìm địa điểm chưa được cấu hình.'
              : 'Không tải được địa điểm. Vui lòng thử lại.',
          code ?? 'request_failed',
        );
      }
      return jsonDecode(utf8.decode(response.bodyBytes));
    } on AuthFailure {
      rethrow;
    } on TimeoutException {
      throw const AuthFailure('Tìm địa điểm quá thời gian. Vui lòng thử lại.');
    } on http.ClientException {
      throw const AuthFailure('Không kết nối được máy chủ. Vui lòng thử lại.');
    } on FormatException {
      throw const AuthFailure('Phản hồi địa điểm không hợp lệ.');
    }
  }

  void dispose() => client.close();

  static const _messages = {
    'invalid_phone': 'Số điện thoại không hợp lệ.',
    'invalid_name': 'Vui lòng nhập họ và tên.',
    'registration_required':
        'Số điện thoại chưa có tài khoản. Vui lòng đăng ký.',
    'phone_registered': 'Số điện thoại đã đăng ký. Vui lòng đăng nhập.',
    'account_blocked': 'Tài khoản đang bị khóa. Vui lòng liên hệ hỗ trợ.',
    'otp_cooldown': 'Vui lòng chờ trước khi yêu cầu mã mới.',
    'otp_rate_limited': 'Bạn đã yêu cầu quá nhiều mã. Vui lòng thử lại sau.',
    'invalid_otp': 'Mã OTP chưa đúng. Vui lòng kiểm tra lại.',
    'invalid_challenge':
        'Mã đã hết hạn hoặc không còn hiệu lực. Vui lòng yêu cầu mã mới.',
    'otp_attempts_exceeded':
        'Bạn đã nhập sai quá số lần cho phép. Vui lòng yêu cầu mã mới.',
    'otp_unavailable': 'Dịch vụ gửi OTP chưa sẵn sàng.',
    'invalid_refresh_token':
        'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.',
    'account_unavailable': 'Tài khoản không khả dụng.',
    'driver_profile_not_found':
        'Chưa có hồ sơ tài xế trong Matching. Vui lòng liên hệ hỗ trợ.',
    'rider_required': 'Tài khoản chưa kích hoạt quyền khách hàng.',
    'rider_activation_required':
        'Bạn đã có tài khoản. Hãy đăng ký tài khoản khách hàng để sử dụng ứng dụng.',
    'role_required':
        'Tài khoản chưa được cấp quyền tài xế. Vui lòng liên hệ hỗ trợ.',
    'invalid_client':
        'Yêu cầu OTP không thuộc ứng dụng này. Vui lòng quay về đăng nhập.',
  };
}
