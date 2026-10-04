# Chương 11 — Các hạn chế đã biết: dự án này dạy gì và production cần gì

> 🇻🇳 Bản tiếng Việt. English version: [11-known-limitations.md](../11-known-limitations.md)

SimpleStore là một dự án **học tập**. Nó cố ý giữ một số thứ ở mức đơn giản để các ý tưởng *chính* (ranh giới giữa các service, event, saga, event sourcing) luôn dễ nhìn thấy. Chương này liệt kê những chỗ mã nguồn đi đường tắt, vì sao điều đó chấp nhận được ở đây, và bạn sẽ thay đổi gì trong một hệ thống thật.

Chỉ riêng việc đọc chương này đã là một bài tập tốt: với mỗi mục, hãy thử tự giải thích **điều gì có thể sai** trước khi đọc phần "Cách sửa cho production".

> **Cách đọc danh sách này.** Mọi mục dưới đây đều đã được đối chiếu với mã nguồn. Ở chỗ nào một phát biểu phụ thuộc vào hành vi mặc định của một thư viện (MassTransit, YARP, ...) chứ không phải vào mã trong repository này, nó được đánh dấu **"to verify"** (cần kiểm chứng) — hãy tự thử trước khi bạn dựa vào nó.

## Bạn sẽ học được

- Những đơn giản hóa nào là cố ý và những chỗ nào là lỗ hổng thật sự.
- Mỗi lỗ hổng sẽ biểu hiện ra sao trong production.
- Cách sửa thông thường là gì, để bạn có thể mở rộng dự án như một bài tập học tập.

---

## 1. Tính nhất quán và đồng thời (concurrency)

### 1.1 Hàng tồn kho có thể bị bán vượt mức khi có nhiều request đồng thời

- **Ở đâu:** [CreateReservationHandler.cs](../../../src/SimpleStore.Inventory.API/Application/Reservations/CreateReservationHandler.cs)
- **Chuyện gì xảy ra:** handler khóa các dòng `stock_levels` bằng `SELECT ... FOR UPDATE` rồi kiểm tra lượng hàng có sẵn. Nhưng nó **không** trừ `OnHand`. Việc trừ được làm sau đó, bất đồng bộ, bởi projector sau khi event `StockReservedV1` được đọc lại từ KurrentDB.
- **Kịch bản lỗi:** hai reservation cho đơn vị hàng cuối cùng đến trước khi projector áp dụng cái đầu tiên. Cả hai đều đọc `OnHand = 1`, cả hai đều qua bước kiểm tra, cả hai đều append `StockReservedV1`. Sau đó projector đẩy `OnHand` xuống `-1` (cột này cho phép giá trị âm).
- **Vì sao chấp nhận được ở đây:** cách làm này giữ cho luồng ghi thuần event-sourced và minh họa eventual consistency (tính nhất quán sau cùng). Comment đầu handler và [checkout-saga.md §10.3](../../checkout-saga.md) có ghi lại tình huống tranh chấp này (và §13 phác thảo cách sửa bằng stream riêng cho từng sản phẩm).
- **Cách sửa cho production:** biến việc reservation thành một quyết định được đưa ra *bên trong aggregate*, dùng một stream mức tồn kho (một aggregate theo từng sản phẩm mà revision của stream đóng vai trò khóa), hoặc trừ một bộ đếm "available to promise" (lượng có thể hứa bán) một cách đồng bộ, trong cùng transaction với bước kiểm tra.

### 1.2 Payment không có kiểm soát đồng thời ở mức dòng

