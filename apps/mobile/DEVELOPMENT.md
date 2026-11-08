# SwiftRide — app khách hàng và tài xế

Hai ứng dụng Flutter dùng chung source và design system, có hai entrypoint:

- `lib/main_rider.dart`: khách hàng; Android ID `vn.swiftride.rider`.
- `lib/main_driver.dart`: tài xế; Android ID `vn.swiftride.driver`.

Đã có đăng nhập số điện thoại → OTP, đăng ký khách hàng, lưu/khôi phục phiên, hồ sơ từ API và đăng xuất. App khách hỗ trợ tìm địa điểm, đặt chuyến với `Idempotency-Key`, khôi phục chuyến hiện tại, duyệt/từ chối giá tài xế, theo dõi trạng thái/tuyến/ETA, trạng thái thanh toán và lịch sử chuyến. App khách dùng SignalR qua Gateway để cập nhật trạng thái, GPS và tuyến. Khi mở lại hoặc reconnect, app tải lại snapshot và tuyến. Menu khách gồm Trang chủ, Chuyến đi và Tài khoản; thanh toán demo do backend xử lý bằng ví SwiftRide. App tài xế đã nối bật/tắt nhận chuyến, lời mời 30 giây qua SignalR, nhận/từ chối, đến đón/đón khách/trả khách, tuyến theo chặng, lịch sử và thu nhập. Kết nối tài khoản được mở trước khi có lời mời; chỉ SubscribeTrip sau khi được gán chuyến. Khôi phục online qua GET /api/matching/drivers/me/availability; gửi GPS khi di chuyển và heartbeat mỗi 20 giây khi đứng yên. Trả khách xong có thể tiếp tục nhận chuyến trong khi backend xử lý thanh toán.

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

Chỉ chạy app khách hàng bằng `.\scripts\Start-Mobile.ps1 -App Rider`; khi đó chỉ cần mở http://localhost:5173.

Mỗi origin có phiên riêng. Proxy Node chuyển `/api/*` sang Gateway; không cần bật CORS hoặc expose Docker ra LAN. CanvasKit, ảnh và font được đóng gói cục bộ, không cần tải font/CDN khi xem app.

Trình duyệt PC hiển thị giao diện mobile, căn giữa và giới hạn rộng 430px. Điện thoại và bản xem thử dùng cùng màn hình đăng nhập, OTP, trang chủ khách hàng và hồ sơ.

## Cấu hình bản đồ Goong

Lưu key tại `.env` ở gốc repository (đã được loại khỏi Git):

```dotenv
GOONG_MAPTILES_KEY=your_maptiles_key
GOONG_API_KEY=your_rest_api_key
```

`Start-Mobile.ps1` chỉ lấy `GOONG_MAPTILES_KEY`, truyền cho Flutter qua file JSON tạm bằng `--dart-define-from-file`, rồi xóa file tạm khi kết thúc. Các mật khẩu, JWT và `GOONG_API_KEY` không được truyền vào app. Matching backend dùng REST API Key để tìm địa chỉ và lấy tuyến đường. Không truyền nguyên `.env` cho Flutter. Khi đổi Maptiles Key, cần build lại (không dùng `-SkipBuild`). Maptiles Key được đóng gói trong client, không phải bí mật có thể giấu khỏi người dùng ứng dụng.

Bản đồ dùng MapLibre GL với style và dữ liệu Goong; bản web cần Internet để tải thư viện và bản đồ, Android yêu cầu quyền vị trí và JDK 21 khi build. Chọn điểm đón/đến từ kết quả tìm kiếm địa chỉ trước khi đặt chuyến. Địa chỉ, tọa độ và loại xe được gửi qua Trip API; app dùng `Idempotency-Key` ổn định để tránh tạo chuyến trùng khi gửi lại. Trong chuyến, bản đồ hiển thị điểm đón/đến và tuyến khi backend đã có; ETA tới đón và thời gian dự kiến lấy từ Matching. Trip được khôi phục bằng `GET /api/trips/current`, các hành động duyệt/từ chối/hủy gửi `stateVersion`, lịch sử dùng `GET /api/trips/history`. Sau khi tạo chuyến, app kết nối Hub `/api/trips/realtime`, gọi `SubscribeTrip`, rồi tải lại snapshot và tuyến. Các sự kiện cập nhật trạng thái, GPS và tuyến. Khi reconnect hoặc mở lại app, app khôi phục chuyến qua API. Yêu cầu đặt xe chưa rõ kết quả được giữ nguyên dữ liệu và khóa trong bộ nhớ bảo mật theo tài khoản; khi thử lại, app xử lý `active_trip_exists` bằng cách mở chuyến đang có. Sau trả khách, khách có thể về trang chủ đặt tiếp dù thanh toán cũ còn chờ. Dịch vụ tìm địa chỉ cần `GOONG_API_KEY`; bản đồ cần `GOONG_MAPTILES_KEY`.

Nếu Goong trả `403`, kiểm tra đúng loại Maptiles Key, trạng thái kích hoạt tài khoản và giới hạn URL/IP. Tài khoản mới có thể cần tới 24 giờ để kích hoạt theo [hướng dẫn Goong](https://help.goong.io/kb/tai-lieu-bo-sung/luu-y/toi-can-lam-gi-de-bat-dau-su-dung-duoc-apis-cua-goong-maps/). Với preview khách hàng, URL được dùng là `http://localhost:5173` (hoặc `http://127.0.0.1:5173` nếu mở bằng IP); cần cho phép địa chỉ thực tế khi cấu hình giới hạn key.

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

Bao phủ chuẩn hóa SĐT, payload đăng ký, lỗi OTP, chặn Rider vào Driver, refresh đồng thời, mất response refresh, logout mất mạng, khôi phục phiên, tìm địa điểm, bố cục app và API đặt chuyến (Idempotency-Key, refresh/retry, 204, lịch sử, stateVersion, giải mã tuyến), SignalR qua WebSocket thật, reconnect, GPS, phản hồi về trễ và khôi phục yêu cầu đặt xe chưa rõ kết quả.

## Hình ảnh và font

Font Be Vietnam Pro được đóng gói kèm giấy phép `assets/fonts/OFL.txt`.
Prompt gốc và tên file của hai ảnh: [assets/IMAGES.md](assets/IMAGES.md).
Hai ảnh AI mới được tạo bằng công cụ imagegen tích hợp và lưu dạng WebP tối ưu tại `assets/images/rider-hero.webp` và `assets/images/driver-hero.webp`, theo phong cách ảnh mẫu người dùng. Không nhúng toàn bộ ảnh chụp giao diện làm màn hình. Mô tả prompt: minh họa thương mại anime tươi sáng tại đô thị Việt Nam, xe máy và đồng phục xanh emerald, trời cyan và cây xanh; bản khách có nữ hành khách cùng tài xế trên xe trắng, bản tài xế nhìn từ sau với điện thoại bản đồ nổi; không chữ, logo hay khung UI. Logo và icon được dựng bằng code/vector.
