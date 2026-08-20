"""โหลดค่าตั้งค่าจาก .env"""
import os
from pathlib import Path
from dotenv import load_dotenv

BASE_DIR = Path(__file__).resolve().parent.parent
load_dotenv(BASE_DIR / ".env")

def _get(key: str, default: str = "") -> str:
    return (os.getenv(key) or default).strip()

DB_SERVER   = _get("DB_SERVER", r"1P69044\SQLEXPRESS")
DB_NAME     = _get("DB_NAME", "MGT_Document_OCR")
DB_USER     = _get("DB_USER", "sa")
DB_PASSWORD = os.getenv("DB_PASSWORD", "")
DB_DRIVER   = _get("DB_DRIVER", "ODBC Driver 17 for SQL Server")

APP_HOST = _get("APP_HOST", "0.0.0.0")
APP_PORT = int(_get("APP_PORT", "8080"))

OWN_COMPANY_KEYWORDS = [x.strip() for x in _get("OWN_COMPANY_KEYWORDS", "MEGACHEM").split(",") if x.strip()]
OWN_TAX_ID = _get("OWN_TAX_ID")

OCR_PROVIDER  = _get("OCR_PROVIDER", "auto").lower()
_default_tess = r"C:\Program Files\Tesseract-OCR\tesseract.exe"
TESSERACT_CMD = _get("TESSERACT_CMD") or (_default_tess if Path(_default_tess).exists() else "")
_default_tessdata = str(BASE_DIR / "tessdata")
TESSDATA_PREFIX = _get("TESSDATA_PREFIX") or (_default_tessdata if Path(_default_tessdata).is_dir() else "")
AZURE_DI_ENDPOINT = _get("AZURE_DI_ENDPOINT")
AZURE_DI_KEY      = _get("AZURE_DI_KEY")
ANTHROPIC_API_KEY = _get("ANTHROPIC_API_KEY")
ANTHROPIC_MODEL   = _get("ANTHROPIC_MODEL", "claude-sonnet-5")
TYPHOON_API_KEY   = _get("TYPHOON_API_KEY")
TYPHOON_MODEL     = _get("TYPHOON_MODEL", "typhoon-ocr")
GEMINI_API_KEY    = _get("GEMINI_API_KEY")
GEMINI_MODEL      = _get("GEMINI_MODEL", "gemini-3.6-flash")

SAP_BASE_URL      = _get("SAP_BASE_URL")
SAP_USER          = _get("SAP_USER")
SAP_PASSWORD      = os.getenv("SAP_PASSWORD", "")
SAP_CLIENT        = _get("SAP_CLIENT", "100")
SAP_COMPANY_CODE  = _get("SAP_COMPANY_CODE", "1000")
SAP_DEFAULT_PLANT = _get("SAP_DEFAULT_PLANT", "1000")

UPLOAD_DIR = BASE_DIR / "uploads"
UPLOAD_DIR.mkdir(exist_ok=True)
PUBLIC_DIR = BASE_DIR / "public"

CONN_STR = (
    f"Driver={{{DB_DRIVER}}};Server={DB_SERVER};Database={DB_NAME};"
    f"Uid={DB_USER};Pwd={DB_PASSWORD};TrustServerCertificate=yes;"
)
