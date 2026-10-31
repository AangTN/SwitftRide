# Giải thích từng trường trong database SwiftRide

Đây là từ điển dữ liệu cho **thiết kế đích trong sơ đồ draw.io/DBML**, không đồng nghĩa toàn bộ schema đã được triển khai.
Phần role đã đổi sang `roles.id` (UUID, PK), `roles.name` (UNIQUE) và `user_roles.role_id` (FK). Hồ sơ xe chuyển sang Matching, proposal và giữ chỗ vẫn là thiết kế đang bàn.

PK = khóa chính; FK = khóa ngoại trong cùng database; UNIQUE = không được trùng; Nullable = được để trống.
Các ID xuyên service chỉ là tham chiếu logic. MongoDB dùng collection/document; không có FK SQL. Thời gian lưu UTC.

## AuthDB

### `users`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã tài khoản UserId dùng chung cho hai app Rider/Driver; một SĐT chỉ tạo một tài khoản. |
| `phone_number` | `varchar(16)` | UNIQUE, Bắt buộc | SĐT Việt Nam chuẩn hóa dạng +84; duy nhất, dùng để đăng ký và đăng nhập OTP. |
| `full_name` | `varchar(150)` | Bắt buộc | Tên hiển thị khách hàng hoặc tên tài khoản trong Auth; không phải tên tài xế do Matching quản lý. |
| `status` | `AuthDB.user_status` | Bắt buộc | Active: được sử dụng tài khoản. Blocked: bị khóa; không đăng nhập/refresh được. |
| `phone_verified_at` | `timestamptz` | Bắt buộc | Thời điểm xác minh SĐT thành công. OTP giả lập Development không chứng minh quyền sở hữu SĐT thật. |
| `created_at` | `timestamptz` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |
| `updated_at` | `timestamptz` | Bắt buộc | Thời điểm cập nhật bản ghi gần nhất, lưu theo UTC. |

### `roles`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã role UUID, là khóa chính ổn định. Đổi tên role không làm thay đổi các liên kết user_roles. |
| `name` | `varchar(20)` | UNIQUE, Bắt buộc | Tên quyền, bắt buộc và UNIQUE: Rider, Driver hoặc Admin. JWT và API vẫn dùng tên quyền để phân quyền. |

### `user_roles`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `user_id` | `uuid` | PK, FK, Bắt buộc | FK tới users.id, cho biết cấp quyền cho tài khoản nào. |
| `role_id` | `uuid` | PK, FK, Bắt buộc | FK tới roles.id, cho biết quyền nào được cấp. Cặp (user_id, role_id) là khóa chính nên không gán trùng cùng quyền cho cùng tài khoản. |

### `otp_challenges`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã challengeId trả về sau khi yêu cầu OTP; client phải gửi đúng mã này khi verify. |
| `phone_number` | `varchar(16)` | Bắt buộc | SĐT cần xác minh. Không có FK tới users vì lúc đăng ký tài khoản chưa được tạo. |
| `purpose` | `AuthDB.otp_purpose` | Bắt buộc | Register: xác minh đăng ký. Login: xác minh đăng nhập. Không dùng challenge của mục đích này cho mục đích kia. |
| `client_app` | `varchar(20)` | Bắt buộc | Loại app đã kiểm tra khi yêu cầu OTP; xác nhận phải khớp và phiên/token lấy phạm vi từ đây. Đăng ký luôn là RiderApp. |
| `registration_full_name` | `varchar(150)` | Nullable | Tên khách chờ đăng ký hoặc kích hoạt Rider; chỉ áp dụng sau khi OTP đúng. Để trống khi đăng nhập. |
| `code_hash` | `varchar(64)` | Bắt buộc | HMAC-SHA256 của OTP gắn với challenge, SĐT và mục đích; không lưu mã OTP gốc. |
| `request_ip` | `varchar(64)` | Bắt buộc | IP nguồn yêu cầu OTP, dùng để đếm giới hạn gửi mã; chỉ tin forwarded IP từ proxy được cấu hình. |
| `attempt_count` | `integer` | Bắt buộc | Số lần nhập sai OTP. Đủ 5 lần theo cấu hình mặc định thì vô hiệu hóa challenge. |
| `created_at` | `timestamptz` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |
| `expires_at` | `timestamptz` | Bắt buộc | Hạn xác minh OTP, mặc định 5 phút từ lúc tạo. Sau hạn, mã đúng cũng không được chấp nhận. |
| `consumed_at` | `timestamptz` | Nullable | Thời điểm OTP được dùng thành công; có giá trị thì không cho dùng lại. |
| `invalidated_at` | `timestamptz` | Nullable | Thời điểm challenge bị vô hiệu hóa, ví dụ do gửi mã mới hoặc nhập sai đủ số lần. |

