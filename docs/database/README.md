# Sơ đồ database SwiftRide — thiết kế đề xuất

## Schema đang triển khai

| Kho | Dữ liệu chính |
|---|---|
| AuthDB · PostgreSQL | `users`, `roles`, `user_roles`, `otp_challenges`, `auth_sessions`, `refresh_tokens`. `legacy_driver_profiles` là bản lưu chuyển đổi. |
| TripDB · SQL Server | `trips`, `trip_proposals`, `trip_status_history`, `outbox_messages`. |
| MatchingDB · MongoDB | `driver_profiles`, `driver_availability`, `matching_jobs`. |
| PaymentDB · PostgreSQL | `payments`, `ledger_accounts`, `ledger_transactions`, `ledger_entries`, `outbox_messages`. |
| Redis · Matching | Cache polyline và thời gian tới đón theo chuyến/đề xuất. |

- Trip lưu giá, quãng đường, thời gian dự kiến và thông số tính giá trong
  `trip_proposals`. `trips` giữ giá đã chốt và thông tin khách tại lúc đặt.
  Migration `RiderAndFareSnapshots` bổ sung các trường này. Thời hạn mặc định
  là 90 giây cho khách và 30 giây cho tài xế, tối đa 3 đề xuất.
- Matching dùng `matching_jobs` để xử lý lệnh và gửi lại kết quả theo
  `proposalId`. Code hiện tại đã bỏ sử dụng `fare_quotes`. Dữ liệu cũ có
  thể còn trên môi trường đã nâng cấp.
- Payment dùng sổ cái nội bộ cho ví demo. `ledger_accounts` xác định tài khoản
  sổ cái; số dư được tính từ `ledger_entries`. Chưa có bảng liên kết thẻ/ngân hàng.
- Migration, mapping EF và code MongoDB là nguồn đối chiếu khi cần từng cột
  chính xác. Các file `*.proposed.*`, ảnh và từ điển bên dưới là tài liệu thiết
  kế, có thể khác schema triển khai.

## File draw.io chỉnh sửa được

Mở `swiftride.proposed.drawio` bằng https://app.diagrams.net: **Device → Open Existing Diagram**, hoặc **File → Open From → Device**. Không cần paste DBML.

File có 6 tab:

1. Tổng quan service và database: ghi rõ service sở hữu DB, công nghệ và danh sách bảng/collection.
2. Toàn bộ cấu trúc 4 database trên một canvas, gồm đầy đủ cột.
3. Auth Service / AuthDB — PostgreSQL.
4. Trip Service / TripDB — SQL Server.
5. Matching & Pricing Service / MatchingDB — MongoDB.
6. Payment Service / PaymentDB — PostgreSQL.

Hình xem nhanh: `swiftride.overview.png`. Trong draw.io, dùng các tab bên dưới để chọn trang và zoom vào từng bảng. Tất cả hộp/cột/đường nối đều chỉnh sửa được, không phải ảnh chèn vào file.

**Giải thích từng cột:** file draw.io ghi chú tiếng Việt ngay bên phải từng trường, không cần rê chuột. Chọn tab của từng service rồi zoom để đọc rõ. Ngoài ra có thể mở `data-dictionary.vi.md` để xem từng trường dạng bảng; dữ liệu chú thích cũng có trong `field-descriptions.vi.json`.

Role đã cập nhật: `roles.id` là UUID/PK, `roles.name` là UNIQUE; bảng `user_roles` có khóa chính ghép `(user_id, role_id)` và FK `role_id → roles.id`. Trong JWT vẫn trả tên quyền, không đổi contract phân quyền sang UUID. Migration mới chuyển các liên kết tên cũ sang ID trước khi bỏ `role_name`, giữ tài khoản và các quyền đã gán.

Đường liền trong sơ đồ chi tiết là FK nội bộ database SQL. Đường đứt giữa hai collection Matching là liên kết logic theo UserId, không có FK MongoDB. Các ID xuyên database được ghi ở field notes trong DBML và phần giải thích dưới đây, không phải FK vật lý. PK = khóa chính, FK = khóa ngoại, UQ = duy nhất, dấu `?` = nullable. Tất cả thời gian UTC.

Sơ đồ ghi lại phương án thiết kế tại thời điểm vẽ. Trip/proposal đã triển khai theo schema ở trên.

## File DBML

**Sơ đồ đầy đủ 4 database:** `swiftride.proposed.dbml`. Copy toàn bộ file này vào dbdiagram.io để xem 15 bảng/collection trong 4 nhóm:

| Nhóm | Công nghệ | Bảng/collection |
|---|---|---|
| AuthDB | PostgreSQL | users, roles, user_roles, otp_challenges, auth_sessions, refresh_tokens |
| TripDB | SQL Server | trips, trip_proposals, trip_status_history |
| MatchingDB | MongoDB | driver_profiles, driver_availability |
| PaymentDB | PostgreSQL | payments, ledger_accounts, ledger_transactions, ledger_entries |

