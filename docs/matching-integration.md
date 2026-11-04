# Tích hợp Matching với Trip

## Luồng đặt xe

1. Khách tạo chuyến qua Trip, gửi điểm đón, điểm đến và `vehicleType`
   (`Motorbike`, `Car4Seats`, `Car7Seats`; mặc định xe máy).
2. Trip lưu chuyến, proposal và lệnh `TripMatchingRequested` vào cùng transaction
   SQL Server. Outbox gửi lệnh qua RabbitMQ.
3. Matching lưu lệnh theo `proposalId` trong collection MongoDB `matching_jobs`.
   Worker tính giá từ tuyến Goong, tìm tài xế gần nhất đúng loại xe trong **10 km**,
   GPS mới trong **120 giây**, trạng thái `Available`, không nằm trong danh sách
   tài xế đã được đề xuất cho chuyến. Giữ tài xế bằng cập nhật MongoDB nguyên tử.
4. Matching lưu kết quả vào chính document công việc trước khi gửi
   `TripMatchingResult`. Kết quả có tài xế + giá hoặc mã lỗi/không tìm thấy tài xế.
   Trip lưu kết quả rồi gửi `TripChanged` qua SignalR; app gọi GET lấy snapshot.
5. Khách có **90 giây xác nhận cả tài xế và giá**. Snapshot proposal có tên,
   avatar, biển số, tên/ảnh xe, số tiền, quãng đường, thời gian dự kiến cả
   chuyến và `riderApprovalDeadline`.
   Chưa xác nhận thì chưa gửi lời mời cho tài xế.
6. Khách đồng ý: tài xế đó nhận `DriverInvitation` qua kết nối SignalR tài khoản,
   có **30 giây trả lời**, tính từ lúc khách đồng ý.
   Tài xế gọi API Trip để nhận/từ chối, không gọi API Matching để tự giữ xe.
7. Khách chủ động từ chối, tài xế từ chối hoặc tài xế hết 30 giây: Trip
   giải phóng tài xế và tìm tiếp nếu còn lượt. Khách duyệt lại tài xế và giá
   của đề xuất mới. Tối đa **3 đề xuất**, tính cả lần đầu. Gửi lại tin RabbitMQ
   không tính thêm lượt. Hết lượt thì `Failed`. Khách hết 90 giây thì hủy chuyến
   ngay và giải phóng tài xế.
8. Tài xế nhận: Trip lưu `DriverReservationConfirmed` vào Outbox, Matching đổi
   `Reserved` thành `Busy`. Tài xế tham gia kênh chuyến bằng cùng kết nối SignalR.
9. Hủy trước khi đón hoặc trả khách xong: Outbox Trip gửi
   `DriverReservationReleaseRequested`. Consumer Matching xử lý cả `Reserved`
   và `Busy`, chỉ khi **driverId + tripId + proposalId cùng khớp**.
   Trả khách xong giải phóng ngay trong lúc Trip chuyển `PaymentPending`;
   không đợi thanh toán thành công.

## Thời gian giữ và thông điệp trùng/đảo thứ tự

- Trip là nơi quyết định hết hạn. `ReservationSeconds=120` của Matching là mốc
  dự kiến bao phủ 90 + 30 giây, không phải cơ chế tự mở khóa tài xế.
  Matching không tự đổi `Reserved` về `Available` dựa trên đồng hồ cục bộ:
  kết quả và lệnh xác nhận có thể bị chậm trên RabbitMQ.
- Worker Trip kiểm tra deadline mỗi giây; API vẫn từ chối phản hồi đã hết hạn.
- `matching_jobs` có khóa `_id=proposalId`, kết quả gửi lại giữ nguyên tài xế,
  giá và MessageId. Duplicate không tạo thêm lần giữ hay làm mới deadline khách.
- Lệnh release lưu cờ kết thúc trước khi xử lý. Lệnh find/confirm đến muộn
  không mở lại công việc đã release. Worker có lease và revision để không
  đánh dấu hoàn tất nhầm khi có lệnh mới trong lúc đang xử lý.
- Xử lý tìm/định giá lỗi tạm thời: lần đầu + 3 lần thử lại, cách 5 giây;
  hết lượt lưu và gửi `matching_processing_failed`. Lỗi nghiệp vụ báo ngay.
- Kết quả đã lưu nhưng RabbitMQ lỗi: worker gửi lại mỗi 5 giây tới khi gửi được,
  không chọn tài xế mới và không tính thêm lượt tìm. Các lỗi chỉ lưu loại exception.
- Outbox Trip giữ chính sách hiện có: lần đầu + 3 lần thử lại, cách 5 giây.
  Lệnh confirm/release hết lượt ở trạng thái `Failed` phải được vận hành gửi lại
  sau khi sửa hạ tầng; không tự mở khóa vì chưa nhận được lệnh confirm.
- Không thêm heartbeat hay xử lý mất mạng riêng. Giữ bộ lọc GPS quá 120 giây;
  tài xế chưa trả lời lời mời hết 30 giây thì Trip tìm người khác.

## API và GPS cho app

- `PUT /api/matching/drivers/me/availability`: token Driver, bật/tắt nhận chuyến.
  Chỉ đổi trạng thái khi chưa được giữ/chưa chạy chuyến; bật cần tọa độ hiện tại.
