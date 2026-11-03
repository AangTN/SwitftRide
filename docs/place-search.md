# Chọn điểm đón và điểm đến bằng tìm kiếm

Rider tạm bỏ lấy GPS/vị trí hiện tại. Cả hai ô dùng tìm kiếm Goong thông qua
Matching và Gateway, với JWT của phiên đăng nhập hiện có. GOONG_API_KEY vẫn chỉ
ở backend; không có key này trong Flutter hoặc query của app tới Gateway.

## API và dữ liệu

- GET `/api/matching/places/search?input=...`: đã có, trả danh sách
  `{ description, placeId }`.
- **Mới:** GET `/api/matching/places/details?placeId=...`: role Rider/Driver;
  trả `{ placeId, name, formattedAddress, latitude, longitude }`, hoặc 404
  khi không còn kết quả. Matching gọi Goong V2 Place Detail bằng đúng place_id.
  Căn cứ định dạng: https://help.goong.io/kb/rest-api-v2/place-detail-rest-api-v2/place-detail-place-detail-v2/
- Các API geocode/reverse hiện có vẫn được giữ cho nhu cầu khác, nhưng luồng
  Rider này không dùng chúng khi chọn địa điểm hoặc bấm Áp dụng.
- Không có migration DB hoặc biến môi trường mới. Dùng GOONG_API_KEY đã có.
  Khi deploy qua GitHub Actions, đặt thêm repository secret `GOONG_API_KEY`.
  Workflow truyền qua SSH stdin vào `.secrets/goong-api-key` (quyền 600) trên
  VPS; release.sh nạp vào môi trường Compose cho Matching. Key này không đi
  vào web build và không được in ra log. `GOONG_MAPTILES_KEY` vẫn là key khác.

## Luồng app

1. Khách gõ ít nhất 2 ký tự ở ô đón hoặc đến. Debounce 350 ms rồi tìm, có trạng
   thái loading/rỗng/lỗi và thử lại. Không tự chọn kết quả đầu tiên.
2. Khách chọn kết quả; app lấy chi tiết theo placeId, hiển thị tên và địa chỉ
   đầy đủ. Dữ liệu hai ô độc lập; kết quả cũ hoặc request của ô trước không ghi
   đè lựa chọn mới.
3. App giữ nguyên địa chỉ và tọa độ từ chi tiết đã chọn; không tra địa chỉ lần
   nữa khi bấm Áp dụng. Kiểm tra tọa độ trước khi đưa vào MapLibre để không bị
   thư viện tự clamp/wrap dữ liệu ngoài phạm vi.
4. Sửa chữ sau khi chọn sẽ xóa lựa chọn/tọa độ cũ. Chỉ gõ chữ, chưa chọn kết quả
   có địa chỉ/tọa độ thì không áp dụng được. Những điểm gợi ý tĩnh có sẵn địa chỉ
   và tọa độ vẫn chọn được.
5. Áp dụng trả RideLocations về home, giữ cả điểm đón và điểm đến đã xác nhận.
   Đóng bảng trước khi áp dụng thì bỏ thay đổi nháp. Không tự tạo chuyến.
6. `RideLocations.toTripRequest()` chuẩn bị body tạo chuyến với pickupAddress,
   pickupLatitude/Longitude, destinationAddress, destinationLatitude/Longitude,
   vehicleType. Hàm chặn dữ liệu chưa có địa chỉ/tọa độ. Người làm luồng Trip
   dùng body này với POST `/api/trips` và Idempotency-Key riêng của lần đặt mới.

Tìm “Đại học Sài Gòn” phụ thuộc dữ liệu Goong, có thể có nhiều cơ sở. App hiển
thị danh sách và địa chỉ để khách chọn; không hứa rằng tìm theo tên luôn chỉ có
một kết quả hay pin trùng tuyệt đối cổng đón.

## Kiểm tra

- Backend: Place Detail giữ place_id/tọa độ, loại dữ liệu sai/khác ID, kết quả
  rỗng và quyền Rider/Driver; API cần JWT, không cho Service dùng thay app.
- Flutter: chọn riêng hai ô, sửa chữ làm mất tọa độ cũ, request cũ trả muộn,
  lỗi/thử lại, body Trip, token refresh, không gửi Goong key, bố cục ba cỡ điện thoại.
- Không thay phần Driver GPS, SignalR, Matching, thanh toán hoặc schema Trip.

## Địa chỉ hành chính mới

Toàn bộ lời gọi REST của Goong chuyển sang V2, cùng key hiện có:

| Nghiệp vụ | Endpoint Goong |
|---|---|
| Gợi ý tìm kiếm | `/v2/place/autocomplete` |
| Chi tiết theo placeId | `/v2/place/detail` |
| Địa chỉ → tọa độ | `/v2/geocode?address=...` |
| Tọa độ → địa chỉ | `/v2/geocode?latlng=...` |
| Định tuyến tính giá | `/v2/direction` |

Các API địa chỉ gửi `has_deprecated_administrative_unit=false`, chỉ đọc trường
`description` hoặc `formatted_address` chính thức, không dùng các trường
`deprecated_description`, `deprecated_compound`. Không dự phòng về V1 khi V2 lỗi,
không dùng regex xóa chữ Quận/Huyện hoặc tự đoán tên phường sau sáp nhập.

Ba điểm đề xuất trong app dùng đúng địa chỉ và tọa độ đã kiểm chứng từ Goong V2:
Chợ Bến Thành (Bến Thành), Nhà thờ Đức Bà (Sài Gòn), Phố đi bộ Nguyễn Huệ (Sài Gòn).
Danh sách lịch sử giả chưa được sử dụng đã được bỏ. Địa chỉ/tọa độ đã chọn vẫn
được giữ nguyên khi chuẩn bị body Trip, nên chuyến mới và thông tin chuyển cho
tài xế dùng cùng dữ liệu mới. Snapshot các chuyến cũ trong DB là dữ liệu lịch sử,
không bị ghi đè hoặc đổi tên phường bằng suy đoán. Không cần migration.

Nguồn API: https://help.goong.io/kb/rest-api-v2/