### `auth_sessions`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | SessionId / claim sid trong JWT. Mỗi lần đăng nhập tạo một phiên; các lần refresh của phiên giữ cùng mã này. |
| `user_id` | `uuid` | FK, Bắt buộc | Mã tài khoản trong users mà bản ghi này thuộc về. |
| `client_app` | `varchar(20)` | Bắt buộc | Loại app lấy từ challenge: RiderApp, DriverApp hoặc AdminApp. Giới hạn JWT đăng nhập và refresh theo role tương ứng mà tài khoản còn có. |
| `device_name` | `varchar(200)` | Nullable | Tên thiết bị do client gửi để nhận biết phiên, ví dụ iPhone; không phải bằng chứng bảo mật. |
| `created_at` | `timestamptz` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |
| `expires_at` | `timestamptz` | Bắt buộc | Hạn tuyệt đối của phiên, mặc định 30 ngày từ đăng nhập. Refresh không kéo dài hạn này. |
| `revoked_at` | `timestamptz` | Nullable | Thời điểm thu hồi phiên. Có giá trị thì mọi refresh token của phiên không dùng tiếp được. |
| `revocation_reason` | `varchar(50)` | Nullable | Lý do thu hồi như Logout, RefreshTokenReuse hoặc UserBlocked. |

### `refresh_tokens`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã bản ghi token trong database; khác chuỗi refresh token gốc mà app nhận. |
| `session_id` | `uuid` | FK, Bắt buộc | FK tới auth_sessions.id, nhóm mọi thế hệ refresh token của một lần đăng nhập. |
| `token_hash` | `varchar(64)` | UNIQUE, Bắt buộc | SHA-256 của refresh token ngẫu nhiên, duy nhất. App giữ token gốc, database chỉ giữ hash. |
| `created_at` | `timestamptz` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |
| `expires_at` | `timestamptz` | Bắt buộc | Hạn refresh token, không vượt quá hạn tuyệt đối của phiên. |
| `used_at` | `timestamptz` | Nullable | Thời điểm token đã được đổi lấy cặp token mới. Gửi lại token đã dùng sẽ thu hồi phiên. |
| `replaced_by_token_id` | `uuid` | FK, Nullable | Mã bản ghi refresh token mới thay thế token này; FK ghép bảo đảm token thay thế vẫn thuộc cùng phiên. |

### Các giá trị trạng thái

- `user_status`: `Active`, `Blocked`.
- `otp_purpose`: `Register`, `Login`.

## TripDB

