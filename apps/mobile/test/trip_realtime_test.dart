import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:swiftride_mobile/src/auth.dart';
import 'package:swiftride_mobile/src/trip_realtime.dart';

import 'auth_test.dart' show MemoryStore, tokens;

void main() {
  test(
    'real SignalR transport negotiates, rejoins and delivers server events',
    () async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      final sockets = <WebSocket>[];
      final joined = StreamController<String>.broadcast();
      var negotiations = 0;
      server.listen((request) async {
        if (request.uri.path.endsWith('/negotiate')) {
          negotiations++;
          expect(request.headers.value('authorization'), startsWith('Bearer '));
          request.response.headers.contentType = ContentType.json;
          request.response.write(
            jsonEncode({
              'negotiateVersion': 1,
              'connectionId': 'id-$negotiations',
              'connectionToken': 'token-$negotiations',
              'availableTransports': [
                {
                  'transport': 'WebSockets',
                  'transferFormats': ['Text'],
                },
              ],
            }),
          );
          await request.response.close();
        } else if (WebSocketTransformer.isUpgradeRequest(request)) {
          final socket = await WebSocketTransformer.upgrade(request);
          sockets.add(socket);
          socket.listen((data) {
            for (final frame
                in (data as String)
                    .split('\u001e')
                    .where((frame) => frame.isNotEmpty)) {
              final message = jsonDecode(frame) as Map<String, dynamic>;
              if (message['protocol'] == 'json') {
                socket.add('{}\u001e');
              } else if (message['target'] == 'SubscribeTrip') {
                joined.add((message['arguments'] as List).single as String);
                socket.add(
                  '${jsonEncode({'type': 3, 'invocationId': message['invocationId']})}\u001e',
                );
              }
            }
          });
        } else {
          request.response.statusCode = 404;
          await request.response.close();
        }
      });
      final auth = AuthApi(
        role: AppRole.rider,
        store: MemoryStore(),
        baseUrl: 'http://127.0.0.1:${server.port}',
      )..session = AuthSession.fromJson(tokens());
      final realtime = SignalRTripRealtime(auth);
      addTearDown(() async {
        await realtime.dispose();
        auth.dispose();
        for (final socket in sockets) {
          await socket.close();
        }
        await joined.close();
        await server.close(force: true);
      });

      final firstJoin = joined.stream.first;
      await realtime.watch('trip-1');
      expect(await firstJoin, 'trip-1');
      final update = realtime.events.firstWhere(
        (event) => event.name == 'TripChanged',
      );
      sockets.last.add(
        '${jsonEncode({
          'type': 1,
          'target': 'TripChanged',
          'arguments': [
            {'tripId': 'trip-1', 'stateVersion': 5, 'status': 'AwaitingDriverResponse'},
          ],
        })}\u001e',
      );
      expect((await update).data['stateVersion'], 5);

      final rejoined = joined.stream.first;
      final connected = realtime.events.firstWhere(
        (event) => event.name == 'connected',
      );
      await sockets.last.close();
      expect(await rejoined.timeout(const Duration(seconds: 10)), 'trip-1');
      await connected.timeout(const Duration(seconds: 10));
      expect(negotiations, greaterThanOrEqualTo(2));
    },
  );
}