- **Ở đâu:** [PaymentService.cs](../../../src/SimpleStore.Payment.API/Services/PaymentService.cs), [PaymentDbContext.cs](../../../src/SimpleStore.Payment.API/Data/PaymentDbContext.cs)
- **Chuyện gì xảy ra:** `PaymentAccount` không có concurrency token và việc trừ tiền không khóa dòng. `ledger.OrderId` / `CorrelationId` không có unique index. "Không trừ tiền hai lần" **chỉ** dựa vào inbox của MassTransit (thứ khử trùng các thông điệp bị gửi lại).
- **Kịch bản lỗi:** một khách hàng nạp tiền đúng lúc một lệnh trừ tiền đang chạy. Cả hai đọc cùng một số dư và một bản cập nhật ghi đè lên bản kia (lỗi lost update kinh điển).
- **Cách sửa cho production:** thêm một concurrency token `xmin` của Postgres (hoặc `SELECT ... FOR UPDATE`) cho account, và một unique index trên `(AccountId, CorrelationId)` cho các dòng `Payment`.

### 1.3 Gộp giỏ hàng không có tính nguyên tử

- **Ở đâu:** `RedisCartStore.MergeAsync` trong [RedisCartStore.cs](../../../src/SimpleStore.Cart.API/Services/RedisCartStore.cs)
- **Chuyện gì xảy ra:** nó đọc hai giỏ hàng, ghi giỏ đích, rồi xóa giỏ nguồn. Hai lần gộp đồng thời (hoặc một lần gộp diễn ra giữa lúc đang thêm hàng) có thể làm mất một bản cập nhật.
- **Vì sao chấp nhận được:** việc gộp chỉ chạy một lần mỗi lần đăng nhập; khoảng thời gian có thể xảy ra lỗi rất nhỏ.
- **Cách sửa cho production:** dùng script Lua hoặc transaction của Redis (`WATCH`/`MULTI`), hoặc lưu giỏ hàng dưới dạng Redis hash và dùng `HINCRBY` cho từng dòng.

---

## 2. Checkout saga

### 2.1 Event đến muộn, bị trùng và sai thứ tự không được xử lý tường minh

- **Ở đâu:** [CheckoutSagaStateMachine.cs](../../../src/SimpleStore.Checkout.API/Sagas/CheckoutSagaStateMachine.cs)
- **Mã nguồn có gì:** mỗi state chỉ xử lý những event mà nó mong đợi. Trong toàn dự án không có `Ignore(...)`, `DuringAny(...)`, `OnUnhandledEvent(...)` hay `OnMissingInstance(...)` nào.
- **Điều đó có nghĩa là gì:** chuyện gì xảy ra khi, ví dụ, một `StockReserved` đến lúc saga đang ở `AwaitingPayment`, hoặc bất kỳ event nào đến sau khi dòng saga đã bị xóa, là do các mặc định của MassTransit quyết định. *Cần kiểm chứng:* hãy xem hành vi của phiên bản MassTransit bạn dùng (kết quả thường gặp của một event không được xử lý là một fault đi qua retry policy và kết thúc ở hàng đợi `_error`).
- **Lưu ý:** [checkout-saga.md §9](../../checkout-saga.md) nói rằng những thông điệp như vậy bị "dropped by a state-machine guard" (bị loại bỏ bởi một chốt chặn của state machine). Mã nguồn không có cấu hình hỗ trợ cho khẳng định đó — hãy lấy mã nguồn làm chuẩn.
- **Cách sửa cho production:** khai báo hành vi tường minh cho mọi cặp `(state, event)` mà bạn có thể hợp lệ nhận muộn, và bổ sung xử lý `OnMissingInstance`.

### 2.2 `PaymentSucceeded` đến muộn sau khi payment timeout thì không có đường hoàn tiền

- **Kịch bản:** Payment.API chạy chậm. Payment timeout 30 s kích hoạt, saga chuyển sang `CompensatingStock` và yêu cầu Inventory nhả hàng. Sau đó Payment.API mới trừ tiền tài khoản và publish `PaymentSucceeded`.
- **Kết quả:** tiền bị lấy, hàng được nhả và đơn hàng bị hủy. Không có gì trong mã nguồn hoàn tiền cho khách hàng.
- **Cách sửa cho production:** coi một success đến muộn là event kích hoạt một compensation **hoàn tiền**, hoặc làm bước thanh toán idempotent với một lệnh "cancel payment" mà saga gửi đi khi timeout.

