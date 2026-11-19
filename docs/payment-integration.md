# Luồng Trip – Payment

## Giao tiếp và quyền

Trip lưu `PaymentRequested` vào Outbox rồi gửi RabbitMQ. Payment đăng ký consumer nhận tin,
dùng `PaymentRequested.PaymentId` làm `Idempotency-Key`. Đây là mã yêu cầu thanh toán do Trip tạo;
Payment có ID bản ghi riêng, không tạo giao dịch mới khi nhận lại cùng yêu cầu.

Payment xử lý xong lưu `PaymentResult(TripId, PaymentId, Status)` vào Outbox cùng transaction với
payment và sổ cái. Trip có consumer nhận kết quả, lưu DB rồi gửi SignalR `TripChanged`.
Khách nhận thông báo phải lấy snapshot qua API; khi reconnect cũng lấy lại snapshot.

`POST /api/payments` được giữ làm API tương thích dành riêng cho role Service. Rider, Driver và Admin
không được tự gửi giá/tài xế để trừ tiền. Luồng đặt xe sử dụng RabbitMQ. API xem ví/lịch sử/chi tiết
vẫn xác thực JWT và kiểm tra quyền của khách/tài xế. Refund dành cho Admin/Service như trước.
Tài khoản broker phải được giữ kín; triển khai thật cần cấp quyền gửi/nhận theo service.

## Tiền, transaction và chống trùng

- Giữ nguyên tối đa **3 lần tổng cộng** gọi gateway, chỉ retry lỗi tạm thời, dùng cùng key.
- Mỗi yêu cầu xử lý trong transaction. PostgreSQL advisory lock theo trip ngăn hai yêu cầu đồng thời
  tạo hai payment. Ví dùng khóa theo user/currency và `FOR UPDATE` trên dòng `RIDER_WALLET` trước khi
  đọc số dư và ghi trừ tiền. Khóa tự giải phóng khi transaction kết thúc.
- Khóa advisory còn bảo vệ trường hợp ví chưa tồn tại. Mọi nghiệp vụ thêm sau này làm thay đổi ví
  phải tuân theo cùng cách khóa trong `IPaymentStore`; không trừ tiền bằng đường ghi DB riêng.
- Khóa ví khi hoàn tiền và khóa trip cũng ngăn hai yêu cầu hoàn tiền tạo hai bút toán đảo.
- Cùng key/nội dung trả lại kết quả đã lưu. Cùng key khác nội dung hoặc cùng chuyến với key mới trả 409.
  Chưa thêm nút thanh toán lại hoặc vòng đời nhiều lần thanh toán thủ công.
- Gateway Wallet ghi Nợ ví khách. MockCard ghi Nợ tài khoản clearing, không trừ thêm ví SwiftRide.
  Hoàn tiền đảo lại đúng nguồn tiền. Đây vẫn là mô phỏng; chưa có MoMo/ngân hàng thật.

## Kết quả và gửi lại

### Tiền mặt và ví

Khách chọn `Cash` hoặc `Wallet` khi tạo chuyến; Trip lưu phương thức trong `trips.payment_method`
và trả qua snapshot. Phương thức này không đổi khi nhận/từ chối đề xuất. Migration `TripPaymentMethod`
thêm cột trong TripDB, gán `Wallet` cho chuyến cũ. Tin `PaymentRequested` cũ thiếu phương thức cũng mặc định `Wallet`.

Với tiền mặt, tài xế bấm **Đã trả khách và thu tiền** sau khi đã nhận tiền từ khách. Trip ghi trả khách,
giải phóng tài xế và lưu `PaymentRequested` có `PaymentMethod=Cash` vào Outbox như luồng ví.
Payment tạo giao dịch `Cash/Succeeded` và Outbox kết quả trong cùng transaction. App không tự đánh dấu
thanh toán thành công trước khi nhận kết quả backend. Trùng tin không tạo thêm giao dịch hoặc phí.

