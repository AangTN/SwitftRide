# SwiftRide

SwiftRide là đồ án đặt xe với hai ứng dụng Flutter cho hành khách và tài xế. Backend dùng .NET 10 và C# 14, nhận yêu cầu qua YARP Gateway và trao đổi thông điệp nghiệp vụ qua RabbitMQ/MassTransit.

- [Báo cáo đồ án](B%C3%A1o%20c%C3%A1o/Bao_cao_SwiftRide.docx)
- [Hướng dẫn Flutter và Android](apps/mobile/DEVELOPMENT.md)
- [Hướng dẫn triển khai demo VPS](docs/deployment.md)

## Chạy thử

Cần .NET SDK 10 theo [global.json](global.json), PowerShell 7 và Docker Compose với Linux containers. Để chạy hai ứng dụng trên máy, cài thêm Flutter và Node.js. Workflow deploy dùng Flutter **3.47.9**.

Tại thư mục gốc của repo:

```powershell
pwsh -File scripts/Initialize-Dev.ps1
docker compose config --quiet
docker compose up -d --build --wait --wait-timeout 300
pwsh -File scripts/Smoke-Test.ps1
pwsh -File scripts/Start-Mobile.ps1
```

Tham khảo [.env.example](.env.example) để cấu hình bản đồ. Thêm `GOONG_API_KEY` vào `.env` trước khi khởi động backend để Matching tra địa chỉ và tính tuyến. `GOONG_MAPTILES_KEY` dùng khi build bản đồ Flutter. Script khởi tạo giữ các khóa và mật khẩu đã có trong `.env` và `.secrets/`.