### 2.3 Có thể tạo reservation cho một đơn hàng đã bị hủy

Nếu RabbitMQ ngừng hoạt động đủ lâu để timeout 30 s của bước *stock* kích hoạt, saga sẽ hủy đơn hàng. Khi broker hồi phục, yêu cầu reserve (đã nằm trong outbox) vẫn được chuyển đi và Inventory giữ hàng cho một đơn hàng đã bị hủy. Vì reservation đó không bao giờ tới bước thanh toán nên không có gì giải phóng nó. [checkout-saga.md §11.3](../../checkout-saga.md) mô tả đây là trường hợp duy nhất trong luồng chưa có bước compensation.

### 2.4 Timeout chỉ chạy đúng với một replica

- **Ở đâu:** [Program.cs](../../../src/SimpleStore.Checkout.API/Program.cs) (Quartz persistent store không có clustering)
- **Vì sao:** chạy hai replica Checkout.API trên cùng các bảng Quartz mà không có `UseClustering()` có thể khiến cả hai cùng kích hoạt một trigger.
- **Cách sửa:** bật Quartz clustering (`UseClustering()` và `SchedulerId` bằng `AUTO`), như đã ghi chú trong [v8b](../../v8b-durable-store-for-saga-timeouts.md).

---

## 3. Order service

### 3.1 Server tin vào giá do client gửi

- **Ở đâu:** `OrderService.CreateOrderAsync` trong [OrderService.cs](../../../src/SimpleStore.Order.API/Services/OrderService.cs)
- **Chuyện gì xảy ra:** `ProductName` và `UnitPrice` được lấy thẳng từ request. Không có lời gọi nào tới Catalog, nên một bên gọi API trực tiếp có thể gửi bất kỳ mức giá nào.
- **Vì sao chấp nhận được:** cách này giữ cho Order độc lập với Catalog lúc chạy (không có ràng buộc đồng bộ) và giữ cho ví dụ ngắn gọn.
- **Cách sửa cho production:** tính lại giá ở phía server — hoặc gọi Catalog, hoặc giữ một bản chụp giá (price snapshot) cục bộ được cập nhật từ `ProductUpdatedEventV1`.

### 3.2 Việc chuyển trạng thái đơn hàng không được ép buộc

- **Ở đâu:** [OrderStatus.cs](../../../src/SimpleStore.Order.API/Models/OrderStatus.cs), `UpdateStatusAsync`
- **Chuyện gì xảy ra:** các consumer và `PATCH` của admin ghi đè `Status` vô điều kiện. Admin có thể chuyển `Cancelled` ngược về `Pending`, và một `OrderConfirmed` đến muộn sẽ ghi đè trạng thái `Cancelled`.
- **Cách sửa cho production:** đặt các quy tắc chuyển trạng thái bên trong entity order (`order.Confirm()`, `order.Cancel()`) và từ chối những bước chuyển không hợp lệ.

---

## 4. Xác thực và session

### 4.1 Việc dùng lại refresh token bị từ chối nhưng không được coi là một cuộc tấn công

- **Ở đâu:** [RefreshTokenService.cs](../../../src/SimpleStore.Identity.API/Services/RefreshTokenService.cs)
- **Chuyện gì xảy ra:** token được xoay vòng sau mỗi lần dùng và token cũ bị thu hồi. Xuất trình một token cũ chỉ đơn giản là thất bại (401). Cột `ReplacedByTokenHash` được ghi nhưng không bao giờ được dùng để thu hồi các token còn lại trong cùng một chuỗi refresh token.
- **Cách sửa cho production:** khi một token đã bị thu hồi được dùng lại, hãy thu hồi mọi token con cháu của người dùng đó (phát hiện việc dùng lại refresh token).

