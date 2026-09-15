# ตั้ง Microsoft Entra ID สำหรับ MGT Document OCR

คู่มือนี้ทำครั้งเดียวตอนเริ่มใช้ SSO ใช้เวลาประมาณ 10 นาที **ไม่ต้องสร้าง client secret**
เพราะ React เป็น SPA ที่ใช้ PKCE — ไม่มีค่าลับอะไรต้องเก็บเลย

---

## 1. สร้าง App registration

**entra.microsoft.com → Applications → App registrations → New registration**

| ช่อง | ค่าที่กรอก |
|---|---|
| Name | `MGT Document OCR` |
| Supported account types | **Accounts in any organizational directory (Multitenant)** |
| Redirect URI | เลือก platform **Single-page application (SPA)** แล้วใส่ `http://localhost:5173` |

> **ทำไมต้อง Multitenant** — บริษัทที่สองในเครือคาดว่าจะอยู่คนละ tenant วันที่พร้อมจะได้แค่ให้
> admin ฝั่งเขากด consent ไม่ต้องสร้าง app ใหม่และไม่ต้องแก้โค้ด ถ้าเลือก single tenant ไปก่อน
> แล้วมาเปลี่ยนทีหลังต้องแก้ทั้ง manifest และ authority ฝั่ง React
>
> **ต้องเป็น platform SPA เท่านั้น** — ถ้าใส่ redirect URI ไว้ใต้ platform "Web" (แบบที่โปรเจกต์
> Claude chat เดิมตั้งไว้) Entra จะไม่เปิด CORS ที่ token endpoint และ MSAL จะขอ token ไม่ได้เลย

กด **Register**

## 2. จดค่ามา 2 ตัว

หน้า **Overview** จะมี — ทั้งคู่ **ไม่ใช่ค่าลับ** แปะในไฟล์ config ได้ตามปกติ

- **Application (client) ID**
- **Directory (tenant) ID**

## 3. เพิ่ม redirect URI ของ production

**Authentication → Single-page application → Add URI**

ใส่ URL จริงที่จะใช้ตอน deploy เช่น `https://ocr.megachem.co.th`
(ไม่ต้องติ๊ก implicit grant ใด ๆ — MSAL ใช้ authorization code + PKCE)

## 4. Expose an API

**Expose an API → Application ID URI → Add** → กด Save ค่า default `api://<client-id>`

จากนั้น **Add a scope**

| ช่อง | ค่า |
|---|---|
| Scope name | `access_as_user` |
| Who can consent | Admins and users |
| Admin consent display name | เข้าใช้งาน MGT Document OCR |
| Admin consent description | ให้แอปเรียก API ของ MGT Document OCR แทนผู้ใช้ |
| State | Enabled |

## 5. API permissions

**Add a permission → My APIs → MGT Document OCR → Delegated → `access_as_user` → Add**

แล้วกด **Grant admin consent for Megachem** เพื่อให้ผู้ใช้ไม่ต้องเจอหน้า consent ตอนล็อกอินครั้งแรก

(`Microsoft Graph → User.Read` ติดมาให้อยู่แล้วโดยอัตโนมัติ เพียงพอสำหรับชื่อและอีเมล)

---

## 6. กรอกค่ากลับเข้าระบบ

### `backend/src/MgtOcr.Api/appsettings.json`

```jsonc
"AzureAd": {
  "Audience": "api://<client-id>",
  "UserDatabase": "MGT_Datawarehouse",
  "DevFallbackEmail": "",
  "Tenants": [
    { "Name": "Megachem", "TenantId": "<tenant-id>", "ClientId": "<client-id>", "CompanyId": 1 },
    { "Name": "<บริษัทที่สอง>", "TenantId": "", "ClientId": "", "CompanyId": 2 }
  ]
}
```

`CompanyId` คือค่าใน `MGT_Datawarehouse.dbo.Ms_Company.CompanyID` — ยังไม่ถูกใช้ในเฟสนี้
แต่กรอกไว้เลยเพราะเฟส 2 (แยกข้อมูลตามบริษัท) จะใช้

แถวที่ `TenantId` ว่างจะถูกข้ามไปเฉย ๆ ไม่ error

### `webfront/.env.local`

```
VITE_AZURE_CLIENT_ID=<client-id>
```

แค่บรรทัดเดียว ที่เหลือมีค่า default ให้แล้ว

---

## 7. วันที่บริษัทที่สองพร้อม

1. เติม `TenantId` + `ClientId` (ใช้ client id ตัวเดิม) ของแถวที่สองใน `appsettings.json`
2. ให้ admin ของ tenant นั้นเปิดลิงก์นี้ครั้งเดียวเพื่อ consent
   ```
   https://login.microsoftonline.com/<tenant-id-ของเขา>/adminconsent?client_id=<client-id>
   ```
3. เพิ่ม user ของเขาใน `Ms_User` + `Ms_UserCompany`

ไม่ต้องแก้โค้ด ไม่ต้อง deploy ใหม่ (แค่ restart เพื่ออ่าน config)

---

## เช็กว่าทำงานถูกไหม

1. `npm run dev` แล้วเปิด `http://localhost:5173` → ต้องเจอหน้า **เข้าสู่ระบบด้วยบัญชี Microsoft**
2. กดล็อกอิน → เด้งไป Microsoft แล้วกลับมา
3. มุมล่างซ้ายของเมนูต้องขึ้น **ชื่อจริง + อีเมล + ชื่อบริษัท** จาก `Ms_User`
4. เปิด DevTools → Network → ทุก request ไป `/api/*` ต้องมี header `Authorization: Bearer …`

## เจอปัญหา

| อาการ | สาเหตุ |
|---|---|
| `AADSTS50011` redirect URI mismatch | URL ที่เปิดอยู่ไม่ตรงกับที่ลงทะเบียน — ตรวจให้ตรงเป๊ะรวม `http`/`https` และพอร์ต |
| ล็อกอินผ่าน แต่ทุก API ตอบ **401** | `aud` ไม่ตรง — มักเกิดจากยังไม่ได้ตั้ง Application ID URI หรือ scope ชื่อไม่ตรง |
| ล็อกอินผ่าน แต่ตอบ **403 “ไม่พบอีเมลนี้ใน Ms_User”** | อีเมลที่ใช้ล็อกอิน M365 ไม่ตรงกับ `Ms_User.Email` / `TokenEmail` หรือ `IsActive = 0` — **ดู log ของ backend จะบอกอีเมลที่ระบบพยายามหาให้เลย** |
| ขึ้นหน้า consent ทุกครั้ง | ยังไม่ได้กด Grant admin consent ในขั้นที่ 5 |
| MSAL error เรื่อง CORS ตอนขอ token | redirect URI อยู่ใต้ platform **Web** ไม่ใช่ **SPA** — ย้ายในหน้า Authentication |

## ปิด SSO ชั่วคราว (เฉพาะตอน dev)

ลบค่า `VITE_AZURE_CLIENT_ID` และเคลียร์ `TenantId` ทุกแถว → ระบบกลับไปทำงานแบบไม่มีล็อกอินเหมือนเดิม
ตั้ง `AzureAd:DevFallbackEmail` เป็นอีเมลใน `Ms_User` สักคนเพื่อให้ยังมีชื่อไปลง audit log ได้

**backend จะไม่ยอมสตาร์ทถ้าไม่มี tenant และไม่ใช่ Development** — กันการเผลอ deploy ขึ้น production
โดยที่ API เปิดโล่ง
