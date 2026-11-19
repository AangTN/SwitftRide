import 'package:flutter/services.dart';

/// Best-effort vibration when a new trip proposal arrives.
///
/// Haptics are not available on every platform (for example the web build),
/// so failures are intentionally ignored.
Future<void> vibrateProposal() async {
  try {
    await HapticFeedback.vibrate();
  } catch (_) {}
}
