# คู่มือแนวทาง: Archive ไฟล์ Invoice ขึ้น SharePoint (หลังโพสต์เข้า SAP สำเร็จ)

โปรเจกต์: OCR → SAP (`D:\Sukanya\WorkSpace\02_OCR\OCR`)
วัตถุประสงค์: เมื่อเอกสารถูกโพสต์เข้า SAP สำเร็จ ให้ระบบเก็บไฟล์ต้นฉบับ (PDF ที่อัปโหลด) ขึ้น SharePoint document library เพื่อเก็บถาวรและอ้างอิงกลับได้
เอกสารนี้เป็น "แนวทางให้พิจารณา" ยังไม่ได้แก้โค้ดใด ๆ

---

## 1. สรุปแนวทางที่แนะนำ

ให้ **backend เรียก Microsoft Graph อัปไฟล์ขึ้น SharePoint เอง** ในจังหวะที่ **post เข้า SAP สำเร็จ** (ไม่ใช้บอทแยก)

เหตุผลที่เลือก backend + Graph แทนบอท:
- เข้ากับ pattern เดิมของแอป (`SapClient`, `AzureOcr`, `GeminiOcr` เรียก REST ผ่าน `HttpClient` อยู่แล้ว) — เพิ่ม `SharePointArchiver` อีกตัวในสไตล์เดียวกัน
- ทำงานในโฟลว์เดียว เชื่อถือได้ ไม่ต้อง poll โฟลเดอร์/ไม่ต้องจัดการไฟล์ซ้ำ
- Auth ตั้งครั้งเดียว (app registration) ไม่ต้องมีคนล็อกอิน
- เก็บ URL ของไฟล์บน SharePoint กลับมาไว้ที่เอกสารได้ทันที → โชว์ปุ่ม "View in SharePoint" ในหน้า DocumentPage

ทางเลือกอื่น (ถ้ายังไม่อยากแตะ backend ทันที) อยู่ท้ายเอกสาร ข้อ 8

---

## 2. ภาพรวมโฟลว์

```
ผู้ใช้กด Post → postToSap() สำเร็จ (ได้ sapDocNo, status = POSTED)
                    │
                    ├─ (มีอยู่แล้ว) อัปเดต DB: sapDocNo, status
                    │
                    └─ (เพิ่มใหม่) SharePointArchiver.UploadAsync(ไฟล์ต้นฉบับ, ปลายทาง)
                            → PUT ไฟล์ขึ้น SharePoint ผ่าน Graph
                            → ได้ webUrl กลับมา → เก็บลง DB (SpUrl, SpArchivedAt)
                            → ถ้าพลาด: ไม่ทำให้ SAP post ล้ม แค่ mark spArchived=false ไว้ retry
```

หลักการสำคัญ: **การอัป SharePoint ต้องไม่ทำให้การโพสต์ SAP ล้ม** — SAP คือของหลัก, SharePoint คือ archive รอง ถ้าอัปไม่สำเร็จให้ log + ตั้งค่าให้ retry ทีหลัง ไม่ใช่ throw ทิ้งทั้ง transaction

---

## 3. สิ่งที่ต้องเตรียม (ครั้งเดียว)

### 3.1 Azure AD (Entra ID) App Registration
1. Entra ID → App registrations → New registration → ได้ **Tenant ID** + **Client ID**
2. Certificates & secrets → New client secret → ได้ **Client Secret** (เก็บให้ดี โผล่ครั้งเดียว)
3. API permissions → Microsoft Graph → **Application permissions** (ไม่ใช่ Delegated):
   - แนะนำ least-privilege: **`Sites.Selected`** แล้วให้แอดมินให้สิทธิ์ write เฉพาะ site ที่จะเก็บ (ผ่าน Graph/PowerShell)
   - หรือกว้างกว่า/ง่ายกว่า: **`Sites.ReadWrite.All`**
4. กด **Grant admin consent** (ต้องใช้สิทธิ์แอดมิน)

> `Sites.Selected` ปลอดภัยสุด แต่ต้องให้แอดมิน grant สิทธิ์แอปเข้าถึง site นั้นเพิ่มอีกสเต็ป (เช่นผ่าน `POST /sites/{site-id}/permissions`) — ทีม IT Digital น่าจะทำได้

### 3.2 ระบุปลายทาง SharePoint
- **Site**: เช่น `https://megachem.sharepoint.com/sites/Finance`
- **Document Library** (drive) ที่จะเก็บ เช่น "Invoice Archive"
- หา ID ครั้งเดียวแล้วเก็บใน config:
  - Site id: `GET https://graph.microsoft.com/v1.0/sites/megachem.sharepoint.com:/sites/Finance`
  - Drive id: `GET https://graph.microsoft.com/v1.0/sites/{site-id}/drives` → เลือก library ที่ต้องการ

> connector Microsoft 365 ที่ต่อในแชทนี้ ผมช่วยหา site id / drive id และทดสอบสิทธิ์ให้ได้ตอนคุณพร้อม

---

## 4. Config ที่จะเพิ่มใน appsettings.json (สไตล์เดียวกับ Sap/Ocr)

```json
"SharePoint": {
  "TenantId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "ClientId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "ClientSecret": "••••••••",
  "SiteId": "megachem.sharepoint.com,<guid>,<guid>",
  "DriveId": "b!....",
  "RootFolder": "Invoices",
  "Enabled": true
}
```
- อ่านเข้า `AppConfig` แบบเดียวกับ `Sap*` / `Gemini*` (ผ่าน `Get("SharePoint:...")`)
- `Enabled` = สวิตช์เปิด/ปิดฟีเจอร์ ถ้ายังไม่พร้อมก็ปิดไว้ได้

---

