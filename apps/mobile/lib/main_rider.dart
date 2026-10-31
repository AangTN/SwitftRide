import 'package:flutter/material.dart';
import 'src/app.dart';
import 'src/auth.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(SwiftRideApp(role: AppRole.rider));
}