### `trips`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uniqueidentifier` | PK, FK, Bắt buộc | Mã chuyến đi do Trip tạo khi khách bắt đầu đặt xe. |
| `rider_id` | `uniqueidentifier` | Bắt buộc | Mã tài khoản khách đặt xe trong AuthDB; đây là tham chiếu logic, không phải khóa ngoại xuyên database. |
| `accepted_proposal_id` | `uniqueidentifier` | FK, Nullable | FK tới proposal đã được tài xế nhận, ban đầu để trống. Giá, tài xế và thông tin xe lấy từ proposal này; phải thuộc đúng chuyến. |
| `vehicle_type` | `nvarchar(30)` | Bắt buộc | Loại xe khách yêu cầu; hiện xe máy, có thể mở rộng ô tô 4 hoặc 7 chỗ. |
| `pickup_address` | `nvarchar(500)` | Bắt buộc | Địa chỉ hiển thị của điểm đón khách. |
| `pickup_latitude` | `float` | Bắt buộc | Vĩ độ điểm đón khách, trong khoảng -90 đến 90. |
| `pickup_longitude` | `float` | Bắt buộc | Kinh độ điểm đón khách, trong khoảng -180 đến 180. |
| `destination_address` | `nvarchar(500)` | Bắt buộc | Địa chỉ hiển thị của điểm đến. |
| `destination_latitude` | `float` | Bắt buộc | Vĩ độ điểm đến, trong khoảng -90 đến 90. |
| `destination_longitude` | `float` | Bắt buộc | Kinh độ điểm đến, trong khoảng -180 đến 180. |
| `status` | `TripDB.trip_status` | Bắt buộc | Trạng thái toàn chuyến: đang tìm, chờ khách, chờ tài xế, đang chạy, chờ thanh toán, hoàn tất, hủy hoặc thất bại. |
| `picked_up_at` | `datetimeoffset` | Nullable | Thời điểm tài xế xác nhận đã đón khách. |
| `dropped_off_at` | `datetimeoffset` | Nullable | Thời điểm xác nhận đã trả khách; có thể giải phóng tài xế trước khi thanh toán hoàn tất. |
| `completed_at` | `datetimeoffset` | Nullable | Thời điểm chuyến hoàn tất thành công sau khi thanh toán được xác nhận; không đồng nghĩa chỉ mới trả khách. |
| `created_at` | `datetimeoffset` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |
| `updated_at` | `datetimeoffset` | Bắt buộc | Thời điểm cập nhật bản ghi gần nhất, lưu theo UTC. |

### `trip_proposals`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uniqueidentifier` | PK, Bắt buộc | Mã một lượt đề xuất. Trip tạo trước khi gọi Matching, rồi gửi sang để Matching lưu currentProposalId khi giữ tài xế. |
| `trip_id` | `uniqueidentifier` | FK, Bắt buộc | FK tới trips.id; một chuyến có thể có nhiều lượt đề xuất tài xế. |
| `driver_id` | `uniqueidentifier` | Nullable | Tài xế của lượt đề xuất. Trống khi Searching; phải có trước khi hiển thị cho khách. Không đề xuất lại tài xế cũ trong cùng chuyến. |
| `status` | `TripDB.proposal_status` | Bắt buộc | Bước/kết quả của riêng lượt này: Searching, chờ khách, khách từ chối/hết giờ, chờ tài xế, tài xế từ chối/hết giờ/đồng ý, hủy hoặc không tìm được. |
| `fare_amount` | `decimal(18,2)` | Nullable | Giá tính cho riêng lượt này. Mỗi lượt tính lại; đầu vào giống nhau có thể ra cùng giá. Không sửa báo giá của proposal đã nhận. |
| `currency` | `nvarchar(3)` | Bắt buộc | Mã tiền tệ của số tiền, hiện mặc định VND. |
| `pricing_snapshot` | `nvarchar(max)` | Nullable | JSON lưu quãng đường/thời gian ước tính cùng đơn giá mỗi km/phút, surge, phí phụ, hệ số khuyến mãi và thuế đã dùng; lưu bằng nvarchar(max) trong SQL Server 2022. |
| `driver_name_snapshot` | `nvarchar(150)` | Nullable | Tên tài xế đã hiển thị cho khách trong lượt đề xuất; giữ nguyên để bảo toàn lịch sử. |
| `plate_number_snapshot` | `nvarchar(20)` | Nullable | Biển số xe đã hiển thị trong lượt đề xuất; không đổi theo hồ sơ xe sau này. |
| `vehicle_name_snapshot` | `nvarchar(150)` | Nullable | Tên xe đã hiển thị trong lượt đề xuất, ví dụ Honda Wave Alpha. |
| `vehicle_image_key_snapshot` | `nvarchar(500)` | Nullable | Tham chiếu ảnh xe tại lúc đề xuất; không chứa dữ liệu ảnh nhị phân. |
| `rider_approval_expires_at` | `datetimeoffset` | Nullable | Hạn khách được duyệt tài xế và giá. Khách không duyệt đúng hạn thì hủy chuyến, không tự tìm tiếp. |
| `rider_responded_at` | `datetimeoffset` | Nullable | Thời điểm khách phản hồi. Xem status để biết đồng ý hay từ chối. |
| `driver_response_expires_at` | `datetimeoffset` | Nullable | Hạn tài xế được trả lời lời mời, được đặt sau khi khách đồng ý và Matching xác nhận gia hạn giữ chỗ. |
| `driver_responded_at` | `datetimeoffset` | Nullable | Thời điểm tài xế phản hồi. Nếu status là DriverAccepted thì đây chính là thời điểm tài xế nhận chuyến. |
| `created_at` | `datetimeoffset` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |
| `updated_at` | `datetimeoffset` | Bắt buộc | Thời điểm cập nhật bản ghi gần nhất, lưu theo UTC. |

