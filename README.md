# SwiftRide

Deploy bản demo lên VPS bằng Docker, Nginx và GitHub Actions: [hướng dẫn](docs/deployment.md).

Backend .NET 10 gồm Auth, Trip, Matching & Pricing, Payment và API Gateway YARP. Mỗi service có Domain / Application / Infrastructure / Api. `ServiceDefaults` chỉ chia sẻ cấu hình hạ tầng; không chia sẻ domain entity hoặc database.

## Phạm vi đã triển khai

- Auth: đăng ký/đăng nhập SĐT + OTP, kích hoạt Rider cho tài khoản đã có, đổi tên khách, JWT RS256, refresh rotation, phát hiện reuse, phiên độc lập, logout, `/me`, nhiều role. Hồ sơ tài xế do Matching quản lý.
- EF migrations cho Auth/PostgreSQL, Trip/SQL Server, Payment/PostgreSQL; collection/index và seed trạng thái Driver cho Matching/MongoDB.
- RabbitMQ/MassTransit, health/readiness, correlation ID, lỗi Problem Details, OpenAPI/Swagger Development, Docker Compose.
- Có hai app Flutter khách hàng/tài xế: đăng nhập OTP, đăng ký khách hàng, hồ sơ và đăng xuất; chạy thử web trên PC hoặc Android. Xem [hướng dẫn app](apps/mobile/DEVELOPMENT.md).
- Matching & Pricing có API cập nhật vị trí/trạng thái tài xế, tìm và khóa tài xế gần nhất, chấp nhận/từ chối khóa, tra địa chỉ và tạo báo giá theo tuyến Goong. Trip chưa tích hợp vòng đời proposal với Matching; Auth, Trip và Payment vẫn do các nhóm tương ứng sở hữu.
- Chưa có API nghiệp vụ chuyến đi/thanh toán, cổng SMS, upload ảnh, ví, hoàn tiền, Saga hoặc Outbox. Schema sổ cái có sẵn; chưa có nghiệp vụ ghi sổ hay tính phí nền tảng.

## Chạy hai app trên PC

Khi backend đang chạy, dùng Flutter stable và Node.js:

```powershell
pwsh -File scripts/Start-Mobile.ps1
```

Mở khách hàng tại `http://localhost:5173`, tài xế tại `http://localhost:5174`. Tài khoản demo lần lượt `0900000001` và `0900000002`; OTP Development `123456` (chưa gửi SMS thật). Chạy lại với `-SkipBuild` nếu chưa sửa source. Chi tiết chạy Android qua USB và xuất hai APK ở [hướng dẫn app](apps/mobile/DEVELOPMENT.md).

## Bản demo công khai

- Khách hàng: https://13.214.93.224/
- Tài xế: https://13.214.93.224/driver/
- OTP demo: `123456`

Các URL trên là bản demo dùng OTP giả lập; mã OTP cố định và không xác minh quyền sở hữu số điện thoại.

## Yêu cầu

- .NET SDK 10.0.300 trở lên trong nhánh 10.0, PowerShell 7 (`pwsh`).
- Docker Desktop chạy Linux containers, Docker Compose. Khuyến nghị dành đủ RAM cho SQL Server cùng các database/broker.
- Các port local: 5000, 5101–5104, 5432, 1433, 27017, 5672, 15672 chưa được sử dụng.

## Chạy toàn bộ bằng Docker

Tại thư mục repository:

```powershell
pwsh -File scripts/Initialize-Dev.ps1
docker compose config --quiet
docker compose up -d --build --wait --wait-timeout 300
docker compose ps
```

Script tạo `.env` chứa mật khẩu riêng cho từng service, HMAC secret và cặp RSA trong `.secrets/`. Chạy lại giữ nguyên secrets. Các file này bị Git/Docker build context bỏ qua. Chỉ Auth container được mount private key; các service khác chỉ nhận public key.

Compose này dành cho **Development**, tự migrate và seed khi khởi động. Không dùng nguyên cấu hình này để triển khai production. Database và RabbitMQ dùng named volumes. `docker compose down` giữ dữ liệu; không thêm `-v` nếu muốn giữ dữ liệu.

