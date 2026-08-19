@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo  ติดตั้งฐานข้อมูล MGT_Document_OCR (schema ocr)
echo ============================================
echo [1/6] สร้างตารางหลัก ...
sqlcmd -S 1P69044\SQLEXPRESS -U sa -P Install01 -C -d MGT_Document_OCR -f 65001 -b -i sql/01_schema.sql
echo [2/6] สร้างตารางการแปลงหน่วย ...
sqlcmd -S 1P69044\SQLEXPRESS -U sa -P Install01 -C -d MGT_Document_OCR -f 65001 -b -i sql/02_uom.sql
echo [3/6] ใส่ข้อมูล Master ตัวอย่าง ...
python app/tools/seed.py
echo [4/6] เพิ่มคอลัมน์รหัสของ SAP ...
sqlcmd -S 1P69044\SQLEXPRESS -U sa -P Install01 -C -d MGT_Document_OCR -f 65001 -b -i sql/03_sapcode.sql
echo [5/6] ใส่กฎการแปลงหน่วยตัวอย่าง ...
python app/tools/seed_uom.py
echo [6/6] เติมรหัสของ SAP ให้ข้อมูลตัวอย่าง ...
python app/tools/seed_sapcode.py
echo.
echo เสร็จเรียบร้อย
pause