### `trip_status_history`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uniqueidentifier` | PK, Bắt buộc | Mã định danh duy nhất của bản ghi, dùng làm khóa chính. |
| `trip_id` | `uniqueidentifier` | FK, Bắt buộc | FK tới trips.id, chuyến có thay đổi trạng thái. |
| `from_status` | `TripDB.trip_status` | Nullable | Trạng thái chuyến trước khi đổi; có thể trống với dòng đầu tiên khi tạo chuyến. |
| `to_status` | `TripDB.trip_status` | Bắt buộc | Trạng thái mới của chuyến. |
| `actor_user_id` | `uniqueidentifier` | Nullable | Mã người thực hiện thay đổi; để trống nếu do worker/hệ thống. |
| `created_at` | `datetimeoffset` | Bắt buộc | Thời điểm phát sinh chuyển trạng thái, UTC; dùng để dựng lịch sử chuyến. |

### Các giá trị trạng thái

- `trip_status`: `Requested`, `Matching`, `AwaitingRiderApproval`, `AwaitingDriverResponse`, `DriverAccepted`, `EnRoute`, `PickedUp`, `DroppedOff`, `PaymentPending`, `Paid`, `Cancelled`, `Failed`.
- `proposal_status`: `Searching`, `AwaitingRiderApproval`, `RiderRejected`, `RiderTimedOut`, `AwaitingDriverResponse`, `DriverRejected`, `DriverTimedOut`, `DriverAccepted`, `Cancelled`, `NoDriverFound`.

## MatchingDB

### `driver_profiles`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `_id` | `varchar(36)` | PK, Bắt buộc | UserId tài xế từ Auth, lưu dạng chuỗi UUID; một document cho mỗi tài xế. |
| `legalName` | `varchar(150)` | Bắt buộc | Tên tài xế do Matching quản lý, độc lập với tên hiển thị khách hàng trong Auth; thay đổi qua quy trình hồ sơ tài xế. |
| `plateNumber` | `varchar(20)` | UNIQUE, Bắt buộc | Biển số xe đã chuẩn hóa, bắt buộc và duy nhất. |
| `vehicleName` | `varchar(150)` | Bắt buộc | Tên xe tài xế dùng, ví dụ Honda Wave Alpha. |
| `vehicleType` | `varchar(30)` | Bắt buộc | Loại xe gốc do Matching quản lý; hiện mặc định Motorbike, có thể mở rộng Car4Seats hoặc Car7Seats. |
| `vehicleImageKey` | `varchar(500)` | Nullable | Đường dẫn hoặc object key ảnh xe; không lưu dữ liệu ảnh nhị phân trong document. |
| `createdAt` | `datetime` | Bắt buộc | Thời điểm tạo document MongoDB, lưu theo UTC. |
| `updatedAt` | `datetime` | Bắt buộc | Thời điểm cập nhật document gần nhất, lưu theo UTC. |

