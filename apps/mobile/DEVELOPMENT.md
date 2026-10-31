# SwiftRide — app khách hàng và tài xế

Hai ứng dụng Flutter dùng chung source và design system, có hai entrypoint:

- `lib/main_rider.dart`: khách hàng; Android ID `vn.swiftride.rider`.
- `lib/main_driver.dart`: tài xế; Android ID `vn.swiftride.driver`.

Đã có đăng nhập số điện thoại → OTP, đăng ký khách hàng (họ tên + SĐT → OTP), gửi lại OTP theo thời gian backend trả về, lưu/khôi phục phiên, refresh token một lần cho các yêu cầu đồng thời, hồ sơ từ API, đăng xuất. App tài xế chỉ chấp nhận user có role Driver; không có chức năng tự cấp quyền tài xế. Đặt xe, bản đồ và nhận chuyến chưa thuộc bản này.

Tài xế seed chỉ có Driver. Muốn dùng app khách, chọn Đăng ký tài khoản với cùng SĐT và nhập tên hiển thị; OTP hợp lệ sẽ thêm Rider vào cùng user. Nếu thử đăng nhập app khách bằng tài khoản Driver-only, app hướng sang form kích hoạt này. Tên khách có thể đổi ở màn tài khoản; tên và xe của tài xế được lấy từ Matching, hoàn toàn độc lập. Chi tiết chuyển dữ liệu: [driver-profile-migration.md](../../docs/driver-profile-migration.md).

## Chạy thử trên PC

Cần Flutter stable (đã chuẩn bị trên máy này ở `D:/Tools/flutter`), Node.js và backend Gateway port 5000. Không cần Android Studio hoặc giả lập cho bản web.

Tại thư mục gốc repository:

```powershell
pwsh -File scripts/Start-Mobile.ps1
```

Script build **lần lượt** hai app rồi mở server preview:

- Khách hàng: http://localhost:5173
- Tài xế: http://localhost:5174

Mỗi origin có phiên riêng. Proxy Node chuyển `/api/*` sang Gateway; không cần bật CORS hoặc expose Docker ra LAN. CanvasKit, ảnh và font được đóng gói cục bộ, không cần tải font/CDN khi xem app.

Trình duyệt PC chỉ hiển thị giao diện mobile, căn giữa và giới hạn rộng 430px. Không có bố cục giới thiệu riêng cho desktop; điện thoại và bản xem thử dùng cùng màn hình đăng nhập, OTP và hồ sơ.

Sau khi đã build, khởi động lại nhanh:

```powershell
pwsh -File scripts/Start-Mobile.ps1 -SkipBuild
```

Khi sửa code, dừng preview bằng Ctrl+C rồi chạy lại script để build. Bản preview trên dùng release web build, không có hot reload. Android debug qua USB hỗ trợ hot reload.

## Tài khoản thử

| App | Số điện thoại | OTP Development |
|---|---|---|
| Khách hàng | `0900000001` | `123456` |
| Tài xế | `0900000002` | `123456` |

Backend Development hiện dùng OTP cố định, **chưa gửi SMS thật**. OTP không tự điền hoặc hiển thị trong UI. Mã hết hạn sau 5 phút; gửi lại sau 60 giây theo cấu hình hiện tại; backend còn áp dụng giới hạn theo SĐT/IP. Nhập được cả `0900000001`, `900000001` và `+84900000001`.

## Chạy điện thoại Android qua USB

Cài Android SDK/JDK theo `flutter doctor`, bật USB debugging, kết nối USB. Không cần giả lập. Chạy `adb reverse tcp:5000 tcp:5000` để điện thoại truy cập Gateway trên máy qua `127.0.0.1:5000`, rồi:

```powershell
pwsh -File scripts/Start-Mobile.ps1 -App Rider -Target Android
pwsh -File scripts/Start-Mobile.ps1 -App Driver -Target Android
```

Chỉ bản Android debug cho phép HTTP local. Khi chạy trực tiếp Flutter cần dùng đúng cặp flavor/entrypoint, ví dụ `flutter run --flavor driver -t lib/main_driver.dart`. Gradle được giới hạn heap 1536 MB và 2 worker cho máy 8 GB; build Android vẫn có thể cần đóng bớt ứng dụng hoặc tạm dừng các dịch vụ backend không dùng.

## Xuất APK gọi API trên VPS

```powershell
pwsh -File scripts/Start-Mobile.ps1 -Target Apk -ApiBaseUrl https://api.example.com
```

Tạo hai APK riêng trong `build/app/outputs/flutter-apk/`. URL ví dụ phải thay bằng API thực của bạn. Backend/VPS cần HTTPS và provider SMS thật nếu dùng OTP cho người dùng thực. Template hiện ký APK bằng debug key để cài thử; cần cấu hình release keystore riêng trước khi phân phối chính thức. Android SDK chưa phải điều kiện để chạy bản web; nếu chưa cài thì chưa thể build APK.

## Lưu phiên và lỗi

Token dùng `flutter_secure_storage`; Android dùng kho bảo mật nền tảng, web dùng cơ chế lưu mã hóa của plugin trên secure context (HTTPS hoặc localhost). Tắt Android backup để tránh lỗi khôi phục khóa. Không log token. Nếu không ghi được storage, phiên chỉ tồn tại trong bộ nhớ. Logout luôn xóa phiên cục bộ; nếu backend không truy cập được, app hiển thị lỗi và phiên phía server có thể còn tồn tại đến khi hết hạn.

Refresh được gom thành một yêu cầu; lỗi/mất response refresh sẽ xóa phiên để tránh retry refresh token cũ gây reuse revocation. Quyền nghiệp vụ thực vẫn phải được backend kiểm tra; clientApp không cấp quyền.

## Kiểm tra

```powershell
cd apps/mobile
flutter analyze
flutter test
```

Bao phủ chuẩn hóa SĐT, payload đăng ký, lỗi OTP, chặn Rider vào Driver, refresh đồng thời, mất response refresh, logout mất mạng, khôi phục phiên, bố cục 360px và desktop, kiểm tra họ tên đăng ký.

## Hình ảnh và font

Font Be Vietnam Pro được đóng gói kèm giấy phép `assets/fonts/OFL.txt`.
Prompt gốc và tên file của hai ảnh: [assets/IMAGES.md](assets/IMAGES.md).
Hai ảnh AI mới được tạo bằng công cụ imagegen tích hợp và lưu trong `assets/images/rider-hero.png`, `assets/images/driver-hero.png`, theo phong cách ảnh mẫu người dùng. Không nhúng toàn bộ ảnh chụp giao diện làm màn hình. Mô tả prompt: minh họa thương mại anime tươi sáng tại đô thị Việt Nam, xe máy và đồng phục xanh emerald, trời cyan và cây xanh; bản khách có nữ hành khách cùng tài xế trên xe trắng, bản tài xế nhìn từ sau với điện thoại bản đồ nổi; không chữ, logo hay khung UI. Logo và icon được dựng bằng code/vector.
