@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo  MGT Document OCR  -^>  SAP S/4HANA
echo ============================================
python -m pip install -q -r requirements.txt
echo.
echo เปิดเบราว์เซอร์ที่ http://localhost:8090
echo กด Ctrl+C เพื่อหยุดเซิร์ฟเวอร์
echo.
start "" http://localhost:8090
python -m uvicorn app.main:app --host 0.0.0.0 --port 8090