### `driver_availability`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `_id` | `varchar(36)` | PK, Bắt buộc | Cùng UserId với driver_profiles._id; mỗi tài xế chỉ có một document trạng thái và cập nhật lại khi thay đổi. |
| `vehicleType` | `varchar(30)` | Bắt buộc | Bản sao loại xe của hồ sơ, phục vụ lọc cùng vị trí/trạng thái khi tìm tài xế. |
| `status` | `MatchingDB.driver_status` | Bắt buộc | Offline: tắt nhận chuyến; Available: rảnh; Reserved: đang giữ chỗ; Busy: đã nhận/đang chạy chuyến. |
| `location` | `json` | Nullable | Object GeoJSON {type: Point, coordinates: [kinh độ, vĩ độ]}; index 2dsphere để tìm gần. Có thể trống lúc chưa cập nhật GPS. |
| `locationUpdatedAt` | `datetime` | Nullable | Lúc nhận vị trí GPS gần nhất. Loại tài xế có vị trí quá cũ khỏi kết quả tìm. |
| `currentTripId` | `varchar(36)` | Nullable | Mã chuyến đang giữ chỗ hoặc đang chạy. Là tham chiếu logic tới TripDB, không có FK xuyên database. |
| `currentProposalId` | `varchar(36)` | Nullable | Mã lượt đề xuất đã tạo trong TripDB, chỉ giữ khi Reserved. Kiểm tra mã này để lệnh đến muộn không gia hạn/giải phóng nhầm lượt. |
| `reservedUntil` | `datetime` | Nullable | Hạn giữ chỗ tài xế; gia hạn khi khách đồng ý để chờ tài xế phản hồi. Không dùng làm hạn giải phóng tài xế Busy và không TTL-xóa document. |
| `updatedAt` | `datetime` | Bắt buộc | Thời điểm cập nhật document gần nhất, lưu theo UTC. |

### Các giá trị trạng thái

- `driver_status`: `Offline`, `Available`, `Reserved`, `Busy`.

## PaymentDB

### `payments`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã giao dịch thanh toán của Payment Service. |
| `trip_id` | `uuid` | UNIQUE, Bắt buộc | Mã chuyến được thanh toán, duy nhất theo phạm vi một payment/chuyến; tham chiếu logic tới TripDB. |
| `rider_id` | `uuid` | Bắt buộc | UserId khách trả tiền, nhận từ Trip qua backend tin cậy. |
| `driver_id` | `uuid` | Bắt buộc | UserId tài xế hưởng tiền, nhận từ proposal được Trip chốt; không lấy tùy ý từ app. |
| `amount` | `numeric(18,2)` | Bắt buộc | Số tiền đã chốt từ proposal được nhận; Payment lưu độc lập để xử lý và kiểm toán. Không dùng float. |
| `currency` | `varchar(3)` | Bắt buộc | Mã tiền tệ của số tiền, hiện mặc định VND. |
| `status` | `PaymentDB.payment_status` | Bắt buộc | Pending: chờ; Processing: đang xử lý; Succeeded: thành công; Failed: thất bại. |
| `idempotency_key` | `varchar(100)` | UNIQUE, Bắt buộc | Khóa duy nhất của một yêu cầu nghiệp vụ; gửi lại cùng yêu cầu không được thực hiện thu/ghi tiền thêm lần nữa. |
| `provider_reference` | `varchar(200)` | Nullable | Mã tham chiếu do cổng thanh toán trả về; để trống nếu chưa có. |
| `failure_code` | `varchar(100)` | Nullable | Mã lỗi xử lý thanh toán, có giá trị khi có lỗi. |
| `paid_at` | `timestamptz` | Nullable | Thời điểm xác nhận thanh toán thành công. |
| `created_at` | `timestamptz` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |
| `updated_at` | `timestamptz` | Bắt buộc | Thời điểm cập nhật bản ghi gần nhất, lưu theo UTC. |