### 4.2 Web và Admin lưu session trong bộ nhớ của process

- **Ở đâu:** `AddDistributedMemoryCache()` trong `Program.cs` của Web và Admin
- **Hệ quả:** session (`ss_session`) bị mất khi ứng dụng khởi động lại và không thể chia sẻ giữa hai instance.
- **Cách sửa:** đăng ký một `IDistributedCache` chạy trên Redis (dự án vốn đã chạy Redis cho giỏ hàng).

### 4.3 Có một đường refresh không được gộp lại (coalesce)

- **Ở đâu:** event `OnMessageReceived` của JwtBearer trong Web và Admin
- **Chuyện gì xảy ra:** `TokenRefreshCoordinator` kiểu single-flight (mỗi lúc chỉ một lần gọi thật) bảo vệ `BearerTokenHandler` *đi ra*, nhưng việc refresh trong `OnMessageReceived` *đi vào* lại gọi thẳng Identity. Hai lần tải trang đồng thời với một token sắp hết hạn có thể tranh chấp nhau.

### 4.4 Thông tin đăng nhập demo và secret phát triển

Các user được seed là `admin@simplestore.local` và `demo@simplestore.local` dùng mật khẩu ai cũng biết và được tạo lúc khởi động. Điều này chỉ dành cho demo; đừng bao giờ seed thông tin đăng nhập cố định trong một triển khai thật, và luôn giữ `jwt-key` trong một nơi lưu secret.

---

## 5. Event sourcing / projection

### 5.1 Projector chỉ có một instance

Projector subscribe `$all` bằng một consumer duy nhất. Chạy hai replica Inventory sẽ khiến mỗi event bị áp dụng hai lần (các chốt idempotency theo từng event giúp nó vẫn *đúng*, nhưng lãng phí). Cách sửa: dùng persistent subscription của KurrentDB với một consumer group, hoặc leader election (bầu chọn leader).

### 5.2 Yêu cầu hủy reservation không tồn tại bị bỏ qua và không được thử lại

`ApplyStockReservationCancelledAsync` không làm gì khi dòng read `reservations` bị thiếu hoặc không ở trạng thái `Active`. Vì `$all` được sắp thứ tự chặt chẽ và saga chỉ cancel sau khi nhận được `StockReservedEventV1`, chốt chặn này chỉ có ý nghĩa nếu read model bị xóa một phần hoặc bị chỉnh sửa. Đây là một phép kiểm tra phòng thủ đáng để hiểu, không phải một tình huống tranh chấp có thật.

### 5.3 Back-off khi kết nối lại không được đặt lại sau khi có tiến triển

Trong `InventoryProjectionService`, khoảng chờ tăng dần từ 1 đến 30 giây chỉ được đặt lại khi vòng lặp subscription kết thúc bình thường. Sau một thời gian dài hoạt động ổn định, lần lỗi tiếp theo vẫn giữ khoảng chờ trước đó (có thể là 30 giây).

### 5.4 "Commit" reservation chưa được cài đặt

Reservation được tạo và có thể bị hủy (v12), nhưng không có bước nào chuyển một reservation đã xác nhận thành delivery note. Hàng tồn kho bị trừ ngay lúc tạo reservation và cứ thế giữ nguyên với các đơn hàng đã xác nhận.

### 5.5 Gauge projector-lag đo bằng byte, không đo bằng số event

`simplestore.inventory.projector.lag` là hiệu số giữa các commit position (một byte offset trong transaction log), và "đuôi" mà nó dùng là vị trí cuối cùng mà subscription đã *thấy*, không phải đuôi thật của log.

---

## 6. Hạ tầng và vận hành

