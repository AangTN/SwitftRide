# Hướng dẫn Khoa làm việc trên branch riêng

Không commit hoặc push trực tiếp vào `main`. ZIP này không chứa thư mục `.git`, nên hãy áp dụng các file đã chỉnh vào bản clone Git của nhóm.

## 1. Tạo branch riêng

Mở PowerShell tại thư mục clone SwiftRide của nhóm:

```powershell
git status
git switch main
git pull origin main
git switch -c feature/khoa-nguoi-4-5
```

Nếu nhóm dùng branch tích hợp khác `main`, thay `main` bằng branch đó. Nếu `git status` đang có thay đổi của người khác, đừng ghi đè; commit hoặc stash phần đó trước.

## 2. Chép các file Khoa phụ trách

Giải nén ZIP và chép đè đúng các file/thư mục sau vào clone đang đứng trên branch `feature/khoa-nguoi-4-5`:

- `apps/mobile/lib/src/app.dart`
- `apps/mobile/lib/src/auth.dart`
- `apps/mobile/lib/src/driver_home.dart`
- `apps/mobile/lib/src/goong_map.dart`
- `apps/mobile/lib/src/rider_home.dart`
- `apps/mobile/lib/src/trip_api.dart`
- `apps/mobile/lib/src/trip_models.dart`
- `apps/mobile/lib/src/trip_realtime.dart`
- `apps/mobile/pubspec.yaml`
- `apps/mobile/DEVELOPMENT.md`
- `scripts/serve-mobile.mjs`
- `.env.example`

File `.env` chứa key Goong dùng local, đã được `.gitignore` loại khỏi Git. Không `git add -f .env` và không gửi file này lên GitHub.

## 3. Cài package và kiểm tra

```powershell
cd apps/mobile
flutter pub get
dart format lib/src/app.dart lib/src/auth.dart lib/src/driver_home.dart lib/src/goong_map.dart lib/src/rider_home.dart lib/src/trip_api.dart lib/src/trip_models.dart lib/src/trip_realtime.dart
flutter analyze
flutter test
cd ../..
```

`flutter pub get` sẽ cập nhật `apps/mobile/pubspec.lock` cho SignalR và gọi điện. Nếu `flutter analyze` báo lỗi, sửa hết trước khi commit.

## 4. Chạy app

Tại thư mục gốc repo, sau khi backend Gateway chạy ở port 5000:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Start-Mobile.ps1 -App Both -Target Web
```

- Khách hàng: http://localhost:5173
- Tài xế: http://localhost:5174

Tài khoản dev: Rider `0900000001`, Driver `0900000002`, OTP `123456`.

## 5. Commit và push branch riêng

```powershell
git add apps/mobile/lib/src/app.dart apps/mobile/lib/src/auth.dart apps/mobile/lib/src/driver_home.dart apps/mobile/lib/src/goong_map.dart apps/mobile/lib/src/rider_home.dart apps/mobile/lib/src/trip_api.dart apps/mobile/lib/src/trip_models.dart apps/mobile/lib/src/trip_realtime.dart apps/mobile/pubspec.yaml apps/mobile/pubspec.lock apps/mobile/DEVELOPMENT.md scripts/serve-mobile.mjs .env.example
git status
git commit -m "feat(mobile): implement rider and driver trip flows"
git push -u origin feature/khoa-nguoi-4-5
```

Chỉ push branch `feature/khoa-nguoi-4-5`. Sau đó tạo Pull Request để nhóm review và merge; branch `main` không bị thay đổi bởi lệnh push này.
