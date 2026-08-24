# CuocSongQuanhTa CAD Tools

Plugin thử nghiệm cho AutoCAD 2026, viết bằng C#/.NET 8.

## Tool 01: tạo đường theo biên rời

- Chọn `LINE`, `ARC`, `SPLINE`, `LWPOLYLINE`.
- Pick một điểm trong vùng cần lấy biên.
- Tool cắt đường tại giao điểm, dò vòng bao quanh điểm pick và tự vá khe hở trong dung sai.
- Chọn kết quả `PLINE` (mặc định) hoặc `SPLINE` trên Palette.
- Chọn màu đầu ra theo ACI hoặc `ByLayer`.
- Preview màu cyan; đoạn tự vá được đánh dấu đỏ.
- Một lần `UNDO` xóa toàn bộ kết quả.

## File dùng trong AutoCAD

Chỉ `NETLOAD` đúng file này:

```text
LOAD TOOL\CuocSongQuanhTa.CadTools.dll
```

Thư mục `LOAD TOOL\Modules` là phần nội bộ phục vụ cập nhật nóng, không chọn DLL trong đó.

## Lệnh

- `TMH`: mở bảng CAD Tools (lệnh chính, ngắn gọn).
- `CQTOOLS`: lệnh dự phòng, vẫn mở cùng bảng CAD Tools.
- `CQBD`: chạy nhanh công cụ tạo đường biên.
- `CQBDOUTPUT`: đặt loại đường và màu bằng dòng lệnh.
- `CQRELOAD`: nạp bản cập nhật mới mà không cần tắt AutoCAD.

## Cách dùng và cập nhật

1. Lần đầu: gõ `NETLOAD`, chọn `LOAD TOOL\CuocSongQuanhTa.CadTools.dll`.
2. Gõ `TMH` để mở bảng.
3. Sau khi code được cập nhật: gõ `CQRELOAD`, rồi `TMH` để mở lại bảng mới.

Không chạy `NETLOAD` lần nữa trong cùng một phiên AutoCAD.

## Build

```powershell
dotnet build .\CuocSongQuanhTa.CadTools.sln -c Release
```
"# PLUGIN-CAD" 
"# PLUGIN-CAD" 
# PLUGIN-CAD
