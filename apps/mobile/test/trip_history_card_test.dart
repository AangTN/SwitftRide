import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/trip_models.dart';
import 'package:swiftride_mobile/src/trip_widgets.dart';
import 'trip_api_test.dart' show snapshot;

void main() {
  testWidgets('history distinguishes payment failure and opens trip details', (
    tester,
  ) async {
    var selected = false;
    final data = snapshot(status: 'PaymentPending')
      ..['paymentStatus'] = 'Failed';
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: TripHistoryCard(
            trip: TripSnapshot.fromJson(data),
            onTap: () => selected = true,
          ),
        ),
      ),
    );
    expect(find.text('Thanh toán lỗi'), findsOneWidget);
    expect(find.text('Đã hoàn thành'), findsNothing);
    expect(find.text('Pickup'), findsOneWidget);
    expect(find.text('Destination'), findsOneWidget);
    await tester.tap(find.byType(TripHistoryCard));
    expect(selected, isTrue);
    expect(tester.takeException(), isNull);
  });
}