Bảng này liệt kê các hộp trong file thiết kế, còn danh sách đang chạy nằm ở đầu tài liệu.

`auth-payment.proposed.dbml` và `trip-matching.proposed.dbml` là hai sơ đồ nhỏ nếu muốn xem từng cặp database. Không paste thêm chúng sau file tổng hợp vì sẽ trùng bảng.

Mỗi database SQL còn có bảng kỹ thuật `__EFMigrationsHistory` với `MigrationId`, `ProductVersion` do EF quản lý. Các bảng kỹ thuật này không nằm trong 15 bảng nghiệp vụ của diagram. RabbitMQ không phải database nghiệp vụ, không có nhóm bảng trong file này.

## Liên kết giữa các database

- User ID từ Auth được dùng cho rider, driver, session và chủ tài khoản kế toán; chỉ các FK nằm **trong AuthDB** mới là FK vật lý.
- Matching `driver_profiles._id` và `driver_availability._id` cùng là UserId tài xế.
- Matching `currentTripId` / `currentProposalId` tham chiếu logic tới TripDB.
- Payment `trip_id` là ID chuyến; `rider_id`, `driver_id` là ID tài khoản. Payment giữ số tiền nhận từ proposal đã được chấp nhận để xử lý thanh toán độc lập.
- `legalName` là tên tài xế do Matching quản lý, không đồng bộ từ tên khách trong Auth.
- Diagram chỉ vẽ FK bên trong từng database SQL; các tham chiếu xuyên database và giữa collection được ghi trong notes, không tạo FK giả.

## Phần TripDB / MatchingDB

File `trip-matching.proposed.dbml` ghi lại phương án thiết kế trước đây. Đối chiếu schema đang triển khai ở đầu tài liệu.

## Cách vẽ trên dbdiagram.io

1. Mở https://dbdiagram.io/d và tạo diagram.
2. Copy toàn bộ nội dung file DBML vào editor bên trái.
3. Diagram gồm hai nhóm: `TripDB_SQL_Server` và `MatchingDB_MongoDB`.

Tên `TripDB` và `MatchingDB` được dùng làm namespace/schema trong công cụ để phân nhóm hai database độc lập. Đây không phải hai schema trong cùng database triển khai thật.

MongoDB được mô phỏng bằng các hộp bảng để thể hiện field. `location` là object GeoJSON nhúng; Date là BSON Date; các kiểu chuỗi trong sơ đồ chỉ thể hiện định dạng/giới hạn mong muốn. MongoDB không có khóa ngoại SQL. Không dùng SQL export của sơ đồ tổng hợp để triển khai hệ thống.

## Nội dung phương án thiết kế

- TripDB: `trips`, `trip_proposals`, `trip_status_history`.
- MatchingDB: `driver_profiles`, `driver_availability`.
- Mỗi proposal tính giá lại, lưu giá và snapshot tại proposal; trip chỉ tham chiếu proposal đã nhận.
- Không còn `quote_id`, collection `fare_quotes`, hoặc mã reservation riêng trong phương án này.
- Proposal được Trip tạo trước khi gọi Matching. Matching lưu ID đó trong `currentProposalId`.
- Các đường FK trong diagram chỉ nằm bên trong TripDB. Liên kết MongoDB và liên kết xuyên service được ghi trong field notes, không vẽ thành FK vật lý.
- `driver_profiles._id` và `driver_availability._id` dùng cùng UserId từ Auth; đây là quan hệ logic một-một.

## Giới hạn thể hiện

- Khóa ngoại ghép từ `(trips.id, trips.accepted_proposal_id)` tới `(trip_proposals.trip_id, trip_proposals.id)` bảo đảm proposal được chọn thuộc đúng chuyến. Việc tạo chuyến không bị vòng chèn dữ liệu vì `accepted_proposal_id` ban đầu null.
- Các unique index có điều kiện cho proposal, MongoDB `2dsphere`, kiểm tra trạng thái, tính bất biến của báo giá và xử lý nguyên tử/timeout được ghi bằng notes. Khi triển khai phải viết migration/index và logic tương ứng.
- Worker không dùng TTL để xóa tài xế; khi hết hạn chỉ giải phóng giữ chỗ, không xóa document.
- Thời gian khách/tài xế phản hồi chưa phải ràng buộc schema; bản thiết kế cũ dùng 30/15 giây; code hiện tại mặc định 90/30 giây.

## Cấu trúc đang chạy khác gì?

Trip giữ giá và snapshot đã chốt trực tiếp trên `trips`; `quote_id` vẫn có
trong schema Trip hiện tại. MongoDB thêm `matching_jobs`, còn Redis giữ tuyến
tạm thời. Xem [luồng Matching–Trip](../matching-integration.md) để đối chiếu
hành vi hiện tại.

## Hồ sơ tài xế đã triển khai

Matching đã có collection driver_profiles với legalName độc lập; Auth chỉ giữ tên hiển thị khách. Xem [hướng dẫn chuyển dữ liệu](../driver-profile-migration.md).