| Thành phần | Địa chỉ |
|---|---|
| Gateway | http://localhost:5000 |
| Auth Swagger | http://localhost:5101/swagger |
| Trip Swagger | http://localhost:5102/swagger |
| Matching Swagger | http://localhost:5103/swagger |
| Payment Swagger | http://localhost:5104/swagger |
| RabbitMQ Management | http://localhost:15672 |

Swagger Matching hiển thị các API nghiệp vụ bên dưới; các API Trip/Payment chưa được triển khai. RabbitMQ đăng nhập bằng user `swiftride` và mật khẩu trong `.env`.

`GET /health/live` kiểm tra process; `GET /health/ready` kiểm tra database và broker tại các service. Gateway readiness kiểm tra readiness cả bốn backend.

## Matching & Pricing API

Các API dưới đây đi qua Gateway (`http://localhost:5000`) và yêu cầu JWT:

| API | Quyền | Hành vi |
|---|---|---|
| `PUT /api/matching/drivers/me/availability` | Driver | Bật/tắt nhận chuyến khi chưa bận; bật cần tọa độ hiện tại |
| `PUT /api/matching/drivers/me/location` | Driver | Cập nhật GPS, cả lúc đang được giữ hoặc chạy chuyến; không đổi trạng thái nhận chuyến |
| `GET /api/matching/places/geocode?address=...` | Rider, Driver | Tra địa chỉ thành tọa độ bằng Goong |
| `GET /api/matching/places/search?input=...` | Rider, Driver | Gợi ý địa điểm bằng Goong Places Autocomplete |
| `GET /api/matching/places/details?placeId=...` | Rider, Driver | Địa chỉ và tọa độ đúng địa điểm đã chọn |
| `GET /api/matching/places/reverse?latitude=...&longitude=...` | Rider, Driver | Tra địa chỉ từ tọa độ bằng Goong |

Khách tạo chuyến qua Trip với điểm đón/đến và `vehicleType` (`Motorbike`, `Car4Seats`, `Car7Seats`). Trip gửi lệnh tìm tài xế/định giá qua RabbitMQ; app không gọi trực tiếp Matching để giữ tài xế hoặc tạo giá chính thức. Matching tìm trong 10 km, loại GPS quá 120 giây. Khách có 90 giây xác nhận cả tài xế và giá, sau đó tài xế có 30 giây trả lời. Trip quyết định hết hạn và gửi lệnh giải phóng qua Outbox. Giá áp dụng `p = ((cd*d + ct*t)*s + cm)*eta*(1+T)`; dùng decimal, làm tròn VND lên tại nửa đồng và lưu snapshot theo proposal.

Đặt `GOONG_API_KEY` trong `.env` để bật tra địa chỉ và định tuyến. Compose chỉ chuyển khóa cho Matching backend; không đưa khóa này vào Flutter. Khi chưa cấu hình, API Goong trả lỗi `503 goong_not_configured` thay vì ước lượng tuyến/giá giả. Có thể chỉnh bán kính tìm kiếm, thời hạn vị trí/khóa/báo giá và đơn giá trong cấu hình `Matching`/`Pricing` của Matching.

Tìm kiếm, chi tiết địa điểm, geocode/reverse và định tuyến đều dùng Goong V2 để trả địa chỉ theo đơn vị hành chính mới. Các gợi ý có sẵn cũng đã cập nhật; chi tiết trong [docs/place-search.md](docs/place-search.md).

Matching chỉ sở hữu hồ sơ, vị trí/trạng thái tài xế, khóa, báo giá và công việc tích hợp MongoDB; không đọc/ghi trực tiếp TripDB. Trip có API xác nhận/nhận/từ chối chuyến, phát lệnh xác nhận/giải phóng tài xế qua Outbox. Matching chuyển GPS cho Trip để báo đúng khách qua SignalR. Chi tiết API, retry, migration và phần frontend cần nối: [docs/matching-integration.md](docs/matching-integration.md).

## Chạy service bằng .NET trên máy

Khởi động dependency trước:

```powershell
docker compose up -d postgres sqlserver sqlserver-init mongo rabbitmq
```

Sau đó chạy mỗi lệnh trong một terminal riêng:

```powershell
pwsh -File scripts/Run-Service.ps1 Auth
pwsh -File scripts/Run-Service.ps1 Trip
pwsh -File scripts/Run-Service.ps1 Matching
pwsh -File scripts/Run-Service.ps1 Payment
pwsh -File scripts/Run-Service.ps1 Gateway
```