- **Mới:** `PUT /api/matching/drivers/me/location`, token Driver,
  body `{ "latitude": 10.78, "longitude": 106.69 }`.
  Chỉ cập nhật GPS và thời điểm, không đổi trạng thái nhận chuyến.
  `Reserved`/`Busy` vẫn gửi được. App tài xế cần gọi định kỳ khi hoạt động.
- Khi tài xế ở trạng thái `Busy`, Matching gửi `DriverGpsUpdated` qua RabbitMQ.
  Trip kiểm tra tài xế, proposal đã nhận và chuyến chưa trả khách/hủy,
  rồi gửi **`DriverLocationUpdated`** riêng cho đúng khách qua SignalR.
  Payload gồm `tripId`, `proposalId`, `driverId`, tọa độ, `recordedAt`.
  App chỉ áp dụng bản tin mới hơn, bỏ bản tin cũ/khác chuyến. GPS là cập nhật
  định kỳ, không đưa vào Outbox nghiệp vụ; lỗi gửi có thể thử lại lần GPS kế tiếp.
- Giữ API hồ sơ tài xế và tìm địa chỉ/geocode/reverse cho Rider/Driver.
- Bỏ các API Matching `proposals/assign`, `quotes`, `reservations/*/accept`,
  `reservations/*/decline`: thao tác nghiệp vụ phải đi qua Trip và RabbitMQ.
- Khách tạo chuyến rồi SubscribeTrip + GET snapshot. Tài xế nhận lời mời qua
  tài khoản, chỉ SubscribeTrip sau khi được Trip xác nhận nhận chuyến.

## Tuyến đường và thời gian tới đón

- `GET /api/trips/{tripId}/route` qua Gateway trả tuyến xem trước đón → đến
  trong lúc duyệt đề xuất. Đúng tài xế được mời có thể lấy tuyến khi lời mời
  còn hiệu lực. App đọc `pickupEtaMinutes` để hiện số phút tới đón, còn quãng
  đường và thời gian dự kiến cả chuyến lấy từ proposal.
- Matching dùng Goong Directions V2 để tính tuyến đón → đến lúc báo giá và
  tuyến tới đón theo đề xuất. Redis giữ cả hai. Nếu cache còn, lúc nhận chuyến
  Matching dùng lại tuyến đã tính. Khi tài xế đón khách, Trip báo Matching đổi
  chặng. Matching tính lại tuyến đang chạy nếu GPS lệch rõ.
- Matching đẩy `TripRoutePreview` cho khách và `TripRoutesUpdated` cho hai bên
  qua RabbitMQ → Trip → SignalR. Khi reconnect, app tải lại snapshot và tuyến.
  API trả 204 khi chưa có tuyến. Giữ trạng thái chuyến và báo giá trong TripDB.
- Cache đường đi có TTL một giờ. Matching gia hạn tuyến đang chạy khi xử lý GPS.
  Khi từ chối đề xuất, giải phóng tài xế và dọn cache tới đón của đề xuất cũ,
  giữ tuyến đón → đến để tìm tiếp. Khi kết thúc chuyến, dọn cache của chuyến.

## Database và chạy kiểm tra

- Avatar tài xế: `driver_profiles.avatarImageKey` lưu key/đường dẫn ảnh, cho phép
  null. Matching tự bổ sung null cho document cũ chưa có field khi khởi động,
  không ghi đè avatar đã có. API hồ sơ trả `avatarImageKey`, Matching gửi
  `driverAvatarImageKey` cùng kết quả đề xuất. Trip lưu
  `driver_avatar_image_key_snapshot` ở cả `trip_proposals` và `trips` qua migration
  `DriverAvatarSnapshot`; GET chuyến trả `driverAvatarImageKeySnapshot`.
  Chưa có ảnh thì frontend dùng icon mặc định. Phần này thêm lưu/trả tham chiếu
  ảnh, không tạo API upload hoặc tự gán ảnh cho tài xế.
- Migration SQL Server `TripVehicleType` thêm `trips.vehicle_type`;
  dữ liệu cũ mặc định `Motorbike`. Không có migration PostgreSQL.
- Migration `RiderAndFareSnapshots` lưu tên/SĐT khách và số liệu báo giá trong
  TripDB. Chỉ tài xế đã nhận và đang phục vụ chuyến được xem SĐT khách.
- Matching đã bỏ sử dụng `fare_quotes`. Báo giá nằm ở `trip_proposals`; bản
  kết quả gửi lại nằm trong `matching_jobs`. Collection cũ có thể còn trên môi
  trường đã nâng cấp.
- MongoDB tạo collection/index `matching_jobs` khi khởi động. Không cần bật
  replica set: inbox/kết quả nằm cùng một document; giữ tài xế là cập nhật
  có điều kiện trên document tài xế, có khóa duy nhất theo chuyến/proposal.
- Backend đã có đường gửi GPS; giao diện tài xế/khách cần nối API và sự kiện
  mới này khi thực hiện phần frontend.
- `dotnet test SwiftRide.sln` chạy các bài kiểm tra thông thường.
- Chạy thêm MongoDB riêng cho test, đặt `SWIFTRIDE_TEST_MONGO` tới server test
  rồi chạy `dotnet test tests/Matching.Tests/SwiftRide.Matching.Tests.csproj`.
  Đặt thêm `SWIFTRIDE_TEST_REDIS` để chạy ca cache và đổi chặng. Mỗi test
  MongoDB tạo và dọn database `matching_test_<uuid>`. Xem [cách chạy test](../README.md#kiểm-thử).
