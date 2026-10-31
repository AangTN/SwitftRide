# Tách hồ sơ tài xế và kích hoạt khách hàng

## Hành vi hiện tại

- Một SĐT chỉ có một user trong Auth. Seed tài xế mới chỉ có Driver.
- Đăng ký khách bằng SĐT chưa tồn tại tạo user + Rider sau khi OTP hợp lệ.
- Đăng ký khách bằng SĐT đã có tài khoản nhưng chưa có Rider: lưu tên chờ trong OTP challenge; xác thực OTP mới thêm Rider và cập nhật tên khách trên cùng user. Không tạo user thứ hai, không sửa hồ sơ tài xế.
- SĐT đã có Rider vẫn bị từ chối đăng ký lại (409). Client không được tự chọn quyền Driver/Admin; đăng ký khách bắt buộc `clientApp=RiderApp`.
- App khách có chức năng đổi tên hiển thị. Chỉ Rider được `PATCH /api/auth/me` với `{ "fullName": "Tên mới" }`; backend kiểm tra cả quyền hiện tại trong DB.
- App tài xế đăng nhập Auth, gọi `GET /api/matching/drivers/me` qua Gateway bằng Bearer token. API yêu cầu Driver, lấy userId từ token, trả driverId, legalName, plateNumber, vehicleName, vehicleType, vehicleImageKey. Auth `/me` và token response không còn chứa `driver`.
- Chưa có API sửa tên tài xế hay quy trình xét duyệt. Tên này không thể sửa qua API đổi tên khách.

## Nâng cấp dữ liệu đang có

Build image Auth/Matching trước. Trong lúc chuyển đổi, dừng hai service để tránh ứng dụng cũ truy cập bảng đã chuyển:

```powershell
docker compose build auth
docker compose build matching
docker compose stop auth matching
pwsh -File scripts/Migrate-DriverProfiles.ps1
docker compose up -d --no-deps auth matching
pwsh -File scripts/Smoke-Test.ps1
```

Script dùng cấu hình `.env` hiện có, không in secret. EF migration đổi tên bảng Auth `driver_profiles` thành `legacy_driver_profiles`, bỏ FK cascade và lưu `legal_name` từ tên gốc tại thời điểm chuyển đổi. Dữ liệu xe và tên gốc vẫn được giữ trong PostgreSQL. Công cụ sao chép từng hồ sơ sang collection Matching `driver_profiles`, kiểm tra nội dung khớp và không ghi đè hồ sơ khác đang có. Nếu gặp xung đột, script dừng; cần đối chiếu nguồn trước khi chạy lại. Không xóa archive, không xóa volume.

Migration chỉ thu hồi Rider trước đây được tự cấp cho đúng tài khoản seed `22222222-2222-2222-2222-222222222222` / `+84900000002`, và thu hồi refresh session cũ của tài khoản đó. Người dùng khác không bị gỡ Rider. Rider được kích hoạt sau migration sẽ được giữ khi chạy seed hoặc khởi động lại. JWT cũ vẫn có thể tồn tại đến hết hạn 15 phút; đổi tên khách vẫn kiểm tra quyền hiện tại trong DB.

Script kiểm chứng và có thể chạy lại; lỗi kết nối giữa hai database không làm mất archive. Matching seed dùng SetOnInsert, không ghi đè tên hoặc xe đã chuyển. Hạ EF migration chỉ khôi phục bản lưu ban đầu, không tự sao chép các chỉnh sửa mới từ MongoDB về PostgreSQL; không tự khôi phục quyền Rider đã thu hồi.

Với database mới hoàn toàn, archive rỗng; seed Auth tạo tài khoản Driver, seed Matching tạo hồ sơ demo tương ứng. Đối với database đã có dữ liệu, chạy script chuyển đổi trước khi khởi động bản Matching mới để hồ sơ demo không được tạo trước hồ sơ thật.

## Kiểm chứng

`scripts/Test-Postgres.ps1` chạy Auth và migration tests trong schema tạm, gồm OTP, giữ userId khi thêm Rider, đổi tên khách, seed lặp lại và phục hồi bản lưu. `dotnet test SwiftRide.sln` còn kiểm tra Matching từ chối anonymous/Rider và chỉ đọc hồ sơ của sub trong token. Flutter tests kiểm tra tên hiển thị tài xế phải đến từ Matching, không fallback sang tên Auth.

`scripts/Test-DriverActivation.ps1` kiểm tra xuyên Gateway bằng một tài khoản Driver tạm: kích hoạt Rider qua OTP, đổi tên khách và đối chiếu tên Matching giữ nguyên. Script dọn đúng user thử mới tạo cùng token/challenge/profile của nó, không sửa tài khoản seed. Chỉ dùng với backend Development có OTP thử nghiệm `123456`.