Không chạy đồng thời bản Docker và bản local của cùng service do trùng port. `Run-Service.ps1` tự nạp `.env`, cấu hình URL, migration và seed. Để thiết bị thật truy cập, cần chủ động cấu hình bind địa chỉ LAN và firewall; mặc định chỉ mở localhost. Android emulator truy cập máy host qua `10.0.2.2:5000` khi gateway đã được cấu hình bind phù hợp.

## Tài khoản Development

| SĐT | Quyền | Thông tin |
|---|---|---|
| `0900000001` | Rider | Rider Demo |
| `0900000002` | Driver | Driver Demo; `59A1-12345`; Honda Wave Alpha; hồ sơ ở Matching |
| `0900000003` | Admin | Admin Demo |

Driver seed dùng ID `22222222-2222-2222-2222-222222222222`, trạng thái ban đầu Offline. Ảnh mẫu có object key `seed/vehicle.svg`, xem qua `/seed/vehicle.svg` ở Gateway. Seed không tạo token, không ghi đè trạng thái nhận chuyến đã có và không cấp quyền vào tài khoản khác trùng SĐT.

**OTP Development cố định `123456`**, không trả mã trong response và không hiện mã trong UI. Phải yêu cầu challenge trước. Cơ chế này không xác minh quyền sở hữu SĐT thật.

## Thử API qua Gateway

### Đăng ký

`POST /api/auth/register/request-otp`

```json
{"fullName":"Nguyễn Văn A","phoneNumber":"0912345678"}
```

Response:

```json
{"challengeId":"<UUID>","expiresInSeconds":300,"retryAfterSeconds":60}
```

`POST /api/auth/register/verify-otp`

```json
{"challengeId":"<UUID>","otp":"123456","clientApp":"RiderApp","deviceName":"Android demo"}
```

Chỉ lúc xác minh thành công mới tạo user (nếu SĐT chưa tồn tại), gán Rider, tiêu thụ challenge và cấp phiên/token trong cùng transaction. Nếu SĐT đã có user nhưng chưa có Rider, cùng luồng này bổ sung Rider và tên hiển thị khách cho user hiện có. SĐT đã có Rider trả 409. Request đăng ký không nhận role từ client; bước xác minh đăng ký chỉ nhận `clientApp=RiderApp`.

### Đăng nhập

`POST /api/auth/login/request-otp`

```json
{"phoneNumber":"0900000002","clientApp":"DriverApp"}
```

`POST /api/auth/login/verify-otp`

```json
{"challengeId":"<UUID>","otp":"123456","clientApp":"DriverApp","deviceName":"Driver phone"}
```

Khi yêu cầu OTP đăng nhập, `clientApp` là bắt buộc: `RiderApp` cần Rider, `DriverApp` cần Driver, `AdminApp` cần Admin. Auth kiểm tra tài khoản tồn tại, Active và role trước khi tạo/gửi mã. Thiếu Rider trả 403 `rider_activation_required` để hướng dẫn đăng ký/kích hoạt khách; thiếu Driver/Admin trả 403 `role_required`.

Challenge lưu `client_app`; bước xác nhận phải khớp loại app đã lưu, không kiểm tra lại role đăng nhập. Phiên lấy loại app từ challenge và JWT chỉ mang role tương ứng mà tài khoản còn có, kể cả khi tài khoản có nhiều role. Refresh giữ phạm vi của phiên và lấy quyền hiện tại từ DB. `user.roles` trong response và `/me` vẫn mô tả toàn bộ quyền tài khoản, không phải quyền của JWT. Backend nghiệp vụ kiểm tra role trong JWT và tài nguyên. Luồng đăng ký/kích hoạt Rider riêng luôn tạo challenge cho `RiderApp`.

Đăng ký/đăng nhập thành công trả:

```json
{
  "accessToken":"<JWT>",
  "refreshToken":"<random-token>",
  "tokenType":"Bearer",
  "accessTokenExpiresAt":"<UTC timestamp>",
  "refreshTokenExpiresAt":"<UTC timestamp>",
  "user":{"id":"<UUID>","phoneNumber":"+84900000002","fullName":"Driver Demo","roles":["Driver"]}
}
```

### Hồ sơ, refresh, logout