### `ledger_accounts`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã tài khoản kế toán trong sổ cái; khác tài khoản đăng nhập. |
| `code` | `varchar(50)` | Bắt buộc | Loại tài khoản kế toán, ví dụ PAYMENT_CLEARING, DRIVER_PAYABLE hoặc PLATFORM_REVENUE. |
| `owner_user_id` | `uuid` | Nullable | UserId chủ tài khoản kế toán, ví dụ tài xế được trả tiền. Trống nếu là tài khoản chung của nền tảng. |
| `currency` | `varchar(3)` | Bắt buộc | Loại tiền mà tài khoản kế toán theo dõi; mỗi tài khoản gắn một loại tiền. |
| `created_at` | `timestamptz` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |

### `ledger_transactions`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã một nghiệp vụ ghi sổ, nhóm các dòng Nợ/Có cần cân bằng. |
| `payment_id` | `uuid` | FK, Bắt buộc | FK tới payments.id; một payment có thể có nhiều nghiệp vụ như thu tiền rồi hoàn tiền sau này. |
| `type` | `varchar(50)` | Bắt buộc | Loại nghiệp vụ, ví dụ PaymentReceived; Refund/Adjustment chỉ là khả năng mở rộng, chưa triển khai. |
| `idempotency_key` | `varchar(100)` | UNIQUE, Bắt buộc | Khóa duy nhất chống ghi sổ lại cùng một nghiệp vụ khi message/request bị gửi trùng. |
| `created_at` | `timestamptz` | Bắt buộc | Thời điểm tạo bản ghi, lưu theo UTC. |

### `ledger_entries`

| Trường | Kiểu | Ràng buộc | Giải thích |
|---|---|---|---|
| `id` | `uuid` | PK, Bắt buộc | Mã dòng ghi sổ Nợ/Có. |
| `transaction_id` | `uuid` | FK, Bắt buộc | FK tới ledger_transactions.id, cho biết dòng thuộc nghiệp vụ nào. |
| `account_id` | `uuid` | FK, Bắt buộc | FK tới ledger_accounts.id, tài khoản kế toán bị tác động. |
| `side` | `PaymentDB.entry_side` | Bắt buộc | Debit (Nợ) hoặc Credit (Có); tổng hai phía của một nghiệp vụ phải bằng nhau. Không đồng nhất đơn giản với trừ/cộng tiền. |
| `amount` | `numeric(18,2)` | Bắt buộc | Số tiền dương của dòng ghi sổ, không dùng số âm để thay cho phía Debit/Credit. |

### Các giá trị trạng thái

- `payment_status`: `Pending`, `Processing`, `Succeeded`, `Failed`.
- `entry_side`: `Debit`, `Credit`.

## Lưu ý đọc sơ đồ

- `roles.name` vẫn dùng để phân quyền trong JWT; `role_id` chỉ là cách liên kết dữ liệu trong AuthDB.
- `trip_proposals` chứa tài xế và giá từng lượt. `trips.accepted_proposal_id` chỉ proposal được nhận, không lặp giá hoặc driver_id trong trips.
- `currentProposalId` do Trip tạo và gửi sang Matching khi tìm/giữ tài xế; không phải một bảng reservation riêng.
- `reservedUntil` là hạn giữ chỗ của Matching; hạn khách/tài xế phản hồi nằm trong proposal của Trip. Hai bên phối hợp mốc hạn qua API.
- UNIQUE có điều kiện của proposal và MongoDB 2dsphere được mô tả trong DBML notes; khi triển khai cần migration/index và kiểm tra nghiệp vụ tương ứng.
- Bảng kỹ thuật EF `__EFMigrationsHistory` không nằm trong sơ đồ nghiệp vụ: `MigrationId` là mã migration, `ProductVersion` là phiên bản EF tạo migration.
