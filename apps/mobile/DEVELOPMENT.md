# SwiftRide — app khách hàng và tài xế

Hai ứng dụng Flutter dùng chung source và design system, có hai entrypoint:

- `lib/main_rider.dart`: khách hàng; Android ID `vn.swiftride.rider`.
- `lib/main_driver.dart`: tài xế; Android ID `vn.swiftride.driver`.

Đã có đăng nhập số điện thoại → OTP, đăng ký khách hàng (họ tên + SĐT → OTP), gửi lại OTP theo thời gian backend trả về, lưu/khôi phục phiên, refresh token một lần cho các yêu cầu đồng thời, hồ sơ từ API, đăng xuất. App tài xế chỉ chấp nhận user có role Driver; không có chức năng tự cấp quyền tài xế. Sau đăng nhập, khách hàng vào trang chủ với bản đồ Goong; hồ sơ và đăng xuất nằm ở tab Tài khoản. Đặt xe và nhận chuyến chưa thuộc bản này.

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

Trình duyệt PC hiển thị giao diện mobile, căn giữa và giới hạn rộng 430px. Điện thoại và bản xem thử dùng cùng màn hình đăng nhập, OTP, trang chủ khách hàng và hồ sơ.

## Cấu hình bản đồ Goong

Lưu key tại `.env` ở gốc repository (đã được loại khỏi Git):

```dotenv
GOONG_MAPTILES_KEY=your_maptiles_key
GOONG_API_KEY=your_rest_api_key
```

`Start-Mobile.ps1` chỉ lấy `GOONG_MAPTILES_KEY`, truyền cho Flutter qua file JSON tạm bằng `--dart-define-from-file`, rồi xóa file tạm khi kết thúc. Các mật khẩu, JWT và `GOONG_API_KEY` không được truyền vào app. REST API Key dành cho backend ở bước sau, chưa được sử dụng. Không truyền nguyên `.env` cho Flutter. Khi đổi Maptiles Key, cần build lại (không dùng `-SkipBuild`). Maptiles Key được đóng gói trong client, không phải bí mật có thể giấu khỏi người dùng ứng dụng.

Bản đồ dùng MapLibre GL với style và dữ liệu Goong. Bản web cần Internet để tải thư viện MapLibre và bản đồ. Build Android với plugin hiện tại cần JDK 21. Trang chủ mặc định mở bản đồ khu vực Quận 1, TP.HCM, nhưng không đặt sẵn điểm đón hoặc chấm vị trí. Bấm nút định vị để xin quyền vị trí và lấy tọa độ thiết bị; chấm xanh có vòng sóng chỉ xuất hiện khi định vị thành công. Nút định vị trong form cũng lấy vị trí thật và hiển thị vòng tải trong lúc chờ. Tọa độ được giữ cùng nhãn “Vị trí hiện tại”; chưa tra ngược ra địa chỉ số nhà/tên đường. Android yêu cầu quyền vị trí; web cần localhost hoặc HTTPS. Khi chưa có lịch sử chuyến, trang chủ hiện ba “Điểm đến đề xuất” ở TP.HCM; ba “Điểm đến gần đây” là dữ liệu mock tùy chọn. Thẻ “Bạn muốn đi đâu?” và các địa điểm mở bảng chọn điểm đón/điểm đến; người dùng chọn dữ liệu cục bộ hoặc nhập địa chỉ rồi bấm Áp dụng. Chọn địa điểm có tọa độ sẽ di chuyển camera; địa chỉ tự nhập chưa được geocode. Điểm đón, loại xe và thanh toán chỉ giữ trong bộ nhớ màn hình; mỗi lần mở form mới thì điểm đến để trống, trừ khi mở từ một địa điểm đề xuất. Chưa gọi tìm địa chỉ/tuyến đường hoặc tạo chuyến. Bảng đề xuất ở trang chủ thay đổi chiều cao theo thao tác kéo và trượt về vị trí thu gọn/mở rộng khi thả. Tiêu đề/nút đóng của form được cố định, và bản đồ bị khóa thao tác khi form mở. Tab chuyến đi và ví là giao diện chờ, chưa nối dữ liệu nghiệp vụ.

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

Bao phủ chuẩn hóa SĐT, payload đăng ký, lỗi OTP, chặn Rider vào Driver, refresh đồng thời, mất response refresh, logout mất mạng, khôi phục phiên, bố cục 360px và desktop, kiểm tra họ tên đăng ký.

## Hình ảnh và font

Font Be Vietnam Pro được đóng gói kèm giấy phép `assets/fonts/OFL.txt`.
Prompt gốc và tên file của hai ảnh: [assets/IMAGES.md](assets/IMAGES.md).
Hai ảnh AI mới được tạo bằng công cụ imagegen tích hợp và lưu dạng WebP tối ưu tại `assets/images/rider-hero.webp` và `assets/images/driver-hero.webp`, theo phong cách ảnh mẫu người dùng. Không nhúng toàn bộ ảnh chụp giao diện làm màn hình. Mô tả prompt: minh họa thương mại anime tươi sáng tại đô thị Việt Nam, xe máy và đồng phục xanh emerald, trời cyan và cây xanh; bản khách có nữ hành khách cùng tài xế trên xe trắng, bản tài xế nhìn từ sau với điện thoại bản đồ nổi; không chữ, logo hay khung UI. Logo và icon được dựng bằng code/vector.