- `GET /api/auth/me`: header `Authorization: Bearer <accessToken>`.
- `PATCH /api/auth/me`: Bearer + quyền Rider, body `{"fullName":"Tên hiển thị khách"}`; không sửa tên tài xế.
- `GET /api/matching/drivers/me`: Bearer + quyền Driver, trả hồ sơ có `legalName`, xe và biển số từ Matching. Token/Auth `/me` không còn chứa hồ sơ tài xế.
- `POST /api/auth/refresh`: body `{"refreshToken":"<refreshToken>"}`; lưu lại **cả hai token mới**, bỏ token cũ.
- `POST /api/auth/logout`: header Bearer; thu hồi phiên `sid` của token, trả 204.

Access JWT có hạn 15 phút; phiên/refresh tối đa 30 ngày kể từ đăng nhập, không kéo dài khi refresh. JWT dùng issuer `swiftride-auth`, audience `swiftride-api`, claims `sub`, `sid`, `jti`, `roles`, `iat`, `nbf`, `exp`.

Refresh token chỉ lưu hash SHA-256. Mọi thế hệ trong phiên cùng `SessionId`. Dùng lại token đã dùng sẽ thu hồi cả phiên; token thế hệ mới nhất cũng không refresh được. Chỉ phiên đó bị ảnh hưởng. Client phải gom các request refresh đồng thời thành một request; không tự retry bằng token cũ khi response refresh bị mất.

Logout/reuse chặn refresh ngay. Access JWT đã cấp vẫn hợp lệ đến hết hạn; chưa có blacklist. Khi refresh, Auth kiểm tra lại trạng thái user và lấy role hiện tại từ database.

### OTP và giới hạn

- Hạn 5 phút, dùng một lần, sai đủ 5 lần khóa challenge.
- Gửi lại sau 60 giây theo cùng SĐT/mục đích như trước, vô hiệu hóa challenge cũ cùng SĐT/mục đích (kể cả khác app).
- Tối đa 5 lần tạo/gửi OTP thành công cho mỗi cặp IP + SĐT chuẩn hóa trong 1 giờ gần nhất, cộng chung các app và mục đích. Request bị từ chối không tăng bộ đếm. Không còn hạn mức riêng theo SĐT hoặc riêng theo IP; không khóa tài khoản do người khác nhập sai OTP.
- Các giá trị cấu hình qua `Otp__LifetimeSeconds`, `Otp__MaxAttempts`, `Otp__ResendSeconds`, `Otp__RequestsPerIpPhonePerHour`; token qua `Tokens__AccessMinutes`, `Tokens__SessionDays`.
- Migration `BindOtpToClientApp` thêm `otp_challenges.client_app` và index cho cặp IP + SĐT. Challenge cũ chưa có loại app sẽ bị vô hiệu hóa, giữ lịch sử để tính hạn mức. JWT đã cấp trước cập nhật vẫn giữ quyền cũ đến khi hết hạn; đăng nhập/refresh sau cập nhật nhận JWT giới hạn theo app.
- Auth tin forwarded IP chỉ từ proxy đã cấu hình. Compose đặt Gateway ở `172.29.0.10`; nếu subnet trùng mạng máy, đổi subnet, IP Gateway và `Gateway__TrustedProxy` cùng nhau.
- Lỗi theo Problem Details: 400 dữ liệu/challenge/OTP không hợp lệ, 401 token không hợp lệ, 403 tài khoản bị khóa/thiếu quyền, 404 cần đăng ký, 409 SĐT đã tồn tại, 429 giới hạn OTP, 503 chưa có bộ gửi OTP.

Ngoài Development, bật OTP cố định hoặc seed sẽ làm ứng dụng từ chối khởi động. Nếu tắt OTP giả lập mà chưa có provider SMS, API OTP trả 503. `IOtpSender` là điểm tích hợp SMS thật; private key, database credentials và HMAC secret phải được cấp từ cấu hình bảo mật.

## Dữ liệu và migration

| Database | Bảng/collection |
|---|---|
| AuthDB | users, roles, user_roles, otp_challenges, auth_sessions, refresh_tokens; legacy_driver_profiles là bản lưu chuyển đổi |
| TripDB | trips, trip_status_history |
| MatchingDB | driver_profiles, driver_availability, fare_quotes |
| PaymentDB | payments, ledger_accounts, ledger_transactions, ledger_entries |