| Chủ đề | Hiện nay | Production |
|---|---|---|
| Database migration | áp dụng tự động khi service khởi động | chạy như một bước triển khai riêng |
| Message broker | một container RabbitMQ, không có clustering | clustered/quorum queue, xử lý dead-letter và cảnh báo trên các hàng đợi `_error` |
| Secret | user-secrets của AppHost | một trình quản lý secret (Key Vault, Vault, ...) |
| TLS | chứng chỉ cho môi trường phát triển | chứng chỉ thật, mTLS giữa các service |
| Gateway | không có rate limiting, không giới hạn kích thước request | rate limiting, WAF, giới hạn request |
| Test | repository **không có test project** | unit test cho aggregate và saga (harness `MassTransit.Testing`), integration test với Testcontainers |
| Lưu giữ dữ liệu | các bảng outbox/inbox cứ phình ra | bật dọn dẹp outbox của MassTransit, lưu trữ (archive) các event cũ |

---

## 7. Tài liệu bị lệch so với mã nguồn mà bạn có thể gặp

Khi đọc các tài liệu cũ hơn, bạn có thể thấy những phát biểu không còn khớp với mã nguồn. Nguồn sự thật hiện tại là mã nguồn và hướng dẫn này.

- [checkout-saga.md](../../checkout-saga.md) các mục 2–12 mô tả luồng **v8** (chưa có bước payment). Mục 15 là luồng v12 hiện tại.
- Cùng tài liệu đó nhắc tới một cột `RowVersion` trên saga state, một lý do thất bại `UnknownProduct` và một bảng tên `CheckoutSagaState`. Bảng thật là `checkout_saga_state`, cơ chế đồng thời là khóa `Pessimistic`, và Inventory chỉ phát ra `InsufficientStock`.
- Một số URL trong `Results.Created(...)` của các endpoint bỏ sót đoạn `/v1`.
- Một vài comment trong mã (ví dụ về việc dùng inbox trong `CatalogDbContext` và `InventoryReadDbContext`) có từ trước các phiên bản sau.

## 8. Các lỗi có thể có được tìm thấy khi viết hướng dẫn này

Những mục này khác với các đơn giản hóa cố ý ở trên: chúng trông giống **lỗi (bug) hoặc những điều bất ngờ**, được tìm thấy khi đọc và thử mã nguồn để viết hướng dẫn này. Mỗi mục cho biết nó đã được kiểm chứng đến mức nào.

### 8.1 `[MessageUrn("urn:message:...")]` bị MassTransit 9.2.1 từ chối — *đã tái hiện*

- **Ở đâu:** mọi event trong [`src/SimpleStore.Contracts`](../../../src/SimpleStore.Contracts/) (ví dụ `OrderSubmittedEventV1`).
- **Chuyện gì xảy ra:** các attribute được viết với đầy đủ tiền tố `urn:message:`. Trong một project console thử nghiệm tham chiếu `SimpleStore.Contracts` và `MassTransit.Abstractions` 9.2.1, `MessageUrn.ForType(typeof(OrderSubmittedEventV1))` đã ném `ArgumentException: Value should not contain the default prefix 'urn:message:'` (được bọc trong một `TypeInitializationException`).
- **Tác động:** MassTransit phân giải URN khi nó publish hoặc consume một thông điệp, nên cùng ngoại lệ đó là kết quả được kỳ vọng trong các service đang chạy. *Chưa kiểm chứng từ đầu đến cuối* — AppHost không được chạy cho lần kiểm tra này. Nếu bản build của bạn chạy bình thường, thì phiên bản package được ghim hoặc trạng thái máy của bạn khác; hãy kiểm tra lại.
- **Cách sửa có khả năng đúng:** bỏ tiền tố trong từng attribute, ví dụ `[MessageUrn("SimpleStore.Contracts:OrderSubmittedEvent")]`. Bài thử nghiệm cho ra cùng URN cuối cùng với dạng này.

### 8.2 Việc ghim URN không giữ ổn định tên exchange — *quan sát được trong một bài thử nghiệm*

