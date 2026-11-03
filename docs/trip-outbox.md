# Gửi yêu cầu Trip qua Outbox

Trip lưu chuyến, proposal và tin cần gửi trong cùng transaction SQL Server.
Bảng `outbox_messages` thuộc TripDB, không phải database dùng chung giữa các service.
API không chờ RabbitMQ; tác vụ nền lấy tin đã commit rồi gửi.

## Thử lại và kết quả

- Gửi lần đầu ngay khi tác vụ nền lấy được tin (kiểm tra mỗi 250 ms).
- Gửi lỗi hoặc chưa được broker xác nhận sau 5 giây: thử lại tối đa 3 lần,
  mỗi lần cách lần thất bại trước 5 giây. Tổng cộng tối đa 4 lần gửi.
- Thời gian chờ gửi được tính riêng với khoảng nghỉ 5 giây; không bảo đảm kết thúc toàn bộ trong 15 giây.
- Số lần thử, lần thử tiếp, thời điểm gửi thành công và loại lỗi cuối được lưu trong Outbox.
  Không ghi chi tiết lỗi transport có thể chứa mật khẩu.
- Giữ nguyên `MessageId`, `proposalId` hoặc `paymentId` khi gửi lại. Không tạo proposal mới vì lỗi RabbitMQ.
- Service khởi động lại vẫn đọc các tin Pending còn hạn gửi. SQL Server khóa từng dòng để các dispatcher
  không cùng lấy một tin. Có thể gửi trùng nếu tiến trình dừng sau khi broker nhận nhưng trước khi DB commit;
  Matching/Payment bắt buộc chống xử lý lặp theo mã nghiệp vụ.

| Loại tin | Hết 3 lần thử lại |
|---|---|
| Tìm tài xế | Outbox Failed; chuyến Failed với FailureReason=matching_delivery_failed; proposal Cancelled, giữ bản ghi |
| Giải phóng tài xế | Outbox Failed, giữ bản ghi để cảnh báo/xử lý vận hành; không đổi chuyến đang chạy |
| Thanh toán | Outbox Failed; chuyến vẫn PaymentPending. Nếu chưa có phản hồi Payment thì PaymentStatus=DeliveryUnknown |

`MaxDriverAttempts=3` là số lượt đề xuất tài xế; các lần thử gửi tin không tăng số này.
Tin tìm xe còn Pending bị bỏ qua nếu proposal/chuyến đã đóng. Callback Matching đến muộn được từ chối
và tạo tin giải phóng đúng tài xế/proposal. Matching phải có thời hạn giữ tài xế để tự giải phóng
nếu yêu cầu giải phóng cũng không đến được.

## Thanh toán

Lỗi gửi tin không chứng minh giao dịch thất bại. Payment quản lý giao dịch và số dư trong database riêng,
chống trừ tiền lặp bằng cùng paymentId. Payment callback có xác thực nội bộ và phải mang đúng paymentId:

- Pending/Processing: đang xử lý, chuyến vẫn PaymentPending.
- Failed: Payment xác nhận thanh toán thất bại; ghi PaymentStatus=Failed, chuyến vẫn PaymentPending.
- Succeeded: ghi PaymentStatus=Succeeded và chuyển chuyến Paid.

Chỉ callback từ Payment xác định kết quả tiền. Không tự tạo paymentId mới khi trạng thái DeliveryUnknown.
Các tin Failed được giữ để kiểm tra; bản thay đổi này chưa thêm màn quản trị hoặc nút gửi lại thủ công.
Frontend đọc snapshot khi kết nối lại để lấy trạng thái, kể cả lúc thông báo SignalR bị mất.

## Cấu hình và triển khai

`TripOutbox:RetryCount=3`, `RetryDelaySeconds=5`, `PublishTimeoutSeconds=5` trong appsettings.
Có thể thay qua `TripOutbox__RetryCount`, `TripOutbox__RetryDelaySeconds`, `TripOutbox__PublishTimeoutSeconds`.
RabbitMq:Enabled=false vẫn lưu Outbox nhưng không chạy dispatcher; bật lại sẽ tiếp tục gửi các tin Pending.

Áp dụng migration TripWorkflow trước TripOutbox. Compose bật Database:MigrateOnStartup nên service tự migrate;
với môi trường không bật, dùng quy trình EF migration hiện có trước khi chạy phiên bản API mới.
Không chỉ xóa tin Failed để thử lại: phải đối chiếu trạng thái chuyến/giao dịch, giữ nguyên mã nghiệp vụ.

Kiểm thử tự động dùng SQLite và transport giả để mô phỏng lỗi, rollback, khởi động lại scope và đồng hồ.
Trước khi deploy thật cần kiểm tra thêm ngắt/khôi phục RabbitMQ và chạy nhiều dispatcher trên SQL Server.