`roles` dùng `id` UUID làm khóa chính và tên `name` duy nhất. `user_roles` liên kết bằng `(user_id, role_id)`. Migration `AddRoleIds` chuyển dữ liệu tên role cũ sang ID, không tạo lại tài khoản hoặc xóa quyền đã gán. JWT/API vẫn trả tên role. Sơ đồ và giải thích từng cột nằm trong `docs/database/`; những thay đổi Trip/Matching trong sơ đồ vẫn là thiết kế đề xuất.

Auth giữ tên hiển thị khách hàng (`users.full_name`). Matching sở hữu tên tài xế riêng (`driver_profiles.legalName`), xe, vị trí/trạng thái và báo giá. Đổi tên khách không cập nhật tên tài xế. Tọa độ GeoJSON theo thứ tự **longitude, latitude**, có geospatial index. Giá Mongo dùng Decimal128; SQL dùng decimal/numeric. Quote hết hạn vẫn được giữ để tra cứu, không có TTL tự xóa.

Database cũ cần chạy [hướng dẫn chuyển hồ sơ tài xế](docs/driver-profile-migration.md) khi nâng cấp. Migration giữ bản lưu `legacy_driver_profiles` trong AuthDB; công cụ sao chép và đối chiếu dữ liệu sang MongoDB. Không xóa volume để nâng cấp.

Trip có các trường snapshot tên tài xế/xe khi nhận chuyến. Payment lưu cả rider/driver ID, idempotency key và sổ cái; không lưu thông tin thẻ. Chưa triển khai API ghi sổ, vì vậy việc kiểm tra cân bằng Debit/Credit sẽ thuộc transaction nghiệp vụ khi phát triển Payment.

Tạo/apply migration thủ công (PowerShell 7):

```powershell
dotnet tool restore
. ./scripts/Import-DevEnvironment.ps1
dotnet ef database update --project src/AuthService/Infrastructure --startup-project src/AuthService/Api
dotnet ef database update --project src/TripService/Infrastructure --startup-project src/TripService/Api
dotnet ef database update --project src/PaymentService/Infrastructure --startup-project src/PaymentService/Api
```

Database init scripts chỉ chạy lúc tạo volume mới. Đổi mật khẩu trong `.env` sau khi đã có volume không tự đổi mật khẩu database; cần đổi credential tương ứng trong database. Không xóa volume để xử lý nếu cần giữ dữ liệu.

## Kiểm thử

```powershell
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln
```

Mặc định HTTP integration tests dùng SQLite tạm, khóa ký riêng cho từng fixture; không ghi vào dữ liệu dev. Các kiểm thử cạnh tranh PostgreSQL được đánh dấu skip khi chưa có connection string.

Chạy lại toàn bộ Auth tests trên PostgreSQL thật, gồm kiểm thử cạnh tranh:

```powershell
docker compose up -d postgres
pwsh -File scripts/Test-Postgres.ps1
```

Tests tạo schema riêng ngẫu nhiên trong AuthDB rồi dọn chính schema đó; không xóa dữ liệu ứng dụng. Kiểm tra migration, registration đồng thời, refresh đồng thời và OTP cooldown dùng PostgreSQL advisory transaction locks.

Khi cả hệ thống đã chạy:

```powershell
pwsh -File scripts/Smoke-Test.ps1
```

Smoke test dùng Driver seed, kiểm tra readiness, login, `/me`, rotation, reuse revocation và logout qua Gateway. Lưu ý cooldown/rate limit nếu chạy nhiều lần liên tiếp.

## Xử lý lỗi khởi động

- Kiểm tra Docker Engine hoạt động bằng `docker version`, sau đó `docker compose ps` và `docker compose logs <service>`.
- Nếu Docker Desktop lỗi trước khi Engine sẵn sàng (ví dụ lỗi socket `dockerInference`), cần khôi phục Docker Desktop trước khi chạy Compose; lỗi này nằm ngoài source backend.
- Khi kết nối database/RabbitMQ hỏng, readiness trả lỗi; liveness chỉ phản ánh process còn chạy.
- Secret và token không được đưa vào issue/log công khai. `docker compose config` không có `--quiet` sẽ in cả cấu hình chứa secret.