## 5. Microsoft Graph ที่จะเรียก (3 จุด)

### 5.1 ขอ token (client credentials)
```
POST https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={ClientId}
&client_secret={ClientSecret}
&scope=https://graph.microsoft.com/.default
&grant_type=client_credentials
```
→ ได้ `access_token` (อายุ ~1 ชม. ควร cache ไว้ใช้ซ้ำ)

### 5.2 อัปไฟล์ (ไฟล์ invoice เล็ก < 250MB → simple upload)
```
PUT https://graph.microsoft.com/v1.0/drives/{DriveId}/root:/{โฟลเดอร์}/{ชื่อไฟล์}:/content
Authorization: Bearer {access_token}
Content-Type: application/pdf

<bytes ของไฟล์>
```
- Graph สร้างโฟลเดอร์ตาม path ให้อัตโนมัติ
- ตอบกลับเป็น DriveItem → เอา field **`webUrl`** มาเก็บ

### 5.3 (ออปชัน) ตั้ง metadata/columns
ถ้า library มีคอลัมน์ (Vendor, SAP Doc No, Module) อยากเซ็ตค่าด้วย ใช้:
```
PATCH https://graph.microsoft.com/v1.0/drives/{DriveId}/items/{item-id}/listItem/fields
```

---

## 6. โครงโฟลเดอร์ + การตั้งชื่อไฟล์ (ข้อเสนอ)

```
{RootFolder}/{AP|II}/{ปี}/{ปี-เดือน}/{VendorCode}/{sapDocNo}_{docNo}_{ชื่อเดิม}.pdf
```
ตัวอย่าง:
```
Invoices/II/2026/2026-09/V-500012/5100004210_UC-25080456_PO_Attach_Oriental.pdf
```
- ใส่ **sapDocNo** ในชื่อ → อ้างอิงกลับ SAP ได้ทันที
- แยก AP (มี PO) / II (ไม่มี PO) ตาม module
- Idempotent: เอกสารเดิม archive ซ้ำ → เขียนทับ path เดิม (หรือเช็คมีอยู่แล้วให้ข้าม)

---

## 7. จุดแก้ในโค้ด (เมื่อตัดสินใจทำจริง)

Backend:
- เพิ่มไฟล์ `MgtOcr.Integrations/SharePointArchiver.cs` (หรือ `MgtOcr.Sap` ข้าง ๆ SapClient) — token + upload ผ่าน `HttpClient`
- ใน `DocumentsController` จุด **post-to-SAP สำเร็จ** (หลังได้ `sapDocNo`, ก่อน return): เรียก `SharePointArchiver.UploadAsync(...)` ใน `try/catch`
- DB: เพิ่มคอลัมน์ `SpUrl`, `SpArchivedAt`, `SpArchived (bit)` ที่ตาราง Document
- `AppConfig` + `Program.cs`: อ่าน section `SharePoint`

Frontend (ออปชัน):
- หน้า DocumentPage: ถ้ามี `spUrl` โชว์ปุ่ม/ลิงก์ "View in SharePoint"

---

## 8. Resilience & retry (สำคัญ)

- อัป SharePoint พลาด → **อย่าให้ SAP post ล้ม** — set `SpArchived = false`, log error
- ทำ retry ได้ 2 ทาง:
  1. ปุ่ม "Re-archive" ในหน้าเอกสาร(manual)
  2. **Scheduled task** รันเป็นรอบ: ดึงเอกสารที่ `Status = POSTED AND SpArchived = false` แล้วอัปซ้ำ (ทีม IT Digital ถนัด scheduled task อยู่แล้ว)

> จริง ๆ แล้ว "Scheduled task ที่หยิบเอกสาร POSTED-แต่-ยังไม่ archive ไปอัป" เป็นดีไซน์ที่ตอบโจทย์ "archive หลังโพสต์ SAP" ได้สวยมาก เพราะ **decouple + retry ฟรี** จะทำแบบนี้ล้วน หรือทำ inline (ข้อ 7) + มี scheduled task เป็นตัวเก็บตกที่พลาด ก็ได้ทั้งคู่

---

## 9. ทางเลือกโดยสรุป

| แนวทาง | ข้อดี | ข้อเสีย | เหมาะเมื่อ |
|---|---|---|---|
| **A. Inline ใน post-SAP** (แนะนำหลัก) | real-time, โค้ดเดียวจบ | ต้องแก้ backend + rebuild | อยากได้ทันทีหลังโพสต์ |
| **B. Scheduled task เก็บ POSTED** | decouple, retry ฟรี, ไม่แตะโฟลว์อัป | ไม่ทันที (หน่วงตามรอบ) | อยากเสถียร/แยกส่วน |
| C. บอท/RPA watch โฟลเดอร์ | ไม่แตะโค้ดแอป | เปราะ, จัดการไฟล์ซ้ำเอง | ทำแบบชั่วคราว |

**คำแนะนำ:** ทำ **A เป็นหลัก + B เป็นตัวเก็บตก** (retry) — ได้ทั้ง real-time และเสถียร

---

## 10. ขั้นตอนถัดไป (เมื่อพร้อม)
1. ทีม IT Digital ทำ App registration + สิทธิ์ Graph (ข้อ 3)
2. ระบุ site + library ปลายทาง → ผมช่วยหา SiteId/DriveId + ทดสอบสิทธิ์ผ่าน connector ให้
3. บอกผม → ผมลงมือทำ `SharePointArchiver` + hook เข้า post-SAP + คอลัมน์ DB + ปุ่มดูไฟล์ (ยังไม่ commit จนกว่าจะพอใจ)

*ยังไม่ได้แก้โค้ดใด ๆ — เอกสารนี้ไว้พิจารณาก่อนตัดสินใจ*