| Thành phần | Địa chỉ cục bộ |
| --- | --- |
| App hành khách | [localhost:5173](http://localhost:5173) |
| App tài xế | [localhost:5174](http://localhost:5174) |
| Gateway | [localhost:5000](http://localhost:5000) |
| Swagger | [Auth](http://localhost:5101/swagger) · [Trip](http://localhost:5102/swagger) · [Matching](http://localhost:5103/swagger) · [Payment](http://localhost:5104/swagger) |
| RabbitMQ Management | [localhost:15672](http://localhost:15672) |

`GET /health/live` kiểm tra tiến trình. `GET /health/ready` tại Gateway kiểm tra mức sẵn sàng của cả bốn dịch vụ nghiệp vụ. Compose cục bộ bind các cổng đã công bố vào loopback, còn Redis chỉ phục vụ Matching trong mạng Docker. `docker compose down` dừng stack và giữ dữ liệu trong named volumes.

### Tài khoản demo

| Ứng dụng | Số điện thoại | OTP Development |
| --- | --- | --- |
| Hành khách | `0900000001` | `123456` |
| Tài xế | `0900000002` | `123456` |

Bản demo dùng OTP giả lập và ví có số dư được tạo sẵn cho tài khoản hành khách demo. Tài xế cần bật nhận chuyến và gửi vị trí gần điểm đón trước khi thử đặt xe.

Demo VPS phục vụ [app hành khách](https://13.214.93.224/) và [app tài xế](https://13.214.93.224/driver/).

## Kiến trúc

| Thành phần | Trách nhiệm | Dữ liệu |
| --- | --- | --- |
| Auth | OTP, tài khoản, vai trò, JWT RS256 và phiên đăng nhập | PostgreSQL AuthDB |
| Trip | Chuyến đi, đề xuất tài xế, giá đã chốt, lịch sử và SignalR | SQL Server TripDB |
| Matching & Pricing | Hồ sơ và trạng thái tài xế, GPS, ghép xe, giá dự kiến và định tuyến Goong | MongoDB MatchingDB và Redis |
| Payment | Thanh toán tiền mặt hoặc ví demo, sổ cái và thu nhập tài xế | PostgreSQL PaymentDB |
| API Gateway | Định tuyến API, chuyển tiếp SignalR và tổng hợp readiness | YARP |

Bốn dịch vụ nghiệp vụ được chia thành `Domain`, `Application`, `Infrastructure` và `Api`. Mỗi dịch vụ sở hữu dữ liệu của mình. Compose cục bộ dùng chung một PostgreSQL cho AuthDB và PaymentDB. Trip và Payment ghi outbox cùng giao dịch với thay đổi nghiệp vụ.

## Luồng đặt xe

1. Hành khách chọn điểm đón, điểm đến và loại xe, rồi tạo chuyến bằng `POST /api/trips` kèm `Idempotency-Key`. Gửi lại cùng khóa trả chuyến đã tạo. Yêu cầu mới trong lúc khách còn chuyến đang phục vụ trả `409 active_trip_exists`.
2. Matching tìm tài xế trong bán kính mặc định **10 km** với GPS mới trong **120 giây**. Trip gửi đề xuất gồm tài xế và giá. Khách có **90 giây** để duyệt. Sau khi khách duyệt, tài xế có **30 giây** để phản hồi.
3. Khách hết hạn duyệt thì chuyến bị hủy. Khách từ chối, tài xế từ chối hoặc tài xế hết hạn thì hệ thống tìm tiếp nếu còn lượt. Mỗi chuyến có tối đa **3 đề xuất** đã gắn tài xế.
4. Sau khi trả khách, Trip giải phóng tài xế và gửi yêu cầu thanh toán theo giá đã chốt. Với ví demo, Payment xử lý giao dịch ví. Với tiền mặt, tài xế thu trực tiếp và sổ cái ghi phí nền tảng phải nộp. Chuyến chuyển từ `PaymentPending` sang `Paid` khi thanh toán thành công.

### Tuyến đường và cập nhật thời gian thực

Matching lưu tuyến trong Redis với TTL **1 giờ** và gia hạn tuyến đang dùng khi xử lý GPS. GPS giúp cập nhật vị trí, quãng đường và thời gian còn lại trên tuyến đã có. Khi thiếu tuyến trong cache hoặc có **3 mẫu GPS liên tiếp lệch hơn 60 m**, Matching thử gọi lại Goong Directions. Khoảng cách tối thiểu giữa hai lần thử tính lại là **20 giây**. Sau khi đón khách, hệ thống chuyển sang tuyến của chặng tới điểm trả.

Hub SignalR nằm tại `/api/trips/realtime` qua Gateway. Hai ứng dụng tải lại snapshot và tuyến sau khi kết nối lại.

## API chính

| API qua Gateway | Hành vi |
| --- | --- |
| `POST /api/auth/login/request-otp` và `POST /api/auth/login/verify-otp` | Đăng nhập bằng OTP. Gửi `clientApp` tương ứng `RiderApp` hoặc `DriverApp`. |
| `POST /api/auth/register/request-otp` và `POST /api/auth/register/verify-otp` | Đăng ký hoặc kích hoạt vai trò hành khách. |
| `GET /api/auth/me`, `POST /api/auth/refresh`, `POST /api/auth/logout` | Hồ sơ và quản lý phiên. Refresh trả một cặp token mới. |
| `PUT /api/matching/drivers/me/availability` và `PUT /api/matching/drivers/me/location` | Bật nhận chuyến và cập nhật GPS của tài xế. |
| `GET /api/matching/places/search`, `details`, `geocode`, `reverse` | Tìm và tra cứu địa điểm. |
| `GET /api/trips/current`, `GET /api/trips/{tripId}`, `GET /api/trips/{tripId}/route` | Khôi phục chuyến đang phục vụ, đọc snapshot và tuyến. |
| `POST /api/trips/{tripId}/proposals/{proposalId}/approve` và `reject` | Khách duyệt hoặc từ chối đề xuất. Gửi `stateVersion`. |
| `POST /api/trips/{tripId}/driver-accept` và `driver-reject` | Tài xế phản hồi. Gửi `proposalId` và `stateVersion`. |
| `GET /api/payments/trips/{tripId}`, `GET /api/payments/me/wallet`, `GET /api/payments/drivers/me/earnings` | Tra kết quả thanh toán, số dư ví và thu nhập. |

OTP có hạn 5 phút và chờ 60 giây giữa hai lần gửi lại. Auth cấp quyền JWT theo ứng dụng đăng nhập. Ứng dụng dùng `stateVersion` trong snapshot mới nhất cho các thao tác cập nhật chuyến.

## Kiểm thử

```powershell
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln
```

Lần chạy xUnit mặc định dùng SQLite cho nhiều ca tích hợp. Đặt các biến môi trường sau để bật các ca cần cơ sở dữ liệu thật:

| Biến | Hạ tầng |
| --- | --- |
| `SWIFTRIDE_TEST_POSTGRES` | PostgreSQL kiểm thử, dùng cho Auth và Payment |
| `SWIFTRIDE_TEST_MONGO` | MongoDB kiểm thử, dùng cho Matching |
| `SWIFTRIDE_TEST_REDIS` | Redis kiểm thử cho cache tuyến của Matching, dùng cùng MongoDB |

`scripts/Test-Postgres.ps1` chạy các ca Auth trên PostgreSQL cục bộ bằng schema riêng. Xem [kiểm thử Matching](docs/matching-integration.md) để chuẩn bị MongoDB và Redis cô lập. Kiểm tra HTTP qua Gateway với `pwsh -File scripts/Smoke-Test.ps1`.

Kiểm tra Flutter từ `apps/mobile/`:

```powershell
flutter analyze
flutter test
```

[Báo cáo đồ án](B%C3%A1o%20c%C3%A1o/Bao_cao_SwiftRide.docx) ghi kết quả của phiên bản được kiểm thử tại commit `a49fac4`, gồm **152 ca xUnit** và **52 ca HTTP**.

## CI/CD

- [CI and deploy demo](.github/workflows/deploy.yml) chạy khi push vào `main` hoặc khi kích hoạt thủ công. Workflow chạy xUnit, phân tích và kiểm thử Flutter, build hai bản web, đẩy image lên GHCR theo SHA của commit, rồi triển khai VPS qua SSH.
- [Build Android APKs](.github/workflows/build-apk.yml) chạy thủ công để tạo APK hành khách và tài xế.

Chi tiết cấu hình secrets, Nginx, TLS và phát hành nằm trong [hướng dẫn triển khai](docs/deployment.md).

## Tài liệu kỹ thuật

- [Matching và định tuyến](docs/matching-integration.md) · [Tìm kiếm địa điểm](docs/place-search.md)
- [Trip Outbox](docs/trip-outbox.md) · [Tích hợp Payment](docs/payment-integration.md)
- [Mô hình dữ liệu](docs/database/README.md) · [Chuyển hồ sơ tài xế](docs/driver-profile-migration.md)