Ghi chú v11 nói rằng việc đổi tên thành `V1` là vô hình trên bus. Điều đó đúng với URN bên trong envelope của thông điệp, nhưng trong bài thử nghiệm thì tên exchange được suy ra từ tên kiểu CLR, không phải từ URN. Do đó sau khi đổi tên, các exchange có tên `SimpleStore.Contracts:<Name>V1`. Điều này vô hại khi mọi service được build lại và triển khai lại cùng lúc, nhưng không phải là một sự đảm bảo cho các lần nâng cấp cuốn chiếu (rolling upgrade).

### 8.3 Giấy phép (licensing) của MassTransit 9 — *cần kiểm chứng*

Một bài test bus in-memory trên 9.2.1 đã thất bại với thông báo yêu cầu giấy phép (`SetLicense` / `MT_LICENSE`). Không tìm thấy cấu hình giấy phép nào trong repository. Hãy kiểm tra điều khoản giấy phép của phiên bản MassTransit mà bạn triển khai.

### 8.4 Các event tồn đọng trong lúc Inventory ngừng hoạt động không được publish — *từ việc đọc mã nguồn*

`KurrentEventStore.SubscribeAllAsync` chỉ đặt `IsLive` sau marker `CaughtUp` của KurrentDB, cho **mọi** subscription, kể cả một lần khởi động lại bình thường tiếp tục từ một checkpoint. Các event được append trong lúc Inventory ngừng hoạt động được phát lại với `IsLive = false`: các bảng read được cập nhật, nhưng `StockReservedEventV1` và các integration event khác không được publish. Saga chỉ hồi phục được nhờ timeout 30 s của nó. Xem [chương 7](07-inventory-event-sourcing-cqrs.md).

### 8.5 Một event lỗi vĩnh viễn làm projector đứng yên — *từ việc đọc mã nguồn*

Khi có exception, vòng lặp ngoài nạp lại cùng checkpoint và thử lại cùng event đó, tối đa mỗi 30 giây một lần. Vì vậy, các event sau đó không được áp dụng vào read model cho đến khi event lỗi được xử lý. Một chính sách dead-letter hoặc "bỏ qua và cảnh báo" là cách khắc phục thường dùng.

### 8.6 Những điều bất ngờ nhỏ hơn trong các ứng dụng web

- Checkbox `Remember me` trên form đăng nhập của Web được bind nhưng không bao giờ được đọc.
- Đăng nhập giữ nguyên id `ss_session` hiện có thay vì cấp id mới (một điểm cần cân nhắc về session fixation).
- Việc gộp giỏ hàng thất bại bị nuốt lỗi và cookie `ss_cart` vẫn bị xóa, khiến giỏ hàng ẩn danh bị bỏ mồ côi cho tới khi hết hạn.
- Trong danh sách đơn hàng của Web, các đơn `Cancelled` có cùng huy hiệu màu xanh lá như đơn `Confirmed`; hãy đọc dòng chữ trạng thái.
- `ProductUpdatedEventV1` không có version hay timestamp, nên hai lần chỉnh sửa lặp lại nhanh được giao sai thứ tự có thể để lại một giỏ hàng với các giá trị cũ hơn.

## Những điều cần nhớ

- Một dự án học tập tối ưu cho **sự rõ ràng**, không phải cho sự đầy đủ; biết nó cắt xén ở những chỗ nào là một phần của bài học.
- Hầu hết các mục ở trên thuộc một trong ba họ: *thiếu kiểm soát đồng thời*, *thiếu xử lý cho thứ tự thông điệp không như mong đợi*, hoặc *giả định chỉ có một instance*.
- Các bài tập hay: sửa 1.2 (khóa payment), 2.2 (hoàn tiền khi success đến muộn), 3.2 (chuyển trạng thái trong entity), 4.2 (session store dùng Redis).

**Quay lại** [mục lục hướng dẫn](README.md).