Tiền mặt không kiểm tra/trừ ví khách và không ghi có khoản tiền phải trả tài xế. Với giá 100.000 đ,
phí hiện tại 15% là 15.000 đ: sổ cái ghi Nợ `DRIVER_CASH_RECEIVABLE` của tài xế 15.000 đ,
Có `PLATFORM_REVENUE` 15.000 đ. Tài xế giữ 100.000 đ tiền mặt, thu nhập sau phí là 85.000 đ
và còn phải nộp 15.000 đ phí. API thu nhập tách `cashCollected`, `cashNetEarnings`,
`nonCashEarnings`, `cashCommissionDue`; tổng thu nhập là số sau phí, không cộng tiền mặt lần nữa.
Khoản phí phải nộp đọc từ số dư sổ cái. Chưa có luồng nộp/bù trừ khoản phí này.

Ví vẫn kiểm tra số dư và ghi trừ tiền như trước. Hoàn tiền tự động cho `Cash` trả lỗi
`manual_cash_refund_required`: chưa có nghiệp vụ xác nhận trả tiền mặt thủ công, không tự cộng vào ví
khách hoặc đảo phí khi chưa biết tiền mặt đã được trả.

Payment xác nhận `Succeeded` thì Trip thành `Paid`. Payment xác nhận `Failed` thì Trip vẫn
`PaymentPending`, `PaymentStatus=Failed`; khách đã đi xong và còn cần xử lý thanh toán.
Kết quả trùng không làm đổi trạng thái lần nữa; kết quả cũ không kéo chuyến `Paid` về thất bại.

Outbox của Payment thuộc PostgreSQL PaymentDB, tách với Outbox Trip trong SQL Server.
Gửi kết quả lỗi không thay đổi kết quả tiền đã commit. Worker thử gửi lần đầu + 3 lần thử lại,
cách lần thất bại trước 5 giây; mỗi lần gửi timeout 5 giây. Các con số này thuộc giao nhận tin,
khác với 3 lần gateway. Hết lượt, tin giữ `Failed` và lỗi cuối để vận hành đối soát/gửi lại;
không tự trừ hoặc hoàn tiền chỉ vì không gửi được kết quả. Chưa thêm giao diện vận hành.

Worker khóa dòng bằng `FOR UPDATE SKIP LOCKED`. Nếu tiến trình sập sau khi broker nhận nhưng trước
khi đánh dấu Sent, có thể gửi trùng. Consumer lưu dữ liệu thành công mới hoàn tất nhận tin và phải
idempotent. Các lỗi hạ tầng của consumer được thử lại ngắn trước khi chuyển sang hàng đợi lỗi.

`PaymentOutbox:RetryCount`, `RetryDelaySeconds`, `PublishTimeoutSeconds` cấu hình riêng trong Payment.
RabbitMq:Enabled=false vẫn ghi Outbox nhưng không chạy worker. Migration `PaymentOutboxAndMethod`
thêm bảng Outbox và phương thức thanh toán; bản ghi cũ được gán Wallet. Compose tự migrate khi khởi động.

## Kiểm thử

- `dotnet test SwiftRide.sln -c Release`: API, quyền, sổ cái, rollback, retry và consumer qua broker in-memory.
- Đặt `SWIFTRIDE_TEST_POSTGRES` tới database test PostgreSQL 16 rồi chạy
  `dotnet test tests/Payment.Tests/SwiftRide.Payment.Tests.csproj -c Release`:
  migration thật, số dư khi hai chuyến đồng thời, yêu cầu trùng và hoàn tiền đồng thời.
  Mỗi factory tạo schema riêng và dọn schema đó sau test.
- Có thể đặt thêm `SWIFTRIDE_TEST_RABBITMQ_PORT` tới RabbitMQ test ở localhost, tài khoản test
  `payment_test` / `payment_test_only`, để kiểm tra hai chiều tin trên broker thật. Không dùng tài khoản này
  cho môi trường triển khai. Hàng đợi test có tên ngẫu nhiên; chỉ chạy trên broker test dùng riêng.
- Chưa tích hợp callback/ngân hàng thật; khi thêm cổng thật phải bổ sung chữ ký, tra cứu giao dịch
  chưa rõ kết quả và cơ chế đối soát. Không được áp dụng mô phỏng rollback của Wallet cho tiền đã thu ngoài hệ thống.
