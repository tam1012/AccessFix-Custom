# AccessFix v2.0 (Custom GUI Edition)

Công cụ vượt chặn nhà mạng nhẹ gọn trên Windows cho các trang web bị chặn theo tên miền (LinkedIn, Quora, Medium, Reddit, BBC...) và Steam / Game — đóng gói thành **1 file chạy `AccessFix.exe` duy nhất**, mở dùng ngay và mang sang bất kỳ máy Windows 10/11 nào không cần cài đặt thêm phần mềm phụ trợ.

---

## Điểm nổi bật & Tối ưu so với bản `.cmd` gốc

1. **Độc lập hoàn toàn (Portable 1 file `.exe` ~70 KB):**
   - Chạy trên nền `.NET Framework 4.x` có sẵn mặc định trong mọi máy Windows 10 và Windows 11.
   - Chép `AccessFix.exe` (kèm `AccessFix.config.json` nếu muốn mang theo danh sách tên miền đã thêm) sang máy khác là bấm chạy ngay.
2. **Không đòi quyền Administrator, không đổi DNS máy, không chiếm cổng `443`:**
   - Tự phân giải IP sạch ngầm qua kênh mã hóa HTTPS trực tiếp tới Cloudflare (`1.1.1.1`) và Google (`8.8.8.8`).
   - Chỉ mở trạm trung chuyển nội bộ ở cổng `127.0.0.1:18080` kèm bộ lọc PAC: **chỉ đúng các tên miền được bật mới đi qua bộ chẻ gói SNI**, mọi trang web khác (ngân hàng, YouTube, công việc, server local `:443`) đều đi thẳng tốc độ gốc, không đổi IP sang nước ngoài.
3. **Tự chuyển đổi IP dự phòng (Multi-IP Failover):**
   - Tự động thử nhiều địa chỉ IP của tên miền với thời gian chờ ngắn (3.2 giây/IP), đẩy IP sống nhanh nhất lên đầu và loại bỏ IP chết — khắc phục lỗi treo 15 giây ở bản `.cmd` cũ.
4. **Quản lý tên miền trực tiếp trên giao diện (Ăn ngay không cần khởi động lại trình duyệt):**
   - Hỗ trợ gõ tên miền (`bbc.com`) hoặc dán nguyên đường link (`https://www.linkedin.com/in/...` -> tự lọc gọn thành `linkedin.com`, áp dụng cho cả `*.linkedin.com`).
   - Có dải nút **Thêm nhanh đủ CDN** (`+ LinkedIn`, `+ Quora`, `+ Medium`, `+ Reddit`, `+ BBC`, `+ Telegram Web`, `+ Discord`) để không bị lỗi vào được chữ nhưng mất ảnh/giao diện.
   - Có nút **Kiểm tra vượt chặn** đo trực tiếp thời gian bắt tay TLS cắt gói SNI ngay trên bảng.
5. **An toàn chống lỗi mất mạng:**
   - Tự động trả cài đặt mạng trình duyệt về mặc định ngay khi bấm **Tắt**, khi thoát ứng dụng, khi tắt máy Windows, hoặc tự dọn sạch ở lần mở tiếp theo nếu máy bị sập nguồn đột ngột.

---

## Cách sử dụng

1. Mở **`AccessFix.exe`**.
2. Mặc định công cụ tự bật vượt chặn ngay với bộ tên miền cấu hình sẵn (LinkedIn, Quora, Steam & Game). Có thể bấm nút lớn **TẮT VƯỢT CHẶN / BẬT VƯỢT CHẶN** bất kỳ lúc nào.
3. Muốn thêm trang mới: gõ tên miền hoặc dán link vào ô nhập rồi bấm **`+ Thêm tên miền`** (hoặc nhấn `Enter`).
4. Khi đang dùng, giữ ứng dụng chạy hoặc bấm **`Ẩn xuống khay`** (biểu tượng chiếc khiên màu xanh ở góc đồng hồ).

### Tham số dòng lệnh hỗ trợ
- `AccessFix.exe --minimized` : Khởi động ẩn dưới khay đồng hồ và bật vượt chặn ngay (dùng khi khởi động cùng Windows).
- `AccessFix.exe --reset` : Khôi phục cài đặt proxy của Windows về mặc định rồi thoát ngay.

---

## Cách tự biên dịch lại từ mã nguồn (không cần cài Visual Studio)

Trên bất kỳ máy Windows 10/11 nào, chỉ cần bấm đúp vào file **`build.bat`** (sử dụng trình biên dịch `csc.exe` có sẵn trong Windows) — file `AccessFix.exe` sẽ được tạo lại trong vài giây.

Hoặc nếu máy có `.NET SDK`:
```powershell
dotnet build src/AccessFix.csproj -c Release
```

---

## Cấu trúc thư mục

- `AccessFix.exe` — File chạy chính thức (Portable).
- `AccessFix.config.json` — File lưu danh sách tên miền và tùy chọn (tự tạo khi chạy lần đầu).
- `build.bat` — Script biên dịch lại `.exe` trên mọi máy Windows không cần cài thêm SDK.
- `src/` — Toàn bộ mã nguồn C# sạch của giao diện và lõi `SplitProxyEngine`.
- `AccessFix-Custom.zip` — Bản script `.cmd` gốc lưu trữ để đối chiếu.
