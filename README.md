# ProjectManagerBot

Bot Discord quản lý dự án/scrum cho studio game, xây dựng bằng .NET 8 Worker Service + Discord.Net + SQLite (EF Core).

## Cài Đặt Nhanh

1. Sao chép `.env.example` thành `.env`
2. Điền tối thiểu:
   - `DISCORD_BOT_TOKEN`
   - `DISCORD_GUILD_ID` (tùy chọn nếu đăng ký lệnh toàn cục)
   - (tùy chọn) `GITHUB_TOKEN` nếu theo dõi repo private hoặc cần rate limit cao hơn
3. Chạy:

```powershell
dotnet run
```

Hoặc trên Windows:

```powershell
.\run.bat
```

## Theo Dõi Push Từ Repo Game (GitHub)

Bot hỗ trợ theo dõi **repo GitHub bên ngoài** (repo game của bạn), không phụ thuộc repo source của bot.

1. Đảm bảo kênh dự án đã được gắn (`/project setup` hoặc `/studio-init`).
2. Bind repo game vào project Discord:
   - `/project github-bind repository:<owner/repo-that>`
   - Mặc định bot theo dõi mọi nhánh (`branch:*`).
   - Có thể dán URL đầy đủ GitHub thay cho `owner/repo-that`.
3. Kiểm tra danh sách binding:
   - `/project github-list`
4. Quét thủ công để test ngay:
   - `/project github-sync`
5. Gỡ theo dõi:
   - Mặc định gỡ toàn bộ nhánh của repo: `/project github-unbind repository:<owner/repo-that>`
   - Hoặc chỉ gỡ 1 nhánh cụ thể: `/project github-unbind repository:<owner/repo-that> branch:main`

Thông báo push sẽ gửi vào kênh `#github-commits` (hoặc kênh bạn chỉ định khi bind).

## Lưu Ý Bảo Mật

- `.env` đã được ignore bởi git và không được commit.
- Giữ `appsettings.json` không chứa secret.
- Chỉ dùng **Bot Token** (không dùng Client Secret).
- Nếu token từng bị lộ, hãy rotate trong Discord Developer Portal ngay.

## Tránh Lỗi Mã Hóa Tiếng Việt

- Luôn lưu file source ở `UTF-8` (repo đã cấu hình `.editorconfig` để ưu tiên `UTF-8 BOM` cho `.cs/.json/.md`).
- Trên Windows Terminal/PowerShell, dùng mã trang UTF-8:
  - `chcp 65001`
- Trong PowerShell, đặt encoding đầu vào/đầu ra:
  - `[Console]::InputEncoding  = [System.Text.UTF8Encoding]::UTF8`
  - `[Console]::OutputEncoding = [System.Text.UTF8Encoding]::UTF8`
  - `$OutputEncoding = [Console]::OutputEncoding`
- Nếu mở file bị vỡ chữ trong IDE, chọn lại encoding:
  - VS Code: `Reopen with Encoding -> UTF-8`, sau đó `Save with Encoding -> UTF-8`.
- Nếu dùng Visual Studio: `File -> Save As -> Save with Encoding -> Unicode (UTF-8 with signature)`.
- Hạn chế chỉnh file tiếng Việt bằng editor/command không hỗ trợ UTF-8.
