import 'package:maplibre_gl/maplibre_gl.dart';
import 'auth.dart';

class RidePlace {
  const RidePlace(this.name, this.address, [this.point, this.placeId]);
  final String name;
  final String address;
  final LatLng? point;
  final String? placeId;

  bool get isResolved =>
      name.trim().isNotEmpty &&
      address.trim().isNotEmpty &&
      point != null &&
      point!.latitude.isFinite &&
      point!.longitude.isFinite &&
      point!.latitude >= -90 &&
      point!.latitude <= 90 &&
      point!.longitude >= -180 &&
      point!.longitude <= 180;
}

class PlacePrediction {
  const PlacePrediction(this.description, this.placeId);
  final String description;
  final String placeId;
}

abstract interface class PlaceLookup {
  Future<List<PlacePrediction>> search(String input);
  Future<RidePlace> details(String placeId);
}

class PlacesApi implements PlaceLookup {
  PlacesApi(this.auth);
  final AuthApi auth;

  @override
  Future<List<PlacePrediction>> search(String input) async {
    final data = await auth.getAuthenticatedJson(
      'matching/places/search',
      query: {'input': input.trim()},
    );
    if (data is! List) {
      throw const AuthFailure('Kết quả tìm địa điểm không hợp lệ.');
    }
    return data.map((item) {
      if (item is! Map<String, dynamic> ||
          item['description'] is! String ||
          item['placeId'] is! String ||
          (item['placeId'] as String).isEmpty) {
        throw const AuthFailure('Kết quả tìm địa điểm không hợp lệ.');
      }
      return PlacePrediction(
        item['description'] as String,
        item['placeId'] as String,
      );
    }).toList();
  }

  @override
  Future<RidePlace> details(String placeId) async {
    final data = await auth.getAuthenticatedJson(
      'matching/places/details',
      query: {'placeId': placeId},
    );
    if (data is! Map<String, dynamic> ||
        data['placeId'] != placeId ||
        data['name'] is! String ||
        data['formattedAddress'] is! String ||
        data['latitude'] is! num ||
        data['longitude'] is! num) {
      throw const AuthFailure('Địa điểm chưa có địa chỉ và tọa độ hợp lệ.');
    }
    final latitude = (data['latitude'] as num).toDouble();
    final longitude = (data['longitude'] as num).toDouble();
    // MapLibre clamps/wraps invalid coordinates; reject them before construction.
    if (!latitude.isFinite ||
        !longitude.isFinite ||
        latitude < -90 ||
        latitude > 90 ||
        longitude < -180 ||
        longitude > 180) {
      throw const AuthFailure('Địa điểm chưa có địa chỉ và tọa độ hợp lệ.');
    }
    final place = RidePlace(
      data['name'] as String,
      data['formattedAddress'] as String,
      LatLng(latitude, longitude),
      placeId,
    );
    if (!place.isResolved) {
      throw const AuthFailure('Địa điểm chưa có địa chỉ và tọa độ hợp lệ.');
    }
    return place;
  }
}
