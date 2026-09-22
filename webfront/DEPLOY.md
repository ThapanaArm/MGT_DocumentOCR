# Deploy — MGT Document OCR

สรุปขั้นตอน publish ขึ้น IIS (เครื่อง server คนละเครื่องกับเครื่อง dev)

| ส่วน | IIS site | Physical path | URL |
|---|---|---|---|
| Frontend (React/Vite) | OcrFront | `C:\WEB\OcrFront` | (hostname ของ frontend) |
| Backend (.NET) | OcrApi | `C:\WEB\OcrApi` | https://apiocr.megachem.co.th |

---

## Frontend — ขั้นตอนปกติ (ใช้บ่อยสุด)

### 1. Build

```cmd
cd /d C:\Users\Kamonwan\source\repos\MGT_DocumentOCR\webfront
npm run build
```

ได้ผลลัพธ์ที่ `dist\` — ต้องมี `index.html`, `assets\`, `favicon.svg`, **`web.config`**

> `web.config` มาจาก `public\web.config` ซึ่ง Vite copy ให้อัตโนมัติ
> **ห้ามแก้ `dist\web.config`** เพราะโดนทับทุกครั้งที่ build — แก้ที่ `public\web.config` เท่านั้น

### 2. Copy ขึ้น server

```cmd
robocopy "C:\Users\Kamonwan\source\repos\MGT_DocumentOCR\webfront\dist" "\\SERVER_NAME\c$\WEB\OcrFront" /MIR /R:2 /W:2
```

หรือรวบ build + copy ในคำสั่งเดียว:

```cmd
powershell -ExecutionPolicy Bypass -File ".\deploy-iis.ps1" -Target "\\SERVER_NAME\c$\WEB\OcrFront"
```

`/MIR` จำเป็น — Vite ตั้งชื่อไฟล์ใหม่ทุก build (`index-tOb_ei2I.js`) ถ้าไม่ลบของเก่าจะกองสะสม

### 3. เช็ก

เปิดเว็บ → F12 → Network

- หน้าแรกขึ้น = assets โหลดผ่าน
- กดเข้าเมนูแล้ว **F5** ไม่ 404 = SPA fallback ทำงาน
- `/api/...` ได้ 200 หรือ 401 (ไม่ใช่ 404/502) = proxy ทำงาน

---

## Backend — เมื่อแก้โค้ด .NET

```cmd
dotnet publish -c Release -o <โฟลเดอร์ publish>
```

แล้ว copy ขึ้น `C:\WEB\OcrApi` บน server

### ⚠️ จุดที่พลาดบ่อยที่สุด

`dotnet publish` จะ **สร้าง `web.config` ทับใหม่ทุกครั้ง** ค่าพวกนี้จะหายหมด ต้องใส่กลับทุกรอบ:

```xml
<aspNetCore processPath="dotnet"
            arguments=".\MgtOcr.Api.dll"
            stdoutLogEnabled="true"
            stdoutLogFile=".\logs\stdout"
            hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
    <environmentVariable name="Auth__JwtSigningKey" value="<คีย์>" />
  </environmentVariables>
</aspNetCore>
```

- `hostingModel` รับแค่ **`inprocess`** หรือ **`outofprocess`** เท่านั้น สะกดเป็นอย่างอื่น (เช่น `Outprocess`) = ตาย 500 ทันทีและ**ไม่มี log ออกมาเลย**
- `Auth__JwtSigningKey` ใช้ **ขีดล่างสองอัน** (`:` ใช้ใน env var บน Windows ไม่ได้) ถ้าไม่ตั้ง แอปจะ refuse to start ใน Production

**ทางเลี่ยงถาวร** — ย้ายคีย์ไปไว้ที่ Application Pool แทน จะไม่หายตอน publish:

IIS Manager → node ชื่อเครื่อง → Configuration Editor → section `system.applicationHost/applicationPools` → Collection `...` → pool ของ OcrApi → `environmentVariables` → Add `Auth__JwtSigningKey` → Apply → Recycle

---

## ค่าตั้งครั้งเดียว (ทำแล้ว ไม่ต้องทำซ้ำ)

- [x] ARR → Server Proxy Settings → **Enable proxy**
- [x] OcrFront app pool = **No Managed Code**, สิทธิ์ `IIS_IUSRS` Read
- [x] OcrApi `hostingModel="inprocess"` + `Auth__JwtSigningKey`
- [ ] Entra: ใส่ URL ของ OcrFront เป็น **SPA redirect URI** (ไม่งั้น login เด้ง `AADSTS50011`)

---

## ไล่ปัญหา

| อาการ | สาเหตุที่เจอจริง |
|---|---|
| หน้าขาว, console 404 `/assets/*.js` | `base` ใน vite.config ผิด หรือ copy ทั้งโฟลเดอร์ `dist` ไปแทนที่จะ copy ข้างใน |
| 404 ตอน F5 หน้า route ย่อย | `web.config` ไม่ได้ขึ้นไปด้วย / ไม่มี URL Rewrite |
| API 500 | ดู **Event Viewer → Application → IIS AspNetCore Module V2** ก่อนเสมอ |
| API 500 แต่ `logs\` ว่างเปล่า | IIS ยังไม่เคยสตาร์ท dotnet — config ผิด ไม่ใช่โค้ดพัง |
| 500 body ว่าง ดูอะไรไม่ได้ | ยิงจากบน server ด้วย `curl.exe -i -k --resolve <host>:443:127.0.0.1 https://<host>/api/me` |
| 502 ฝั่ง /api | ARR ยังไม่ enable proxy / target ใน web.config ผิด |

### คำสั่งที่ใช้บ่อยตอนดีบัก (รันบน server)

```cmd
cd /d C:\WEB\OcrApi
dotnet .\MgtOcr.Api.dll
```

รันมือแบบนี้เห็น exception จริงเต็ม ๆ เร็วกว่าไล่ log — แต่ **Ctrl+C ปิดให้เรียบร้อยก่อนกลับไปใช้ IIS** ไม่งั้นจองพอร์ตค้าง

```powershell
Get-WinEvent -LogName Application -MaxEvents 20 |
  Where-Object { $_.ProviderName -match 'AspNetCore|IIS|WAS' } |
  Format-List TimeCreated, ProviderName, Message
```
