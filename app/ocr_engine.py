"""
ตัวอ่านเอกสาร (Document Extraction)
ลำดับการทำงานของโหมด auto:
  1) PDF ที่มีชั้นข้อความ  -> ดึงข้อความตรง ๆ ด้วย pdfplumber (แม่นที่สุด ไม่ต้อง OCR)
  2) ไฟล์รูป / PDF สแกน    -> Tesseract OCR (ถ้าติดตั้งไว้)
  3) Azure Document Intelligence (ถ้าตั้งค่า AZURE_DI_* ใน .env)
  4) โหมดสาธิต (demo)      -> ใช้ชุดข้อมูลตัวอย่าง เพื่อให้ทดสอบ flow ได้ทันที
ผลลัพธ์: {"header": {...}, "lines": [...], "confidence": float, "provider": str, "rawText": str}
"""
from __future__ import annotations

import re
import shutil
from pathlib import Path

from . import config

IMAGE_EXT = {".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp"}

# ---------------------------------------------------------------- helpers
THAI_MONTHS = {
    "ม.ค": 1, "มกราคม": 1, "ก.พ": 2, "กุมภาพันธ์": 2, "มี.ค": 3, "มีนาคม": 3,
    "เม.ย": 4, "เมษายน": 4, "พ.ค": 5, "พฤษภาคม": 5, "มิ.ย": 6, "มิถุนายน": 6,
    "ก.ค": 7, "กรกฎาคม": 7, "ส.ค": 8, "สิงหาคม": 8, "ก.ย": 9, "กันยายน": 9,
    "ต.ค": 10, "ตุลาคม": 10, "พ.ย": 11, "พฤศจิกายน": 11, "ธ.ค": 12, "ธันวาคม": 12,
}
NUM = r"[\d,]+(?:\.\d+)?"


def th(s: str) -> str:
    """สร้าง pattern จับคำภาษาไทย ทนต่อ Tesseract OCR ที่มักแทรกช่องว่างระหว่างอักขระทุกตัว
    เช่น 'ภาษีมูลค่าเพิ่ม' -> ยังจับ 'ภา ษ ี มู ล ค ่ า เพ ิ ่ ม' ได้ด้วย"""
    return r"\s*".join(re.escape(c) for c in s)


def _clean_desc(s: str) -> str:
    """รวมช่องว่างที่ Tesseract แทรกระหว่างอักขระไทย (เช่น 'ค ่ า บ ร ิ ก า ร' -> 'ค่าบริการ')
    แต่คงช่องว่างระหว่างคำอังกฤษ/ตัวเลขไว้ตามเดิม เพื่อให้คำอธิบายรายการอ่านง่ายขึ้น
    รวมถึงตัด '.' เดี่ยว ๆ ที่ OCR แทรกมั่วระหว่างคำไทย (เช่น 'ล้าง .เครื่อง' -> 'ล้างเครื่อง')"""
    s = re.sub(r"(?<=[ก-๙])\s*\.\s*(?=[ก-๙])", "", s)
    return re.sub(r"(?<=[ก-๙])\s+(?=[ก-๙])", "", s).strip()


def _f(s) -> float:
    try:
        return float(str(s).replace(",", "").strip())
    except (TypeError, ValueError):
        return 0.0


def _iso_date(day: int, month: int, year: int) -> str:
    if year < 100:
        year += 2500 if year > 40 else 2000
    if year > 2400:          # พ.ศ. -> ค.ศ.
        year -= 543
    try:
        return "%04d-%02d-%02d" % (year, month, day)
    except Exception:
        return ""


DATE_RE = re.compile(r"(?<![\d\-/])(\d{1,2})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{4}|\d{2})(?![\d\-/])")


def _valid_dmy(d: int, m: int, y: int) -> bool:
    return 1 <= d <= 31 and 1 <= m <= 12 and (y >= 1900 or y < 100 or y > 2400)


def find_dates(text: str) -> list[str]:
    """คืนวันที่ทุกตัวที่เจอ (เรียงตามที่ปรากฏ) — กันไม่ให้จับรหัสอย่าง T11-26-03-16-09"""
    out = []
    for m in DATE_RE.finditer(text):
        d, mo, y = int(m.group(1)), int(m.group(2)), int(m.group(3))
        if not _valid_dmy(d, mo, y):
            continue
        iso = _iso_date(d, mo, y)
        if iso:
            out.append(iso)
    for m in re.finditer(r"(\d{1,2})\s+([ก-๙.]{2,12})\s+(\d{2,4})", text):
        mon = next((v for k, v in THAI_MONTHS.items() if m.group(2).startswith(k)), 0)
        if mon:
            iso = _iso_date(int(m.group(1)), mon, int(m.group(3)))
            if iso:
                out.append(iso)
    return out


def find_date(text: str, labels: list[str] | None = None) -> str:
    """หาวันที่ — ถ้าระบุ labels จะมองหาวันที่ที่อยู่หลังคำนั้นก่อน"""
    for lb in labels or []:
        m = re.search(lb + r"[^0-9]{0,20}" + DATE_RE.pattern, text, re.I)
        if m:
            d, mo, y = int(m.group(1)), int(m.group(2)), int(m.group(3))
            if _valid_dmy(d, mo, y):
                return _iso_date(d, mo, y)
    ds = find_dates(text)
    return ds[0] if ds else ""


TAX_LABEL = (th("เลขประจำตัวผู้เสียภาษี") + "|" + th("เลขทะเบียนนิติบุคคล") +
             r"|Tax\s*(?:ID|Id|No)(?:\s*Number)?|TIN")
TAX13 = re.compile(r"(?<!\d)(\d[\s\-]?){13}(?!\d)")


def find_tax_id(text: str) -> str:
    """หาเลข 13 หลักหลังคำว่า Tax ID — เผื่อกรณีที่มีอักขระขยะ เช่น (cid:12) คั่นอยู่"""
    for m in re.finditer(TAX_LABEL, text, re.I):
        window = text[m.end():m.end() + 250]
        t = TAX13.search(window)
        if t:
            return re.sub(r"\D", "", t.group(0))
    t = TAX13.search(re.sub(r"[\s\-]", "", text))
    return re.sub(r"\D", "", t.group(0)) if t else ""


LEGAL = r"บริษัท|ห้างหุ้นส่วน|หจก\.|Co\.\s*,?\s*Ltd|Company|Corporation|Limited|PCL|CO\.,LTD"

# ดึงเฉพาะ "ช่วงที่เป็นชื่อนิติบุคคล" ออกจากบรรทัด — ฝั่งอังกฤษบังคับตัวพิมพ์ใหญ่
# เพื่อไม่ให้ประโยคทั่วไปอย่าง "... given to a company" ถูกมองเป็นชื่อบริษัท
NAME_EN = re.compile(
    r"[A-Z][A-Z0-9&.,'()\-/ ]{3,60}?"
    r"(?:PUBLIC\s+COMPANY\s+LIMITED|COMPANY\s+LIMITED|CO\.\s*,?\s*LTD\.?|CORPORATION|"
    r"LIMITED\s+PARTNERSHIP|PCL\.?|LTD\.?|LIMITED\b)")
NAME_TH = re.compile(r"(?:บริษัท|ห้างหุ้นส่วนจำกัด|หจก\.)[^\n]{3,70}?"
                     r"(?:จำกัด(?:\s*\(มหาชน\))?|จก\.)")


# ชื่อแบบ Title Case เช่น "Universal Chemical Supply Co., Ltd."
# ใช้ต่อเมื่อไม่พบแบบตัวพิมพ์ใหญ่ล้วน (case-sensitive จึงไม่จับประโยค "... to a company")
NAME_MIXED = re.compile(
    r"[A-Z][A-Za-z0-9&.,'()\-/ ]{3,60}?"
    r"(?:Co\.\s*,?\s*Ltd\.?|Public\s+Company\s+Limited|Company\s+Limited|Corporation|PCL\.?|"
    r"Limited\b|Ltd\.?\b)")


def company_candidates(line: str) -> list[str]:
    out = [m.group(0) for m in NAME_TH.finditer(line)]
    out += [m.group(0) for m in NAME_EN.finditer(line)]
    if not out:
        out += [m.group(0) for m in NAME_MIXED.finditer(line)]
    return out


def is_own_company(s: str) -> bool:
    up = str(s or "").upper()
    return any(k.upper() in up for k in config.OWN_COMPANY_KEYWORDS if k)


def _clean_company(s: str) -> str:
    # (?![A-Za-z]) กัน "TOYO" / "TOA" ถูกตัดคำว่า TO ออกหน้า
    s = re.sub(r"^\s*(ATTN|SHIP\s*-?\s*TO|BILL\s*-?\s*TO|SOLD\s*-?\s*TO|VENDOR|TO|FOR|เรียน|ถึง)"
               r"(?![A-Za-z])\s*[:.]?\s*", "", s, flags=re.I)
    s = re.sub(r"\((HEAD\s*OFFICE|BRANCH[^)]*|สำนักงานใหญ่|สาขา[^)]*)\)", "", s, flags=re.I)
    s = re.sub(r"\(\s*\d{6,}\s*\)", "", s)          # รหัสคู่ค้าในระบบของเขา
    s = re.sub(r"[,\s]+$", "", re.sub(r"\s{2,}", " ", s)).strip()
    return s[:200]


def find_company_with_pos(text: str, exclude_own: bool = True) -> tuple[str, int]:
    """เลือกชื่อนิติบุคคลของ 'คู่ค้า' — ตัดบริษัทตัวเองและบรรทัด ATTN ออก
    แล้วเลือกชื่อที่ปรากฏบ่อยที่สุด (ชื่อที่มีคำลงท้ายนิติบุคคลได้คะแนนพิเศษ)
    คืนค่าเลขบรรทัดที่พบด้วย เพื่อใช้ไปหาที่อยู่ที่ตามหลังชื่อ"""
    score: dict = {}
    for pos, raw in enumerate(text.splitlines()):
        # ข้ามบรรทัดที่เป็นข้อมูลธนาคารสำหรับโอนเงิน (เช่น 'Bank Name: Siam Commercial Bank
        # Public Company Limited') ซึ่งไม่ใช่ชื่อคู่ค้า แต่มีคำลงท้ายนิติบุคคลทำให้ regex จับผิด
        if re.search(r"Bank\s*(?:Name|Account)|Account\s*(?:No|Name)|Swift\s*Code|"
                    + th("ชื่อธนาคาร") + "|" + th("เลขที่บัญชี"), raw, re.I):
            continue
        for cand in company_candidates(_clean_company(raw)):
            s = _clean_company(cand)
            if len(s) < 6:
                continue
            if exclude_own and is_own_company(s):
                continue
            if re.match(r"^Bank\b", s, re.I):
                continue
            key = re.sub(r"[^A-Z0-9ก-๙]", "", s.upper())
            cnt, name, first = score.get(key, (0, s, pos))
            # ชื่อที่อยู่ต้นเอกสาร (หัวจดหมาย) มีโอกาสเป็นคู่ค้ามากกว่า
            score[key] = (cnt + 1, name if len(name) >= len(s) else s, first)
    if not score:
        return "", -1
    best = max(score.values(), key=lambda v: (v[0], -v[2]))
    return best[1], best[2]


def find_company(text: str, exclude_own: bool = True) -> str:
    return find_company_with_pos(text, exclude_own)[0]


def _address_after(text_lines: list[str], pos: int) -> str:
    """เก็บ 2-3 บรรทัดถัดจากชื่อบริษัท มาเป็นที่อยู่ — หยุดเมื่อเจอป้ายกำกับอื่น"""
    out = []
    if pos < 0:
        return ""
    for ln in text_lines[pos + 1:pos + 6]:
        s = ln.strip()
        if not s:
            break
        if re.search(r"TAX\s*ID|เลขประจำตัว|BRANCH|สาขา|^SHIP\b|VENDOR|ATTN", s, re.I):
            break
        out.append(s)
        if len(out) >= 3:
            break
    return " ".join(out)[:400]


_ISOLATED_NUM = re.compile(r"(?<![A-Za-z0-9./,])" + NUM + r"(?![A-Za-z0-9./%])")


def _plausible_amount(v: str) -> bool:
    """ยอดเงินจริงมักมีจุดทศนิยมหรือตัวคั่นหลักพัน หรืออย่างน้อยมีค่ามากพอสมควร
    กันไม่ให้จับเลขลำดับ/รหัสสั้น ๆ ที่บังเอิญอยู่ใกล้ป้ายกำกับ (เช่น เลข '1' ต้นแถวตารางถัดจากหัวคอลัมน์)"""
    return "." in v or "," in v or _f(v) >= 10


def find_amount(text: str, keys: list[str]) -> float:
    """หายอดเงินที่อยู่ใกล้คำสำคัญ — ข้ามตัวเลขที่เป็นอัตราภาษี เช่น 'VAT 7%  4,690.00'
    และไม่จับตัวเลขที่ติดอยู่ในรหัส เช่น 'QP03209' (ต้องไม่มีตัวอักษรนำหน้าทันที)
    ลองหา "ป้ายกำกับ...ค่า" ก่อนเสมอ ถ้าไม่พบจึงลองมองค่าที่อยู่ "ก่อน" ป้ายกำกับแทน
    (เอกสารที่อ่านด้วย OCR บางฉบับ ลำดับการอ่านสลับคอลัมน์ ทำให้ตัวเลขโผล่มาก่อนป้ายกำกับ)
    ถ้าป้ายกำกับซ้ำหลายที่ (เช่น หัวตารางกับยอดรวมจริง) เลือกตัวที่ "สมเหตุผลที่สุดตัวสุดท้าย"
    เพราะยอดรวมจริงมักอยู่ท้ายเอกสารเสมอ หลังรายการย่อยต่าง ๆ"""
    for k in keys:
        best = None
        for m in re.finditer(k + r"[^0-9\-]{0,40}(?:\d{1,2}(?:\.\d+)?\s*%[^0-9\-]{0,20})?"
                             r"(?<![A-Za-z])(" + NUM + r")", text, re.I):
            if _plausible_amount(m.group(1)):
                best = m.group(1)
        if best:
            return _f(best)
        lm = list(re.finditer(k, text, re.I))
        if lm:
            window = text[max(0, lm[-1].start() - 60):lm[-1].start()]
            nums = [n for n in _ISOLATED_NUM.findall(window) if _plausible_amount(n)]
            if nums:
                return _f(nums[-1])
    return 0.0


def find_doc_no(text: str, keys: list[str]) -> str:
    """หมายเหตุ: ตัวคั่นระหว่างป้ายกำกับกับค่าตั้งใจไม่รวม '.' — ถ้ารวมด้วย จะข้ามจุดจบประโยค
    แล้วไปจับคำ/ตัวเลขของประโยคถัดไปมาผิด ๆ เช่น '...number.\\n3.Indent...' จะได้ '3.Indent'"""
    fallback = ""
    for k in keys:
        for m in re.finditer(k + r"[\s:#\-]*([A-Z0-9][A-Z0-9\-/_.]{3,24})", text, re.I):
            v = m.group(1).strip(" .-/")
            if DATE_RE.fullmatch(v) or re.fullmatch(r"[\d/.\-]{8,10}", v) and v.count("/") == 2:
                continue                                   # เป็นวันที่ ไม่ใช่เลขเอกสาร
            if re.fullmatch(r"(NO|NUMBER|DATE|ADDRESS)", v, re.I):
                continue
            # ป้ายกำกับที่มีคำว่า "RECEIPT" ปนอยู่ใกล้ ๆ (เช่น "RECEIPT/TAX INVOICE NO") มักหมายถึง
            # เลขที่ใบเสร็จ ไม่ใช่เลขที่ใบแจ้งหนี้ตัวจริง — ถ้ามีป้ายกำกับแบบไม่มีคำนี้ปนอยู่ที่อื่น ให้เลือกอันนั้นก่อน
            if re.search(r"RECEIPT", text[max(0, m.start() - 20):m.start()], re.I):
                fallback = fallback or v
                continue
            return v
    return fallback


UOMS = {"KG", "G", "L", "ML", "M", "PC", "PCS", "EA", "BAG", "DRUM", "SET", "BOX", "TON",
        "AU", "กก.", "กรัม", "ลิตร", "ชิ้น", "ถุง", "ถัง", "กล่อง", "ตัน",
        # หน่วยงานบริการที่พบบ่อยในใบแจ้งหนี้ Non-Trade (ค่าบริการ/ค่าเช่า ไม่ใช่สินค้าที่นับเป็นชิ้น)
        "งาน", "นาย", "เดือน", "ครั้ง", "คน", "ชม.", "วัน", "สัญญา"}
UOM_ALIAS = {"KILOGRAM": "KG", "KILOGRAMS": "KG", "KILOGRAMME": "KG", "KILOGRAMMES": "KG",
             "LITER": "L", "LITERS": "L", "LITRE": "L", "LITRES": "L",
             "GRAM": "G", "GRAMS": "G", "PIECE": "EA", "PIECES": "EA", "UNIT": "EA", "UNITS": "EA"}
_NUM_TOKEN = re.compile(r"^-?[\d,]+(?:\.\d+)?$")   # อนุญาตเครื่องหมายลบ เช่น ส่วนลดติดลบ '-48.00'
_CODE_TOKEN = re.compile(r"^[A-Z0-9][A-Z0-9\-_/.]{3,24}$")


# คำที่บ่งชี้ว่าเป็นบรรทัดสรุปยอด/หัวตาราง ไม่ใช่รายการสินค้า/บริการจริง — ใช้กรองทั้งตอนหาแถว
# และตอนหาคำอธิบาย (กันไม่ให้ไปหยิบหัวคอลัมน์ เช่น "รายละเอียด จำนวน ราคาต่อหน่วย" มาเป็นชื่อสินค้า)
# ห่อคำภาษาไทยด้วย th() เสมอ เพราะ OCR มักแทรกช่องว่างระหว่างอักขระ (เช่น "ร ว ม")
_LINE_SKIP = (th("รวม") + "|Total|" + th("ภาษี") + "|VAT|Sub\\s*-?\\s*total|" + th("ยอด") +
             "|Grand|^Item\\b|Description\\b|" + th("รายละเอียด") + "|^" + th("จำนวน") + r"\b|^" +
             th("ราคา") + r"\b|" + th("หน่วย") + "|" + th("ลำดับที่") + "|Quantity\\b|Unit\\s*Price\\b" +
             "|Withholding|" + th("หัก") + r"\s*" + th("ณ") + r"\s*" + th("ที่จ่าย") +
             r"|^Amount\b|^Net\s*Payment\b|^" + th("จำนวนเงิน") + r"\b|^Date\b|^" + th("วันที่") + r"\b" +
             r"|Signature|" + th("ลงนาม") + "|" + th("ผู้มีอำนาจ"))


def parse_lines(text: str) -> list[dict]:
    """หาแถวรายการสินค้า: บรรทัดที่มีตัวเลขท้ายแถว >= 3 ตัว (จำนวน / ราคา / จำนวนเงิน)
    ทำงานระดับ token เพื่อไม่ให้การตัดหน่วย/ตัวเลขไปกินตัวอักษรกลางคำ
    รองรับฟอร์มที่มีคอลัมน์แทรก (PACKING/DISC/เลขที่อ้างอิง/วันที่) โดยหาชุด (จำนวน x ราคา = จำนวนเงิน)
    ที่สมเหตุผลจากตัวเลขทั้งหมดในแถว แทนที่จะเดาตำแหน่งตายตัว"""
    raw_lines = text.splitlines()
    out: list[dict] = []
    for li, ln in enumerate(raw_lines):
        toks = ln.split()
        if len(toks) < 4:
            continue
        s = " ".join(toks)
        if re.search(_LINE_SKIP, s, re.I):
            continue
        # ตัดเลขลำดับรายการนำหน้าออกก่อนหาชุด (จำนวน x ราคา = จำนวนเงิน) เช่น "1 ค่าบริการล้าง... 9,891.00 9,891.00"
        # ไม่งั้นตัวค้นหาจะเข้าใจผิดจับ "1" (เลขลำดับ) เป็น "จำนวน" แทน — เพราะ 1 คูณอะไรก็ได้ค่าเดิม (trivial match)
        # ทำให้กินตำแหน่งซ้ายสุดจนไม่เหลือ token ใดเป็นคำอธิบายเลย (สังเกตจาก "ราคา"=="จำนวนเงิน" พอดี)
        if (len(toks) >= 5 and re.fullmatch(r"\d{1,3}", toks[0])
                and not _NUM_TOKEN.match(toks[1])):
            toks = toks[1:]
        idx = [i for i, t in enumerate(toks) if _NUM_TOKEN.match(t)]
        if len(idx) < 3:
            continue
        # หาแถว (จำนวน x ราคา = จำนวนเงิน) — เริ่มค้นจากตัวเลข "ขวาสุด" ก่อนเสมอ เพราะคอลัมน์
        # จำนวนเงินอยู่ทางขวาสุดของแถวในฟอร์มแทบทุกแบบ กัน false-positive จากตัวเลขอื่นที่บังเอิญ
        # คูณกันได้ใกล้เคียง (เช่น เลขลำดับ x ราคา ≈ ตัวเลขที่แทรกอยู่กลางชื่อสินค้า)
        # นอกจาก "คูณ" ยังลอง "ลบ" ด้วย (ค่าบริการ - ส่วนลด = ยอดสุทธิ) ซึ่งพบบ่อยในบิลค่าบริการ/โทรคมนาคม
        # เช่น 'ค่าบริการ YTEL 1234  78.00  -48.00  30.00' (78-48=30)
        triple, rel = None, "mul"
        for c in range(len(idx) - 1, -1, -1):
            amt = _f(toks[idx[c]])
            # กันตัวเลขยาวผิดปกติ (เลขผู้เสียภาษี 13 หลัก/เบอร์โทร/รหัสอ้างอิง) ถูกเข้าใจผิดว่าเป็นยอดเงิน
            if amt == 0 or len(re.sub(r"\D", "", toks[idx[c]])) > 9:
                continue
            found, frel = None, "mul"
            for a in range(c):
                for b in range(a + 1, c):
                    q, p = _f(toks[idx[a]]), _f(toks[idx[b]])
                    tol_mul = max(0.5, abs(amt) * 0.01)
                    # ความสัมพันธ์แบบลบ จำกัด tolerance สัมบูรณ์ไว้ไม่เกิน 2 บาท (ต่างจากคูณ) เพราะ
                    # ผลต่างของเลขสองตัวที่ใหญ่ (เช่น 2569 กับ 18) อาจบังเอิญตกในช่วง 1% ของยอดใหญ่ได้ง่าย
                    # ทั้งที่ไม่เกี่ยวข้องกัน (เช่น เลขปี พ.ศ. ที่ปนมาจากวันที่ในแถวเดียวกัน)
                    tol_sub = min(tol_mul, 2.0)
                    if q > 0 and p > 0 and abs(q * p - amt) <= tol_mul:
                        found, frel = (idx[a], idx[b]), "mul"; break
                    if q > 0 and abs(q - p - amt) <= tol_sub:          # เต็ม - ส่วนลด(บวก) = สุทธิ
                        found, frel = (idx[a], idx[b]), "sub"; break
                    if q > 0 and p < 0 and abs(q + p - amt) <= tol_sub:  # เต็ม + ส่วนลด(ติดลบอยู่แล้ว) = สุทธิ
                        found, frel = (idx[a], idx[b]), "sub"; break
                if found:
                    break
            if found:
                triple, rel = (found[0], found[1], idx[c]), frel
                break
        if not triple:
            continue
        i_qty, i_price, i_amt = triple
        if rel == "mul":
            qty, price, amount = _f(toks[i_qty]), _f(toks[i_price]), _f(toks[i_amt])
        else:
            # ความสัมพันธ์แบบลบ (ค่าบริการ - ส่วนลด = สุทธิ): มองเป็น 1 หน่วย ราคาเต็ม = ค่าบริการ,
            # จำนวนเงิน = ยอดสุทธิหลังหักส่วนลด
            qty, price, amount = 1.0, _f(toks[i_qty]), _f(toks[i_amt])
        # หน่วย: มองหาถัดจากคอลัมน์จำนวน (หรือที่ไหนก็ได้ในแถวถ้าไม่เจอ) — รองรับหน่วยแบบเต็มคำ เช่น Kilogram
        def _uom_of(t):
            u = t.upper()
            return UOM_ALIAS.get(u) or (u if u in UOMS else "")
        uom = (next(filter(None, (_uom_of(t) for t in toks[i_qty + 1:])), "")
               or next(filter(None, (_uom_of(t) for t in toks)), ""))
        # วันกำหนดส่ง: date token ที่ไหนก็ได้ในแถว
        due = ""
        for t in toks:
            if DATE_RE.fullmatch(t):
                d = find_dates(t)
                if d:
                    due = d[0]
                    break
        # คำบรรยาย = ทุก token ก่อนคอลัมน์ตัวเลขตัวแรก — ตัดเลขลำดับ/วันที่/ขนาดบรรจุที่นำหน้าออก
        # (ตัดเฉพาะ "ตัวเลขล้วน" ที่อยู่ต้นสุด เพื่อไม่ให้กระทบตัวเลขที่แทรกกลางชื่อสินค้า
        #  เช่น "MEK 99.5 PCT" ซึ่ง 99.5 ไม่ได้อยู่ตำแหน่งแรก)
        rest = [t for t in toks[:min(i_qty, i_price, i_amt)] if t != "|"]   # เอาตัวคั่นคอลัมน์ (Typhoon OCR) ออก
        while rest and (DATE_RE.fullmatch(rest[0]) or _NUM_TOKEN.match(rest[0])):
            rest.pop(0)
        code = ""
        if rest and _CODE_TOKEN.match(rest[0]) and not DATE_RE.fullmatch(rest[0]) and (
                re.search(r"[-_/]", rest[0]) or re.fullmatch(r"[A-Z]{1,5}\d{2,}", rest[0])):
            code = rest.pop(0)
        desc = " ".join(rest)[:300]
        if not desc:
            # บางฟอร์มแยกรหัส+ชื่อสินค้าไว้บรรทัด "ก่อนหน้า" แถวตัวเลข เช่น
            # "00001  200338  Cetearyl alcohol 1618" แล้วค่อยตามด้วยแถว "11,000.000 Kilogram ..."
            for prv in reversed(raw_lines[max(0, li - 2):li]):
                pt = prv.strip()
                if not pt or re.search(_LINE_SKIP, pt, re.I):
                    break
                ptoks = pt.split()
                if len(ptoks) > 1 and re.fullmatch(r"0*\d{1,6}", ptoks[0]):
                    ptoks.pop(0)                                 # เลขลำดับรายการ
                if not code and ptoks and re.fullmatch(r"[A-Z0-9]{4,15}", ptoks[0], re.I):
                    code = ptoks.pop(0)                          # รหัสสินค้า
                if ptoks:
                    desc = " ".join(ptoks)[:300]
                break
        if not desc:
            # บางฟอร์มแยกรายละเอียดสินค้าไว้บรรทัดถัดไป (โค้ด+ตัวเลขบรรทัดหนึ่ง, ชื่อสินค้าอีกบรรทัด)
            for nxt in raw_lines[li + 1:li + 3]:
                nt = nxt.strip()
                if not nt or re.search(_LINE_SKIP, nt, re.I):
                    break
                digit_ratio = len(re.findall(r"\d", nt)) / max(1, len(nt))
                if digit_ratio > 0.3:
                    break                                       # แถวถัดไปเป็นรายการใหม่ ไม่ใช่คำบรรยาย
                desc = nt[:300]
                break
        elif desc:
            # บางฟอร์มขึ้นบรรทัดใหม่กลางคำอธิบายเพราะชื่อยาวเกินความกว้างคอลัมน์ เช่น
            # "ค่าบริการล้าง 1.00 EA 9,891.00 9,891.00" ตามด้วยบรรทัดถัดไป "เครื่องปรับอากาศ(6/6)"
            # ซึ่งเป็นคำต่อ ไม่ใช่รายการใหม่ — ต่อท้ายถ้าบรรทัดถัดไปเป็นข้อความสั้น ๆ ไม่มียอดเงินของตัวเอง
            for nxt in raw_lines[li + 1:li + 3]:
                nt = nxt.strip()
                if not nt:
                    continue
                if re.search(_LINE_SKIP, nt, re.I) or _NOT_AMOUNT_CONTEXT.search(nt):
                    break
                if any(_NUM_TOKEN.match(t) and _looks_like_money(t) for t in nt.split()):
                    break                                       # มียอดเงินของตัวเอง แปลว่าเป็นแถวใหม่ ไม่ใช่คำต่อ
                desc = (_clean_desc(desc) + _clean_desc(nt))[:300]
                break
        desc = _clean_desc(desc)
        out.append({"extCode": code, "desc": desc, "qty": qty, "dueDate": due,
                    "uom": uom or "EA", "price": price, "amount": amount})

    if not out:
        out = _parse_single_amount_lines(raw_lines)
    if not out:
        out = _parse_desc_then_amount_lines(raw_lines)
    return out[:60]


# บรรทัดที่มักมีตัวเลขโดด ๆ ตัวเดียวแต่ "ไม่ใช่" ยอดเงิน — เลขทะเบียน/รหัสไปรษณีย์/เบอร์โทร/ที่อยู่
# (ตั้งใจไม่รวม "ประจำเดือน"/"ปี" เพราะเป็นคำที่ใช้ในคำอธิบายรายการบริการทั่วไปด้วย เช่น
#  "ค่าบริการ...ประจำเดือนพฤษภาคม" ซึ่งเป็นรายการจริงที่ต้องการจับ ไม่ใช่บรรทัดที่ต้องกรองทิ้ง)
_NOT_AMOUNT_CONTEXT = re.compile(
    th("เลขประจำตัว") + "|" + th("เลขทะเบียน") + r"|Tax\s*ID|" + th("ที่อยู่") + "|" + th("แขวง") +
    "|" + th("เขต") + "|" + th("ถนน") + "|" + th("ซอย") + "|" + th("หมู่") + "|" + th("โทร") +
    r"|Tel\b|Fax\b|E-?mail|www\.|" + th("เลขที่") + r"|No\.|" + th("รหัส") + r"|Ref\.|Branch|" +
    th("สาขา"), re.I)


def _looks_like_money(v: str) -> bool:
    """ตัวเลขที่ 'ดูเหมือน' จำนวนเงินจริง ต้องมีตัวคั่นหลักพันหรือจุดทศนิยม 2 ตำแหน่งพอดี
    และไม่ยาวเกินไป — กันเลขผู้เสียภาษี/รหัสไปรษณีย์/เบอร์โทร/ปี พ.ศ. ซึ่งเป็นเลขล้วนไม่มีการจัดรูปแบบ"""
    digits_only = re.sub(r"\D", "", v)
    if len(digits_only) > 9:
        return False
    return bool("," in v or re.search(r"\.\d{2}$", v))


def _parse_single_amount_lines(raw_lines: list[str]) -> list[dict]:
    """บางใบแจ้งหนี้ค่าบริการ (Non-Trade) มีแค่ 'คำอธิบาย + ยอดเงินตัวเดียว' ไม่มีคอลัมน์
    จำนวน/ราคาต่อหน่วยแยก เช่น 'ค่าบริการพนักงานรับ-ส่งเอกสาร ประจำเดือน พฤษภาคม 2569  24,000.00'
    ใช้เป็นทางเลือกสำรองเฉพาะเมื่อรอบแรก (หาชุดจำนวน x ราคา = ยอดเงิน) ไม่พบรายการใดเลย
    เข้มงวดกว่ารอบแรกมาก เพราะไม่มีคอลัมน์อื่นช่วยยืนยันว่าตัวเลขนั้นคือยอดเงินจริง"""
    out: list[dict] = []
    for ln in raw_lines:
        toks = ln.split()
        if len(toks) < 2:
            continue
        s = " ".join(toks)
        if re.search(_LINE_SKIP, s, re.I) or _NOT_AMOUNT_CONTEXT.search(s):
            continue
        money = [(i, t) for i, t in enumerate(toks) if _NUM_TOKEN.match(t) and _looks_like_money(t)]
        if len(money) != 1:
            continue           # ต้องมีตัวเลขที่ 'ดูเหมือนเงิน' แค่ตัวเดียวเท่านั้นในแถว (เลขลำดับ/ปี พ.ศ. ไม่นับ)
        i_amt, tok = money[0]
        amt = _f(tok)
        if amt < 10:
            continue
        desc = " ".join(toks[:i_amt]).strip()
        m = re.match(r"^0*(\d{1,3})[.)\s]\s*(.+)$", desc)         # ตัดเลขลำดับรายการนำหน้า เช่น "1 ค่าบริการ..." หรือ "1. ค่าบริการ..."
        if m:
            desc = m.group(2)
        if len(desc) < 6 or len(re.sub(r"[^ก-๙A-Za-z]", "", desc)) < 4:
            continue                                              # คำอธิบายต้องมีตัวอักษรจริงพอสมควร ไม่ใช่สัญลักษณ์/เลขล้วน
        out.append({"extCode": "", "desc": _clean_desc(desc)[:300], "qty": 1.0, "dueDate": "",
                    "uom": "EA", "price": amt, "amount": amt})
    return out[:60]


# บรรทัดที่ "พูดซ้ำ" ยอดเงินโดยมีป้ายกำกับกำกับอยู่ในบรรทัดเดียวกัน (เช่น "Amount 1,200.00" หรือ
# "จำนวนเงิน 1,200.00") — พบบ่อยในใบเสร็จที่วางป้ายกำกับ/ค่าเป็นกล่องแยกจากคำอธิบายสินค้า เช่น Intertek
_AMOUNT_RESTATEMENT = re.compile(
    r"^(?:Amount|Net\s*Payment|" + th("จำนวนเงิน") + r")\b[:\-\s]*([\d,]+\.\d{2})\s*$", re.I)
# ป้ายกำกับหัวคอลัมน์ที่บางครั้งติดมาในบรรทัดเดียวกับคำอธิบายสินค้าจริง (ไม่ใช่บรรทัดหัวตารางเดี่ยว ๆ
# ซึ่ง _LINE_SKIP จัดการอยู่แล้ว) — ตัดออกก่อนใช้เป็นคำอธิบาย
_DESC_LABEL_PREFIX = re.compile(
    r"^(?:" + th("รายการสินค้าหรือบริการ") + "|" + th("รายละเอียด") + r"|Description(?:\s+of\s+goods?\s*/?\s*service)?)"
    r"\s*[:\-]?\s*", re.I)


def _parse_desc_then_amount_lines(raw_lines: list[str]) -> list[dict]:
    """ใบเสร็จ/ใบกำกับภาษีบางแบบ (เช่น Intertek) วางป้ายกำกับ "DESCRIPTION"/"AMOUNT" เป็นหัวคอลัมน์แยกกล่อง
    แล้วค่อยตามด้วยค่าจริงคนละบรรทัด (คนละตำแหน่งบนหน้ากระดาษ) ทำให้คำอธิบายกับยอดเงินไม่อยู่บรรทัดเดียวกัน
    ต่างจาก _parse_single_amount_lines ที่ต้องการทั้งสองอย่างในบรรทัดเดียว — ฟังก์ชันนี้จับคู่บรรทัดยอดเงินล้วน ๆ
    (หรือบรรทัด "ป้ายกำกับ + ยอดเงิน" เช่น "Amount 1,200.00" ที่ _LINE_SKIP กันไว้ไม่ให้เป็นรายการเอง)
    กับ "บรรทัดข้อความปกติ" ที่ใกล้ที่สุดก่อนหน้า (ข้ามป้ายกำกับ/บรรทัดว่าง) แทน ใช้เป็นทางเลือกสำรองสุดท้าย
    เข้มงวดที่สุดในบรรดา fallback ทั้งหมด เพราะไม่มีบริบทอื่นช่วยยืนยันว่าจับคู่ถูกต้อง"""
    out: list[dict] = []
    used_desc_at: set[int] = set()
    for i, ln in enumerate(raw_lines):
        toks = ln.split()
        amt = None
        if len(toks) == 1 and _NUM_TOKEN.match(toks[0]) and _looks_like_money(toks[0]):
            amt = _f(toks[0])
        else:
            m2 = _AMOUNT_RESTATEMENT.match(ln.strip())
            if m2:
                amt = _f(m2.group(1))
        if amt is None or amt < 10:
            continue
        desc, desc_i = "", -1
        for j in range(i - 1, max(-1, i - 6), -1):         # มองย้อนกลับไม่เกิน 5 บรรทัด หาคำอธิบายที่ใกล้ที่สุด
            pt = raw_lines[j].strip()
            if not pt or j in used_desc_at:
                continue
            if _NOT_AMOUNT_CONTEXT.search(pt):
                continue
            pt2 = _DESC_LABEL_PREFIX.sub("", pt).strip()   # ตัดป้ายกำกับหัวคอลัมน์ที่อาจติดหน้าคำอธิบายจริงออก
            if re.search(_LINE_SKIP, pt2, re.I):
                continue
            if _NUM_TOKEN.match(pt2.replace(" ", "")) or len(re.sub(r"[^ก-๙A-Za-z]", "", pt2)) < 4:
                continue                                  # ข้ามบรรทัดที่เป็นตัวเลข/สัญลักษณ์ล้วน
            desc, desc_i = pt2, j
            break
        if not desc:
            continue
        used_desc_at.add(desc_i)
        out.append({"extCode": "", "desc": _clean_desc(desc)[:300], "qty": 1.0, "dueDate": "",
                    "uom": "EA", "price": amt, "amount": amt})
    return out[:60]


# ---------------------------------------------------------------- providers
def pdf_blocks(path: Path) -> dict:
    """ฟอร์มใบสั่งซื้อส่วนใหญ่เป็น 2 คอลัมน์ (VENDOR ADDRESS | SHIP TO ADDRESS)
    ใช้พิกัดของคำเพื่อแยกบล็อกซ้าย/ขวา แทนการอ่านทีละบรรทัดซึ่งจะสลับกันมั่ว"""
    try:
        import pdfplumber
        with pdfplumber.open(str(path)) as pdf:
            page = pdf.pages[0]
            words = page.extract_words() or []
    except Exception:
        return {}
    if not words:
        return {}

    lines: dict = {}
    for w in words:
        lines.setdefault(round(w["top"] / 4) * 4, []).append(w)
    ordered = sorted(lines.items())

    marker_y = marker_x = None
    end_y = None
    for y, ws in ordered:
        txt = " ".join(w["text"] for w in sorted(ws, key=lambda x: x["x0"])).upper()
        if marker_y is None and re.search(r"SHIP\s*-?\s*TO", txt):
            marker_y = y
            for w in sorted(ws, key=lambda x: x["x0"]):
                if w["text"].upper().startswith("SHIP"):
                    marker_x = w["x0"]
                    break
        elif marker_y is not None and end_y is None and re.search(
                r"DUE\s*DATE|DESCRIPTION|QUANTITY|TERMS\b|รายการ", txt):
            end_y = y
    if marker_y is None or marker_x is None:
        return {}
    end_y = end_y or (marker_y + 400)

    # ถ้าคำว่า SHIP TO อยู่ชิดขอบซ้าย แปลว่าไม่ใช่ฟอร์ม 2 คอลัมน์
    # ให้เก็บทั้งบรรทัดตั้งแต่ marker ลงไปเป็นบล็อกที่อยู่จัดส่งแทน
    if marker_x < (page.width if hasattr(page, "width") else 600) * 0.33:
        rows = []
        for y, ws in ordered:
            if marker_y <= y < end_y:
                rows.append(" ".join(w["text"] for w in sorted(ws, key=lambda x: x["x0"])))
        rows = [re.sub(r"^\s*(SHIP\s*-?\s*TO)\s*(ADDRESS)?\s*:?\s*", "", r, flags=re.I).strip()
                for r in rows]
        return {"left": [], "right": [r for r in rows if r]}

    left, right = [], []
    for y, ws in ordered:
        if not (marker_y <= y < end_y):
            continue
        l = [w for w in sorted(ws, key=lambda x: x["x0"]) if w["x0"] < marker_x - 12]
        r = [w for w in sorted(ws, key=lambda x: x["x0"]) if w["x0"] >= marker_x - 12]
        if l:
            left.append(" ".join(w["text"] for w in l))
        if r:
            right.append(" ".join(w["text"] for w in r))

    clean = lambda rows: [re.sub(r"^(VENDOR|SHIP\s*-?\s*TO)\s*ADDRESS\s*:?\s*", "", x, flags=re.I).strip()
                          for x in rows]
    return {"left": [x for x in clean(left) if x], "right": [x for x in clean(right) if x]}


def pdf_text(path: Path) -> str:
    try:
        import pdfplumber
        with pdfplumber.open(str(path)) as pdf:
            return "\n".join((p.extract_text() or "") for p in pdf.pages[:20])
    except Exception:
        return ""


def tesseract_text(path: Path) -> str:
    """OCR ด้วย Tesseract (ไทย+อังกฤษ) — PDF จะถูกแปลงเป็นภาพต่อหน้าด้วย PyMuPDF ก่อน (ไม่ต้องใช้ poppler)"""
    exe = config.TESSERACT_CMD or shutil.which("tesseract")
    if not exe:
        return ""
    try:
        import os
        import pytesseract
        from PIL import Image
        pytesseract.pytesseract.tesseract_cmd = exe
        if config.TESSDATA_PREFIX:
            os.environ["TESSDATA_PREFIX"] = config.TESSDATA_PREFIX

        def ocr_img(img):
            return pytesseract.image_to_string(img, lang="tha+eng")

        if path.suffix.lower() == ".pdf":
            import fitz
            out = []
            with fitz.open(str(path)) as doc:
                for page in doc[:5]:                    # จำกัด 5 หน้าแรก กันช้าเกินไปกับไฟล์ยาว
                    pix = page.get_pixmap(dpi=300)
                    img = Image.frombytes("RGB", [pix.width, pix.height], pix.samples)
                    out.append(ocr_img(img))
            return "\n".join(out)
        return ocr_img(Image.open(str(path)))
    except Exception:
        return ""


def typhoon_text(path: Path) -> tuple[str, str]:
    """OCR ด้วย Typhoon OCR (opentyphoon.ai) — โมเดล OCR ภาษาไทย/อังกฤษ open-weight ของ SCB 10X
    เรียกผ่าน API แบบ OpenAI-compatible (chat/completions) ส่งภาพแต่ละหน้าเข้าไป ให้ตอบกลับเป็นข้อความ/markdown
    แล้วส่งต่อให้ parse_text()/parse_lines() แบบเดียวกับ Tesseract (ไม่ผูกกับ pip package ทางการ ใช้ urllib ตรง ๆ
    เหมือน azure_extract/claude_vision_extract เพื่อไม่เพิ่ม dependency)
    คืนค่า (ข้อความ, ข้อความ error ถ้ามี) — แยก error ออกมาชัดเจนแทนการกลืน exception เงียบ ๆ เพราะ Typhoon
    จำกัด rate limit ไว้ค่อนข้างต่ำ (2 req/s, 20 req/min) ผู้ใช้ควรรู้ว่าทำไมอ่านไม่ได้ (rate limit ต่างจาก
    ไฟล์เสีย/คีย์ผิด)"""
    if not config.TYPHOON_API_KEY:
        return "", "ยังไม่ได้ตั้งค่า TYPHOON_API_KEY ใน .env"
    try:
        import base64
        import json as _json
        import time
        import urllib.error
        import urllib.request

        imgs: list[bytes] = []
        if path.suffix.lower() == ".pdf":
            import fitz
            with fitz.open(str(path)) as doc:
                for page in doc[:5]:                    # จำกัด 5 หน้าแรก กันช้าเกินไปกับไฟล์ยาว
                    pix = page.get_pixmap(dpi=200)
                    imgs.append(pix.tobytes("png"))
        else:
            imgs.append(path.read_bytes())

        out = []
        for i, b in enumerate(imgs):
            if i > 0:
                time.sleep(0.6)                          # เคารพ rate limit 2 req/s เวลาเอกสารมีหลายหน้า
            data_uri = "data:image/png;base64," + base64.b64encode(b).decode()
            body = {
                "model": config.TYPHOON_MODEL, "temperature": 0, "max_tokens": 8000,
                "messages": [{"role": "user", "content": [
                    {"type": "text", "text":
                     "ถอดข้อความในภาพนี้ทีละบรรทัดตามที่ปรากฏจริง เรียงจากบนลงล่าง ซ้ายไปขวา แต่ละบรรทัดที่แยกกัน "
                     "ในเอกสารต้นฉบับให้ขึ้นบรรทัดใหม่เสมอ ห้ามนำข้อความจากหลายบรรทัด/หลายจุดมารวมเป็นรายการ "
                     "คั่นด้วยจุลภาคเดียวเด็ดขาด\n"
                     "สำหรับตารางรายการสินค้า/บริการ: แต่ละแถวของตารางต้องอยู่บรรทัดเดียวกัน โดยเรียง "
                     "รหัสสินค้า(ถ้ามี) รายละเอียด จำนวน หน่วยนับ ราคาต่อหน่วย จำนวนเงิน คั่นแต่ละคอลัมน์ด้วย "
                     "เครื่องหมาย | เช่น 'ค่าบริการทดสอบ | 1 | EA | 1200.00 | 1200.00'\n"
                     "ตอบเฉพาะข้อความที่อ่านได้เท่านั้น ห้ามสรุป อธิบายเพิ่มเติม หรือขึ้นต้นด้วยหัวข้อใด ๆ"},
                    {"type": "image_url", "image_url": {"url": data_uri}},
                ]}],
            }
            req = urllib.request.Request(
                "https://api.opentyphoon.ai/v1/chat/completions", data=_json.dumps(body).encode("utf-8"),
                method="POST", headers={"Authorization": f"Bearer {config.TYPHOON_API_KEY}",
                                        "Content-Type": "application/json"})
            attempt = 0
            while True:
                try:
                    with urllib.request.urlopen(req, timeout=90) as r:
                        resp = _json.loads(r.read().decode("utf-8"))
                    break
                except urllib.error.HTTPError as e:
                    if e.code == 429 and attempt < 2:     # โดน rate limit — รอแล้วลองใหม่ (สูงสุด 2 ครั้ง)
                        attempt += 1
                        time.sleep(3 * attempt)
                        continue
                    detail = e.read().decode("utf-8", "ignore")[:200]
                    if e.code == 429:
                        return "", "Typhoon OCR ติด rate limit (2 req/s, 20 req/min) ลองใหม่อีกครั้งในอีกสักครู่"
                    if e.code in (401, 403):
                        return "", f"TYPHOON_API_KEY ไม่ถูกต้องหรือหมดอายุ (HTTP {e.code})"
                    return "", f"Typhoon OCR ตอบกลับผิดพลาด (HTTP {e.code}): {detail}"
            content = ((resp.get("choices") or [{}])[0].get("message", {}) or {}).get("content", "")
            m = re.search(r'"natural_text"\s*:\s*"((?:[^"\\]|\\.)*)"', content, re.S)
            if m:                                        # SDK ทางการบางเวอร์ชันห่อผลลัพธ์เป็น JSON — แกะออกถ้าเจอ
                content = m.group(1).encode().decode("unicode_escape")
            out.append(content)
        return "\n".join(out), ""
    except Exception as e:
        return "", f"เชื่อมต่อ Typhoon OCR ไม่สำเร็จ: {e}"


def azure_extract(path: Path) -> dict | None:
    """Azure AI Document Intelligence - prebuilt-invoice"""
    if not (config.AZURE_DI_ENDPOINT and config.AZURE_DI_KEY):
        return None
    try:
        import time, urllib.request, json
        url = (config.AZURE_DI_ENDPOINT.rstrip("/") +
               "/documentintelligence/documentModels/prebuilt-invoice:analyze?api-version=2024-11-30")
        req = urllib.request.Request(url, data=path.read_bytes(), method="POST", headers={
            "Ocp-Apim-Subscription-Key": config.AZURE_DI_KEY,
            "Content-Type": "application/octet-stream"})
        with urllib.request.urlopen(req, timeout=60) as r:
            op = r.headers.get("operation-location")
        for _ in range(30):
            time.sleep(2)
            g = urllib.request.Request(op, headers={"Ocp-Apim-Subscription-Key": config.AZURE_DI_KEY})
            with urllib.request.urlopen(g, timeout=60) as r:
                data = json.loads(r.read())
            if data.get("status") == "succeeded":
                return data
            if data.get("status") == "failed":
                return None
    except Exception:
        return None
    return None


def _from_azure(data: dict, module: str) -> dict:
    doc = (data.get("analyzeResult", {}).get("documents") or [{}])[0]
    f = doc.get("fields", {})

    def g(name, sub="valueString"):
        v = f.get(name) or {}
        return v.get(sub) or v.get("content") or ""

    def gnum(name):
        v = f.get(name) or {}
        cur = v.get("valueCurrency") or {}
        return cur.get("amount") or _f(v.get("content"))

    lines = []
    for it in (f.get("Items", {}).get("valueArray") or []):
        o = it.get("valueObject", {})
        lines.append({
            "extCode": (o.get("ProductCode", {}) or {}).get("valueString", ""),
            "desc": (o.get("Description", {}) or {}).get("valueString", ""),
            "qty": (o.get("Quantity", {}) or {}).get("valueNumber", 0) or 0,
            "uom": (o.get("Unit", {}) or {}).get("valueString", "") or "EA",
            "price": ((o.get("UnitPrice", {}) or {}).get("valueCurrency", {}) or {}).get("amount", 0) or 0,
            "amount": ((o.get("Amount", {}) or {}).get("valueCurrency", {}) or {}).get("amount", 0) or 0,
        })
    name = g("CustomerName") if module == "SO" else g("VendorName")
    tax = g("CustomerTaxId") if module == "SO" else g("VendorTaxId")
    header = _blank_header(module)
    if module != "SO":
        header.update({"invoiceNo": g("InvoiceId"), "invoiceDate": g("InvoiceDate", "valueDate"),
                       "postingDate": g("InvoiceDate", "valueDate"), "vendorName": name, "vendorTaxId": tax,
                       "subTotal": gnum("SubTotal"), "vatAmount": gnum("TotalTax"),
                       "totalAmount": gnum("InvoiceTotal"), "vatRate": 7})
    else:
        header.update({"poNo": g("PurchaseOrder"), "poDate": g("InvoiceDate", "valueDate"),
                       "customerName": name, "customerTaxId": tax,
                       "shipToName": g("ShippingAddressRecipient"), "shipToAddress": g("ShippingAddress"),
                       "totalAmount": gnum("InvoiceTotal")})
    return {"header": header, "lines": lines,
            "confidence": float(doc.get("confidence") or 0.9), "provider": "azure",
            "rawText": data.get("analyzeResult", {}).get("content", "")[:20000]}


def _claude_prompt(module: str, mode: str = "image") -> str:
    """สั่งให้ Claude อ่านเอกสารแล้วตอบกลับเป็น JSON ตรงตามโครงสร้างที่ระบบต้องการโดยตรง
    (ข้ามการ parse ด้วย regex ทั้งหมด — ใช้ความเข้าใจบริบทแทน)
    mode="image": ส่งภาพเอกสารเข้าไปให้ Claude อ่านเอง (claude_vision_extract)
    mode="text": ส่งเฉพาะข้อความที่ OCR อ่านมาแล้ว (Tesseract/pdfplumber) ให้ Claude จัดโครงสร้าง
    ถูกกว่า image มาก เพราะไม่มีค่า token รูปภาพ (claude_text_extract)"""
    if module == "SO":
        fields = """{
  "header": {
    "docType": "ประเภทเอกสาร เช่น PURCHASE ORDER",
    "poNo": "เลขที่ใบสั่งซื้อของลูกค้า",
    "poDate": "วันที่เอกสาร รูปแบบ YYYY-MM-DD",
    "customerName": "ชื่อลูกค้า (นิติบุคคลที่ออกใบสั่งซื้อ ไม่ใช่บริษัทผู้ขาย/ผู้รับเอกสาร)",
    "customerTaxId": "เลขทะเบียนนิติบุคคล/ผู้เสียภาษี 13 หลักของลูกค้า",
    "shipToName": "ชื่อสถานที่ส่งของ", "shipToAddress": "ที่อยู่จัดส่งเต็ม",
    "deliveryDate": "วันที่ต้องการรับสินค้า YYYY-MM-DD",
    "currency": "รหัสสกุลเงิน 3 ตัวอักษร เช่น THB", "paymentTerms": "เงื่อนไขการชำระเงิน",
    "incoterms": "Incoterms ถ้ามี", "subTotal": 0, "vatAmount": 0, "totalAmount": 0, "remark": ""
  },
  "lines": [{"extCode": "รหัสสินค้าตามเอกสาร", "desc": "ชื่อ/รายละเอียดสินค้า",
             "qty": 0, "uom": "หน่วยนับ", "price": 0, "amount": 0}]
}"""
    else:
        fields = """{
  "header": {
    "docType": "ประเภทเอกสาร เช่น ใบกำกับภาษี/ใบแจ้งหนี้",
    "invoiceNo": "เลขที่ใบกำกับภาษี/ใบแจ้งหนี้", "invoiceDate": "วันที่เอกสาร YYYY-MM-DD",
    "postingDate": "วันที่เดียวกับ invoiceDate ถ้าไม่มีระบุแยก",
    "vendorName": "ชื่อผู้ขาย/ผู้ออกใบกำกับภาษี (ไม่ใช่บริษัทผู้ซื้อ/ผู้รับเอกสาร)",
    "vendorTaxId": "เลขทะเบียนนิติบุคคล/ผู้เสียภาษี 13 หลักของผู้ขาย", "branch": "สาขาของผู้ขาย",
    "poRef": "เลขที่ใบสั่งซื้ออ้างอิงถ้ามี",
    "currency": "รหัสสกุลเงิน 3 ตัวอักษร เช่น THB", "paymentTerms": "เงื่อนไขการชำระเงิน",
    "subTotal": 0, "vatRate": 7, "vatAmount": 0, "whtAmount": 0, "totalAmount": 0
  },
  "lines": [{"extCode": "รหัสสินค้า/บริการถ้ามี", "desc": "ชื่อ/รายละเอียดสินค้าหรือบริการ",
             "qty": 0, "uom": "หน่วยนับ", "price": 0, "amount": 0}]
}"""
    intro = (
        "อ่านเอกสารในภาพนี้ (ใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อภาษาไทยหรืออังกฤษ) แล้วดึงข้อมูลออกมา\n"
        if mode == "image" else
        "ข้อความด้านล่างนี้ได้จากการอ่าน OCR เอกสารใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อ อาจมีช่องว่างแทรก"
        "ระหว่างตัวอักษรไทยผิดปกติ ตัวเลข/ตัวอักษรบางจุดอ่านผิด หรือลำดับคอลัมน์สลับกัน "
        "ให้ตีความเนื้อหาอย่างชาญฉลาดแล้วดึงข้อมูลออกมาให้ถูกต้องที่สุด\n"
    )
    return (
        intro +
        f"ตอบกลับเป็น JSON ล้วน ๆ ตามโครงสร้างนี้เท่านั้น ห้ามมีข้อความอื่นนอก JSON:\n{fields}\n\n"
        "กติกา:\n"
        "- ตัวเลขทุกช่อง (qty, price, amount, subTotal, vatAmount, totalAmount ฯลฯ) ต้องเป็นตัวเลขล้วน "
        "ไม่มีคอมมา/สัญลักษณ์สกุลเงิน\n"
        "- วันที่ทุกช่องต้องอยู่ในรูปแบบ YYYY-MM-DD (แปลง พ.ศ. เป็น ค.ศ. โดยลบ 543)\n"
        "- ช่องไหนหาไม่เจอในเอกสารให้ใส่สตริงว่าง \"\" หรือ 0 ตามชนิดข้อมูล อย่าเดา\n"
        "- ห้ามใช้ชื่อ/เลขทะเบียนของบริษัทที่เป็น 'ผู้รับเอกสาร' (Megachem (Thailand)) เป็นชื่อคู่ค้าเด็ดขาด\n"
        "- ตรวจสอบผลรวม: subTotal + vatAmount ควรใกล้เคียง totalAmount"
    )


def claude_vision_extract(path: Path, module: str) -> dict | None:
    """อ่านเอกสารด้วย Claude Vision (Anthropic API) — ส่งภาพหน้าเอกสารเข้าไปพร้อม prompt
    ให้โมเดลตอบกลับเป็น JSON ตรงโครงสร้างที่ต้องการโดยตรง แม่นกว่า OCR+regex มากสำหรับเอกสารที่ซับซ้อน"""
    if not config.ANTHROPIC_API_KEY:
        return None
    try:
        import base64
        import json as _json
        import urllib.request

        imgs: list[bytes] = []
        if path.suffix.lower() == ".pdf":
            import fitz
            with fitz.open(str(path)) as doc:
                for page in doc[:3]:                    # จำกัด 3 หน้าแรก
                    pix = page.get_pixmap(dpi=200)
                    imgs.append(pix.tobytes("png"))
        else:
            imgs.append(path.read_bytes())
        if not imgs:
            return None

        content = [{"type": "text", "text": _claude_prompt(module)}]
        for b in imgs:
            content.append({"type": "image", "source": {
                "type": "base64", "media_type": "image/png", "data": base64.b64encode(b).decode()}})

        body = {"model": config.ANTHROPIC_MODEL, "max_tokens": 3000,
                "messages": [{"role": "user", "content": content}]}
        req = urllib.request.Request(
            "https://api.anthropic.com/v1/messages", data=_json.dumps(body).encode("utf-8"),
            method="POST", headers={"x-api-key": config.ANTHROPIC_API_KEY,
                                    "anthropic-version": "2023-06-01", "content-type": "application/json"})
        with urllib.request.urlopen(req, timeout=90) as r:
            resp = _json.loads(r.read().decode("utf-8"))
        raw = "".join(b.get("text", "") for b in resp.get("content", []) if b.get("type") == "text")
        m = re.search(r"\{.*\}", raw, re.S)
        if not m:
            return None
        parsed = _json.loads(m.group(0))

        h = _blank_header(module)
        for k, v in (parsed.get("header") or {}).items():
            if k in h:
                h[k] = v
        lines = []
        for ln in (parsed.get("lines") or [])[:60]:
            lines.append({"extCode": str(ln.get("extCode") or ""), "desc": str(ln.get("desc") or ""),
                          "qty": _f(ln.get("qty")), "uom": str(ln.get("uom") or "EA") or "EA",
                          "price": _f(ln.get("price")), "amount": _f(ln.get("amount"))})
        usage = resp.get("usage") or {}
        return {"header": h, "lines": lines, "confidence": 0.88, "provider": "claude", "rawText": raw[:20000],
                "tokensIn": usage.get("input_tokens"), "tokensOut": usage.get("output_tokens")}
    except Exception:
        return None


def claude_text_extract(path: Path, module: str, text: str) -> dict | None:
    """สถาปัตยกรรม 2 ชั้น: ใช้ OCR ที่มีอยู่แล้ว (pdfplumber/Tesseract — ฟรี) ดึงข้อความออกมาก่อน
    แล้วส่ง "ข้อความ" (ไม่ใช่รูปภาพ) ให้ Claude จัดโครงสร้างเป็น JSON — ถูกกว่า claude_vision_extract มาก
    เพราะไม่มีค่า token รูปภาพ เหมาะกับงานเอกสารซ้ำ ๆ (PO/Invoice) ที่ OCR อ่านตัวอักษรออกมาได้ระดับหนึ่งแล้ว
    (ถ้าข้อความที่ OCR อ่านได้เป็นขยะตั้งแต่ต้น วิธีนี้กู้คืนไม่ได้ ต้องใช้ Claude Vision แทน)"""
    if not config.ANTHROPIC_API_KEY or not text.strip():
        return None
    try:
        import json as _json
        import urllib.request

        prompt = _claude_prompt(module, mode="text") + f"\n\n--- ข้อความจาก OCR ---\n{text[:12000]}\n"
        body = {"model": config.ANTHROPIC_MODEL, "max_tokens": 3000,
                "messages": [{"role": "user", "content": prompt}]}
        req = urllib.request.Request(
            "https://api.anthropic.com/v1/messages", data=_json.dumps(body).encode("utf-8"),
            method="POST", headers={"x-api-key": config.ANTHROPIC_API_KEY,
                                    "anthropic-version": "2023-06-01", "content-type": "application/json"})
        with urllib.request.urlopen(req, timeout=60) as r:
            resp = _json.loads(r.read().decode("utf-8"))
        raw = "".join(b.get("text", "") for b in resp.get("content", []) if b.get("type") == "text")
        m = re.search(r"\{.*\}", raw, re.S)
        if not m:
            return None
        parsed = _json.loads(m.group(0))

        h = _blank_header(module)
        for k, v in (parsed.get("header") or {}).items():
            if k in h:
                h[k] = v
        lines = []
        for ln in (parsed.get("lines") or [])[:60]:
            lines.append({"extCode": str(ln.get("extCode") or ""), "desc": str(ln.get("desc") or ""),
                          "qty": _f(ln.get("qty")), "uom": str(ln.get("uom") or "EA") or "EA",
                          "price": _f(ln.get("price")), "amount": _f(ln.get("amount"))})
        usage = resp.get("usage") or {}
        return {"header": h, "lines": lines, "confidence": 0.82, "provider": "claude_text", "rawText": text[:20000],
                "tokensIn": usage.get("input_tokens"), "tokensOut": usage.get("output_tokens")}
    except Exception:
        return None


def gemini_vision_extract(path: Path, module: str) -> dict | None:
    """อ่านเอกสารด้วย Google Gemini Vision — ส่งภาพหน้าเอกสารเข้าไปพร้อม prompt เดียวกับ Claude Vision
    (ใช้ generationConfig.responseMimeType=application/json ให้ Gemini คืน JSON ล้วน ๆ โดยตรง)"""
    if not config.GEMINI_API_KEY:
        return None
    try:
        import base64
        import json as _json
        import urllib.request

        imgs: list[bytes] = []
        if path.suffix.lower() == ".pdf":
            import fitz
            with fitz.open(str(path)) as doc:
                for page in doc[:3]:                    # จำกัด 3 หน้าแรก
                    pix = page.get_pixmap(dpi=200)
                    imgs.append(pix.tobytes("png"))
        else:
            imgs.append(path.read_bytes())
        if not imgs:
            return None

        parts = [{"text": _claude_prompt(module)}]
        for b in imgs:
            parts.append({"inline_data": {"mime_type": "image/png", "data": base64.b64encode(b).decode()}})

        body = {"contents": [{"parts": parts}],
                "generationConfig": {"responseMimeType": "application/json", "maxOutputTokens": 3000}}
        url = (f"https://generativelanguage.googleapis.com/v1beta/models/"
               f"{config.GEMINI_MODEL}:generateContent?key={config.GEMINI_API_KEY}")
        req = urllib.request.Request(
            url, data=_json.dumps(body).encode("utf-8"),
            method="POST", headers={"content-type": "application/json"})
        with urllib.request.urlopen(req, timeout=90) as r:
            resp = _json.loads(r.read().decode("utf-8"))
        raw = "".join(p.get("text", "") for c in resp.get("candidates", [])
                     for p in (c.get("content") or {}).get("parts", []))
        m = re.search(r"\{.*\}", raw, re.S)
        if not m:
            return None
        parsed = _json.loads(m.group(0))

        h = _blank_header(module)
        for k, v in (parsed.get("header") or {}).items():
            if k in h:
                h[k] = v
        lines = []
        for ln in (parsed.get("lines") or [])[:60]:
            lines.append({"extCode": str(ln.get("extCode") or ""), "desc": str(ln.get("desc") or ""),
                          "qty": _f(ln.get("qty")), "uom": str(ln.get("uom") or "EA") or "EA",
                          "price": _f(ln.get("price")), "amount": _f(ln.get("amount"))})
        usage = resp.get("usageMetadata") or {}
        # thoughtsTokenCount = token คิด/reasoning ภายในของ Gemini — ไม่โผล่ในข้อความตอบ แต่ Google คิดเงินเป็น
        # output token ด้วย (พบว่าบางครั้งมากกว่า candidatesTokenCount หลายเท่า) ต้องรวมด้วยไม่งั้นค่าใช้จ่ายจะต่ำกว่าจริงมาก
        tokens_out = (usage.get("candidatesTokenCount") or 0) + (usage.get("thoughtsTokenCount") or 0)
        return {"header": h, "lines": lines, "confidence": 0.88, "provider": "gemini", "rawText": raw[:20000],
                "tokensIn": usage.get("promptTokenCount"), "tokensOut": tokens_out or None}
    except Exception:
        return None


def openai_vision_extract(path: Path, module: str) -> dict | None:
    """อ่านเอกสารด้วย OpenAI GPT-4o/GPT-5 Vision — ส่งภาพหน้าเอกสารเข้าไปพร้อม prompt เดียวกับ Claude/Gemini Vision
    (ใช้ response_format=json_object ให้ตอบกลับเป็น JSON ล้วน ๆ โดยตรง)"""
    if not config.OPENAI_API_KEY:
        return None
    try:
        import base64
        import json as _json
        import urllib.request

        imgs: list[bytes] = []
        if path.suffix.lower() == ".pdf":
            import fitz
            with fitz.open(str(path)) as doc:
                for page in doc[:3]:                    # จำกัด 3 หน้าแรก
                    pix = page.get_pixmap(dpi=200)
                    imgs.append(pix.tobytes("png"))
        else:
            imgs.append(path.read_bytes())
        if not imgs:
            return None

        content = [{"type": "text", "text": _claude_prompt(module)}]
        for b in imgs:
            content.append({"type": "image_url",
                            "image_url": {"url": "data:image/png;base64," + base64.b64encode(b).decode()}})

        body = {"model": config.OPENAI_MODEL, "max_tokens": 3000,
                "response_format": {"type": "json_object"},
                "messages": [{"role": "user", "content": content}]}
        req = urllib.request.Request(
            "https://api.openai.com/v1/chat/completions", data=_json.dumps(body).encode("utf-8"),
            method="POST", headers={"Authorization": "Bearer " + config.OPENAI_API_KEY,
                                    "content-type": "application/json"})
        with urllib.request.urlopen(req, timeout=90) as r:
            resp = _json.loads(r.read().decode("utf-8"))
        raw = ((resp.get("choices") or [{}])[0].get("message") or {}).get("content") or ""
        m = re.search(r"\{.*\}", raw, re.S)
        if not m:
            return None
        parsed = _json.loads(m.group(0))

        h = _blank_header(module)
        for k, v in (parsed.get("header") or {}).items():
            if k in h:
                h[k] = v
        lines = []
        for ln in (parsed.get("lines") or [])[:60]:
            lines.append({"extCode": str(ln.get("extCode") or ""), "desc": str(ln.get("desc") or ""),
                          "qty": _f(ln.get("qty")), "uom": str(ln.get("uom") or "EA") or "EA",
                          "price": _f(ln.get("price")), "amount": _f(ln.get("amount"))})
        usage = resp.get("usage") or {}
        return {"header": h, "lines": lines, "confidence": 0.88, "provider": "openai", "rawText": raw[:20000],
                "tokensIn": usage.get("prompt_tokens"), "tokensOut": usage.get("completion_tokens")}
    except Exception:
        return None


def _chat_fix_call_claude(system_prompt: str, history: list[dict], message: str,
                          image_b64: str | None, image_media_type: str) -> str | None:
    if not config.ANTHROPIC_API_KEY:
        return None
    import json as _json
    import urllib.request

    messages = []
    for h in (history or [])[-12:]:                  # จำกัดความยาวบทสนทนาย้อนหลัง กันบวม token
        role = "assistant" if h.get("role") == "assistant" else "user"
        text = str(h.get("text") or "").strip()
        suffix = " [แนบภาพประกอบ]" if h.get("hasImage") and role == "user" else ""
        if text or suffix:
            messages.append({"role": role, "content": text + suffix})

    cur_content: list | str
    if image_b64:
        cur_content = [{"type": "image", "source": {"type": "base64", "media_type": image_media_type, "data": image_b64}},
                       {"type": "text", "text": message}]
    else:
        cur_content = message
    messages.append({"role": "user", "content": cur_content})

    body = {"model": config.ANTHROPIC_MODEL, "max_tokens": 3000,
            "system": system_prompt, "messages": messages}
    req = urllib.request.Request(
        "https://api.anthropic.com/v1/messages", data=_json.dumps(body).encode("utf-8"),
        method="POST", headers={"x-api-key": config.ANTHROPIC_API_KEY,
                                "anthropic-version": "2023-06-01", "content-type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        resp = _json.loads(r.read().decode("utf-8"))
    return "".join(b.get("text", "") for b in resp.get("content", []) if b.get("type") == "text")


def _chat_fix_call_gemini(system_prompt: str, history: list[dict], message: str,
                          image_b64: str | None, image_media_type: str) -> str | None:
    if not config.GEMINI_API_KEY:
        return None
    import json as _json
    import urllib.request

    contents = []
    for h in (history or [])[-12:]:
        role = "model" if h.get("role") == "assistant" else "user"
        text = str(h.get("text") or "").strip()
        suffix = " [แนบภาพประกอบ]" if h.get("hasImage") and role == "user" else ""
        if text or suffix:
            contents.append({"role": role, "parts": [{"text": text + suffix}]})
    cur_parts = [{"text": message or " "}]
    if image_b64:
        cur_parts.append({"inline_data": {"mime_type": image_media_type, "data": image_b64}})
    contents.append({"role": "user", "parts": cur_parts})

    body = {"contents": contents, "systemInstruction": {"parts": [{"text": system_prompt}]},
            "generationConfig": {"responseMimeType": "application/json", "maxOutputTokens": 3000}}
    url = (f"https://generativelanguage.googleapis.com/v1beta/models/"
          f"{config.GEMINI_MODEL}:generateContent?key={config.GEMINI_API_KEY}")
    req = urllib.request.Request(url, data=_json.dumps(body).encode("utf-8"),
                                 method="POST", headers={"content-type": "application/json"})
    with urllib.request.urlopen(req, timeout=90) as r:
        resp = _json.loads(r.read().decode("utf-8"))
    return "".join(p.get("text", "") for c in resp.get("candidates", [])
                  for p in (c.get("content") or {}).get("parts", []))


def _chat_fix_call_openai(system_prompt: str, history: list[dict], message: str,
                          image_b64: str | None, image_media_type: str) -> str | None:
    if not config.OPENAI_API_KEY:
        return None
    import json as _json
    import urllib.request

    messages = [{"role": "system", "content": system_prompt}]
    for h in (history or [])[-12:]:
        role = "assistant" if h.get("role") == "assistant" else "user"
        text = str(h.get("text") or "").strip()
        suffix = " [แนบภาพประกอบ]" if h.get("hasImage") and role == "user" else ""
        if text or suffix:
            messages.append({"role": role, "content": text + suffix})

    cur_content: list | str
    if image_b64:
        cur_content = [{"type": "image_url", "image_url": {"url": f"data:{image_media_type};base64,{image_b64}"}},
                       {"type": "text", "text": message or " "}]
    else:
        cur_content = message or " "
    messages.append({"role": "user", "content": cur_content})

    body = {"model": config.OPENAI_MODEL, "max_tokens": 3000,
            "response_format": {"type": "json_object"}, "messages": messages}
    req = urllib.request.Request(
        "https://api.openai.com/v1/chat/completions", data=_json.dumps(body).encode("utf-8"),
        method="POST", headers={"Authorization": "Bearer " + config.OPENAI_API_KEY,
                                "content-type": "application/json"})
    with urllib.request.urlopen(req, timeout=90) as r:
        resp = _json.loads(r.read().decode("utf-8"))
    return ((resp.get("choices") or [{}])[0].get("message") or {}).get("content") or ""


_CHAT_FIX_CALLERS = {"claude": _chat_fix_call_claude, "gemini": _chat_fix_call_gemini, "openai": _chat_fix_call_openai}


def chat_fix_document(module: str, header: dict, lines: list[dict], history: list[dict], message: str,
                      image_b64: str | None = None, image_media_type: str = "image/png",
                      provider: str = "claude") -> dict | None:
    """เมนู "แชทสั่งแก้" — ผู้ใช้พิมพ์บอกจุดที่ผิดด้วยภาษาธรรมดา (เช่น "ชื่อผู้ขายที่ถูกคือ ABC จำกัด ไม่ใช่ XYZ")
    พร้อมแนบภาพประกอบได้ (เช่น capture หน้าจอจุดที่อ่านผิดจาก Review Document) ให้ AI ดูภาพนั้นด้วย
    แล้วให้ AI แก้เฉพาะจุดที่ระบุในข้อมูล header/lines ปัจจุบัน ไม่แตะข้อมูลอื่น — ใช้กับเอกสารนี้เท่านั้น
    (ไม่ auto-learn ไปเอกสารอื่น ต่างจาก /learn ที่บันทึกลง Master Data ถาวร)

    provider: "claude" | "gemini" | "openai" — เลือกโมเดล Vision ที่จะใช้อ่านภาพที่แนบมา (เผื่อโมเดลหลักอ่านเอกสาร
    ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า API key ของโมเดลนั้น ผู้ใช้เลือกโมเดลอื่นที่พร้อมใช้งานแทนได้)

    history: บทสนทนาก่อนหน้า [{"role": "user"|"assistant", "text": str}, ...] เรียงเก่า->ใหม่ — ส่งเข้าไปเป็น
    บริบทให้ AI ตอบแบบถามตอบต่อเนื่องได้ (เช่น ถามคำถามต่อจากที่คุยไว้ก่อนหน้า) ไม่ใช่แค่คำสั่งเดี่ยว ๆ ทีละครั้ง
    ไม่ใส่ภาพของเทิร์นก่อนหน้ากลับเข้าไปซ้ำ (คุมขนาด prompt/ค่าใช้จ่าย) เพราะผลของการแก้ไขที่ยืนยันแล้วอยู่ใน
    header/lines ปัจจุบันที่ส่งให้ทุกครั้งอยู่แล้ว

    คืนค่า {reply, header, lines} หรือ None ถ้าเรียก API ไม่สำเร็จ/ยังไม่ได้ตั้งค่า key ของโมเดลที่เลือก"""
    caller = _CHAT_FIX_CALLERS.get(provider, _chat_fix_call_claude)
    try:
        import json as _json

        system_prompt = (
            "คุณคือผู้ช่วยแก้ไขข้อมูลเอกสาร (ใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อ) ที่อ่านมาจาก OCR ในระบบ OCR-to-SAP\n"
            "ด้านล่างนี้คือข้อมูล header และ lines ปัจจุบันของเอกสารนี้ในรูปแบบ JSON (เป็นค่าล่าสุด "
            "รวมการแก้ไขจากบทสนทนาก่อนหน้าแล้ว):\n\n"
            f"header:\n{_json.dumps(header, ensure_ascii=False)}\n\n"
            f"lines:\n{_json.dumps(lines, ensure_ascii=False)}\n\n"
            "กติกา:\n"
            "- ผู้ใช้อาจพิมพ์คำสั่งแก้ไข หรือถามคำถามเกี่ยวกับเอกสารนี้ก็ได้ (ถามตอบต่อเนื่องได้ตามบทสนทนาก่อนหน้า)\n"
            "- ถ้าเป็นคำสั่งแก้ไข ให้แก้เฉพาะจุดที่ผู้ใช้ระบุเท่านั้น ห้ามเปลี่ยนค่าอื่นที่ไม่เกี่ยวข้องแม้จะดูแปลกตา\n"
            "- ถ้ามีภาพแนบมาในข้อความล่าสุด ให้ใช้ภาพเป็นหลักฐานยืนยันค่าที่ถูกต้อง (เช่น อ่านตัวเลข/ชื่อจากภาพโดยตรง) "
            "ประกอบกับคำอธิบายของผู้ใช้\n"
            "- โครงสร้างและชื่อ field ของ header/lines ต้องเหมือนเดิมทุกประการ ห้ามเพิ่ม/ลบ field ห้ามเพิ่ม/ลบรายการใน lines "
            "เว้นแต่ผู้ใช้ขอให้เพิ่ม/ลบรายการโดยตรง\n"
            "- ถ้าเป็นคำถาม (ไม่ใช่คำสั่งแก้ไข) ให้ตอบคำถามใน reply แล้วคืน header/lines เดิมโดยไม่แก้ไขอะไร\n"
            "- ตัวเลขต้องเป็นตัวเลขล้วน ไม่มีคอมมา\n"
            "- ตอบกลับเป็น JSON ล้วน ๆ เท่านั้นทุกครั้ง ไม่ว่าข้อความก่อนหน้าในบทสนทนาจะเป็นรูปแบบใด "
            "ตามโครงสร้างนี้ ห้ามมีข้อความอื่นนอก JSON:\n"
            '{"reply": "ข้อความสั้น ๆ ยืนยันว่าแก้อะไรไป หรือคำตอบคำถาม (ภาษาไทย)", '
            '"header": { ...header ที่แก้ไขแล้ว (หรือเดิมถ้าไม่ได้แก้)... }, '
            '"lines": [ ...lines ที่แก้ไขแล้ว (หรือเดิมถ้าไม่ได้แก้)... ]}'
        )

        raw = caller(system_prompt, history, message, image_b64, image_media_type)
        if raw is None:
            return None
        m = re.search(r"\{.*\}", raw, re.S)
        parsed = None
        if m:
            try:
                parsed = _json.loads(m.group(0))
            except ValueError:
                parsed = None
        if parsed is None:
            # บางครั้งโมเดลตอบคำถามเป็นข้อความล้วนไม่ห่อ JSON (โดยเฉพาะเทิร์นถามตอบต่อเนื่อง) —
            # ถือเป็นคำตอบคำถามธรรมดา ไม่แก้ไข header/lines แทนที่จะถือว่าล้มเหลวทั้งเทิร์น
            reply_text = raw.strip()
            if not reply_text:
                return None
            return {"reply": reply_text, "header": dict(header), "lines": [dict(ln) for ln in lines]}

        h = _blank_header(module)
        for k, v in (parsed.get("header") or {}).items():
            if k in h:
                h[k] = v
        out_lines = []
        for ln in (parsed.get("lines") or [])[:60]:
            out_lines.append({"extCode": str(ln.get("extCode") or ""), "desc": str(ln.get("desc") or ""),
                              "qty": _f(ln.get("qty")), "uom": str(ln.get("uom") or "EA") or "EA",
                              "price": _f(ln.get("price")), "amount": _f(ln.get("amount"))})
        return {"reply": str(parsed.get("reply") or "แก้ไขเรียบร้อยแล้ว"), "header": h, "lines": out_lines}
    except Exception:
        return None


# ---------------------------------------------------------------- headers
def _blank_header(module: str) -> dict:
    if module == "SO":
        return {"docType": "PURCHASE ORDER", "poNo": "", "poDate": "", "customerName": "", "customerTaxId": "",
                "shipToName": "", "shipToAddress": "", "deliveryDate": "", "currency": "THB",
                "paymentTerms": "", "incoterms": "", "subTotal": 0, "vatAmount": 0,
                "totalAmount": 0, "remark": ""}
    if module == "II":
        # Incoming Invoices (Fiori F0859) — เอกสารตั้งหนี้เจ้าหนี้แบบไม่มี PO อ้างอิง แยกจาก Supplier Invoice (MIRO)
        # เก็บในตาราง ocr.Document/DocumentLine ชุดเดียวกัน แต่แยกด้วย Module='II' (คนละชุดข้อมูลกับ AP/SO โดยสมบูรณ์)
        return {"docType": "ใบกำกับภาษี/ใบแจ้งหนี้", "transaction": "", "invoiceNo": "", "invoiceDate": "",
                "postingDate": "", "vendorName": "", "vendorTaxId": "", "sapDocType": "", "currency": "THB",
                "calculateTax": "", "taxCode": "", "businessPlace": "", "headerText": "",
                "subTotal": 0, "vatRate": 7, "vatAmount": 0, "whtAmount": 0, "totalAmount": 0,
                # Address and Bank Data — ข้อมูลที่อยู่/ธนาคารของเจ้าหนี้ (override เฉพาะเอกสารนี้)
                "language": "", "vendorName2": "", "vendorName3": "", "vendorName4": "",
                "addressStreet": "", "addressCity": "", "addressPostalCode": "", "addressCountry": "",
                "vendorEmail": "", "bankCountry": "", "bankKey": "", "bankAccountNo": "", "taxNumber3": "",
                # Payment
                "baselineDate": "", "paymentTerms": "", "paymentMethod": "", "paymentBlock": "",
                "partnerBank": "", "houseBank": "", "bankAccountId": "",
                # Details
                "assignmentText": "", "refKey1": "", "refKey2": "", "refKey3": "",
                # Withholding Tax
                "whtCode": "", "whtBaseAmount": 0,
                # Line Items (G/L distribution) — โครงสร้างเดียวกับ glItems ของ Supplier Invoice
                "glItems": []}
    if module == "PODP":
        # Purchase Order Down Payments — module แยกจาก Supplier Invoice/Incoming Invoices โดยสมบูรณ์
        # ฟิลด์เริ่มต้นแบบพื้นฐาน (baseline เดียวกับ Incoming Invoices) รอรายละเอียดฟิลด์ฉบับเต็มจากผู้ใช้
        return {"docType": "PO Down Payment", "invoiceNo": "", "invoiceDate": "", "postingDate": "",
                "vendorName": "", "vendorTaxId": "", "poRef": "", "currency": "THB", "paymentTerms": "",
                "totalAmount": 0}
    return {"docType": "ใบกำกับภาษี/ใบแจ้งหนี้", "invoiceNo": "", "invoiceDate": "", "postingDate": "",
            "vendorName": "", "vendorTaxId": "", "branch": "", "poRef": "", "currency": "THB",
            "paymentTerms": "", "subTotal": 0, "vatRate": 7, "vatAmount": 0, "whtAmount": 0, "totalAmount": 0,
            # ฟิลด์สำหรับเอกสารประเภท Trade (MIRO) — ผู้ใช้กรอกเอง ไม่ได้เดาจาก OCR
            "taxCode": "", "calculateTax": "", "baselineDate": "", "paymentMethod": "", "assignmentText": "",
            # ฟิลด์เพิ่มสำหรับเอกสารประเภท Non-Trade ไม่มี PO — ผู้ใช้กรอกเอง ไม่ได้เดาจาก OCR
            "companyCode": "", "headerText": "",
            # ฟิลด์เพิ่มสำหรับ Supplier Invoice (MIRO) ฉบับเต็ม — ผู้ใช้กรอกเอง ไม่ได้เดาจาก OCR
            "businessPlace": "", "refDocType": "", "taxDate": "", "taxReportingDate": "", "taxFulfillDate": "",
            "paymentBlock": "", "partnerBank": "", "houseBank": "", "bankAccountId": "",
            "unplannedDeliveryCost": 0, "whtCode": "", "whtBaseAmount": 0,
            "glItems": []}


def _po_number_date(text: str) -> tuple[str, str]:
    """บางฟอร์ม (เช่น Henkel/European buyer) เขียนเลขที่ PO + รหัสผู้ซื้อ + วันที่ ติดกัน
    เป็นรูปแบบเฉพาะ เช่น '4593442527 / GGA / 09.03.2026' ซึ่งเจาะจงพอที่จะจับได้แม่นยำ
    โดยไม่ต้องพึ่งป้ายกำกับ (ป้ายกำกับอาจถูกตัดคำ/ขึ้นบรรทัดใหม่จนจับไม่ได้)"""
    m = re.search(r"\b(\d{6,12})\s*/\s*[A-Z]{2,5}\s*/\s*(\d{1,2}\.\d{1,2}\.\d{2,4})\b", text, re.I)
    if not m:
        return "", ""
    d, mo, y = m.group(2).split(".")
    return m.group(1), _iso_date(int(d), int(mo), int(y))


def _sane_vat_rate(vat: float, sub: float) -> float:
    """VAT ไทยเกือบทั้งหมดคือ 7% (หรือ 0% ถ้าได้รับยกเว้น) — ถ้าคำนวณได้ค่าที่ไม่สมเหตุผล
    (มักเกิดจาก OCR อ่านตัวเลขผิดจนยอด VAT ผิดเพี้ยนไปมาก) ให้ใช้ 7% แทน กัน overflow คอลัมน์ DB ด้วย"""
    if not (sub and vat):
        return 7
    rate = round(vat / sub * 100, 2)
    return rate if 0 <= rate <= 30 else 7


def _tax_summary(text: str) -> tuple[float, float]:
    """บางฟอร์มสรุปภาษี/ยอดรวมเป็นตารางแยกต่างหาก เช่น
       'Tax  Tax  Total Order' บรรทัดถัดมา '7.000  11,200.00  171,200.00'
    คืนค่า (vat, total) จากแถวตัวเลขที่อยู่ใต้หัวตารางที่มีคำว่า Total กับ Order คู่กัน"""
    lines = text.splitlines()
    for i, ln in enumerate(lines):
        if re.search(r"Total\s*Order|Order\s*Total", ln, re.I):
            for nxt in lines[i + 1:i + 3]:
                nums = re.findall(NUM, nxt)
                if len(nums) >= 2:
                    vat, total = _f(nums[-2]), _f(nums[-1])
                    if total > 0:
                        return vat, total
    return 0.0, 0.0


def parse_text(text: str, module: str, blocks: dict | None = None, provider: str = "text") -> dict:
    h = _blank_header(module)
    lines = parse_lines(text)
    blocks = blocks or {}

    # หมายเหตุ: ป้ายกำกับภาษาไทยห่อด้วย th() เพื่อให้จับได้แม้ OCR แทรกช่องว่างระหว่างอักขระ
    total = find_amount(text, [r"PURCHASE\s*ORDER\s*TOTAL", r"GRAND\s*TOTAL", th("รวมทั้งสิ้น"),
                               th("ยอดรวมสุทธิ"), th("จำนวนเงินรวมทั้งสิ้น"), th("จำนวนเงินรวมทั้งสิน"),
                               r"NET\s*(?:AMOUNT|TOTAL)", r"Total\s*Amount", r"(?<!TAX\s)\bTOTAL\b(?!\s*TAX)"])
    sub = find_amount(text, [th("รวมเป็นเงิน"), th("มูลค่าสินค้า"), th("ราคาสินค้า"), r"Sub\s*-?\s*total",
                             r"Amount\s*before", r"TOTAL\s*BEFORE"])
    vat = find_amount(text, [r"TOTAL\s*TAX", th("ภาษีมูลค่าเพิ่ม"), r"\bVAT\b", th("ภาษี") + r"\s*7"])
    wht = find_amount(text, [th("หัก") + r"\s*" + th("ณ") + r"\s*" + th("ที่จ่าย"), r"WHT", r"Withholding"])
    sum_lines = round(sum(l["amount"] for l in lines), 2)
    if not vat and not total:                        # ยังไม่พบเลย ลองหาจากตารางสรุปภาษีแยกต่างหาก
        vat, total = _tax_summary(text)
    if not sub:
        sub = round(total - vat, 2) if (total and vat) else sum_lines
    if not total:
        total = round(sub + vat, 2) if sub else sum_lines

    cur = "THB"
    mc = re.search(r"CURRENCY\s*[:\s]\s*([A-Z]{3})|สกุลเงิน\s*[:\s]\s*([A-Z]{3})", text, re.I)
    if mc:
        cur = (mc.group(1) or mc.group(2)).upper()
    terms = ""
    mt = (re.search(r"(?<!INCO)TERMS?\D{0,60}?(\d{1,3})\s*(?:DAYS?|วัน)", text, re.I)
          or re.search(r"เครดิต\D{0,20}?(\d{1,3})\s*วัน", text)
          # รูปแบบย่อแบบยุโรป เช่น "Terms of payment: End of m.,120d,..." -> "120d"
          or re.search(r"(?:TERMS|PAYMENT)\D{0,80}?(\d{1,3})\s*[Dd](?![A-Za-z])", text, re.I))
    if mt:
        terms = mt.group(1) + " วัน"
    else:
        # ต้องมีตัวเลขประกอบ กัน false-positive จับคำทั่วไป เช่น "Terms of payment" -> "of"
        m2 = re.search(r"(?<!INCO)(?:PAYMENT\s*)?TERMS\s*[:\s]\s*(CASH|COD|เงินสด|[A-Z]{1,3}\d{2,4})", text, re.I)
        if m2:
            terms = m2.group(1).strip()
        else:
            # ตัวคั่นไม่ข้ามขึ้นบรรทัดใหม่ (ใช้ [ \t]* ไม่ใช่ \s*) กันไม่ให้กระโดดไปจับข้อความ
            # ของประโยค/ข้อถัดไปที่ไม่เกี่ยวข้อง เช่น ข้อสัญญาที่ขึ้นต้นด้วยตัวเลข "10 ..."
            m3 = re.search(r"\bCond(?:ition)?s?\s*[:\.][ \t]*([^\n]{3,40})", text, re.I)
            if m3:
                terms = re.sub(r"\s{2,}", " ", m3.group(1)).strip().rstrip(".")
    inco = ""
    mi = re.search(r"INCOTERMS?\s*[:\s]\s*([A-Z]{3}\b[^\n]{0,20})", text, re.I)
    if mi:
        inco = mi.group(1).strip()

    if module != "SO":
        d = find_date(text, [th("วันที่ใบกำกับภาษี"), th("วันที่ใบแจ้งหนี้"), r"INVOICE\s*DATE", r"DATE"])
        h.update({"invoiceNo": find_doc_no(text, [th("เลขที่ใบกำกับภาษี"), th("เลขที่ใบแจ้งหนี้"),
                                                  r"INVOICE\s*NO\.?", r"INV\.?\s*NO\.?", th("เลขที่"), r"No\.?"]),
                  "invoiceDate": d, "postingDate": d,
                  "vendorName": find_company(text), "vendorTaxId": _partner_tax(text),
                  "poRef": find_doc_no(text, [r"อ้างอิง\s*PO", r"P\.?O\.?\s*No\.?", r"Purchase\s*Order"]),
                  "currency": cur, "paymentTerms": terms,
                  "subTotal": sub, "vatAmount": vat, "whtAmount": wht, "totalAmount": total,
                  "vatRate": _sane_vat_rate(vat, sub)})
    else:
        due = sorted([l["dueDate"] for l in lines if l.get("dueDate")])
        cust_name, cust_pos = find_company_with_pos(text)
        ship_name, ship_addr = _ship_block(text, blocks)
        if not ship_name and cust_name:
            # ฟอร์มบางแบบไม่มีป้าย "SHIP TO" ที่จับได้ (เช่น label ขึ้นบรรทัดใหม่กลางชื่อคู่ค้า)
            # ที่อยู่จัดส่งส่วนใหญ่คือที่อยู่เดียวกับที่ระบุไว้ใต้ชื่อคู่ค้าที่หัวเอกสาร
            ship_name, ship_addr = cust_name, _address_after(text.splitlines(), cust_pos)
        po_no2, po_date2 = _po_number_date(text)          # รูปแบบ "เลขที่ / รหัส / วันที่" ติดกัน (เช่น Henkel)
        po_no = po_no2 or find_doc_no(text, [r"P\.?O\.?\s*No\.?", r"ใบสั่งซื้อเลขที่", r"เลขที่ใบสั่งซื้อ",
                                             r"PURCHASE\s*ORDER\s*(?:NO\.?|NUMBER)", r"เลขที่", r"ORDER\s*NO\.?"])
        po_date = po_date2 or find_date(text, [
            r"PRINT\s*DATE", r"P\.?O\.?\s*DATE", r"ORDER\s*DATE", r"วันที่เอกสาร", r"วันที่",
            # ป้ายกำกับ "DATE" เดี่ยว ๆ เป็นทางเลือกสุดท้าย — กันไม่ให้ไปจับ "Delivery/Due/Required date"
            # ซึ่งเป็นคนละความหมาย (วันที่ต้องการรับของ ไม่ใช่วันที่เอกสาร)
            r"(?<!Delivery )(?<!Due )(?<!Required )(?<!Ship )(?<!Requested )DATE"])
        h.update({"poNo": po_no, "poDate": po_date,
                  "customerName": cust_name,
                  "customerTaxId": _partner_tax(text),
                  "shipToName": ship_name, "shipToAddress": ship_addr,
                  "deliveryDate": due[0] if due else find_date(text, [r"DELIVERY\s*DATE", r"DUE\s*DATE",
                                                                      r"REQUIRED\s*DATE", r"วันที่ส่งของ",
                                                                      r"กำหนดส่ง"]),
                  "currency": cur, "paymentTerms": terms, "incoterms": inco,
                  "subTotal": sub, "vatAmount": vat, "totalAmount": total})

    filled = sum(1 for k, v in h.items() if str(v or "").strip() not in ("", "0", "0.0"))
    conf = 0.35 + 0.03 * filled + min(0.2, 0.05 * len(lines))
    if provider == "ocr":
        conf -= 0.1                                      # ข้อความจาก OCR สัญญาณรบกวนสูงกว่าข้อความที่ฝังในไฟล์
    return {"header": h, "lines": lines, "confidence": round(min(max(conf, 0.1), 0.9), 2),
            "provider": provider, "rawText": text[:20000]}


def _partner_tax(text: str) -> str:
    """เลขผู้เสียภาษีของคู่ค้า — ข้ามเลขของบริษัทเราเอง"""
    own = re.sub(r"\D", "", config.OWN_TAX_ID or "")
    for m in re.finditer(TAX_LABEL, text, re.I):
        t = TAX13.search(text[m.end():m.end() + 250])
        if t:
            v = re.sub(r"\D", "", t.group(0))
            if v and v != own:
                return v
    for t in TAX13.finditer(re.sub(r"[\s\-]", "", text)):
        v = re.sub(r"\D", "", t.group(0))
        if v != own:
            return v
    return ""


def _ship_block(text: str, blocks: dict):
    """ที่อยู่จัดส่งจากคอลัมน์ขวาของฟอร์ม — คืนค่าว่างถ้าแยกคอลัมน์ไม่ได้
    (ตั้งใจไม่ fallback ไป regex บรรทัดเดียวที่นี่ เพราะ '\\s' ข้ามขึ้นบรรทัดใหม่ได้ ทำให้กระโดด
    ไปจับข้อความที่ไม่เกี่ยวข้องในบรรทัดถัดไปมาผิด ๆ — ผู้เรียก (parse_text) จะ fallback ไปใช้
    ที่อยู่ใต้ชื่อคู่ค้าที่หัวเอกสารแทน ซึ่งปลอดภัยกว่า)"""
    rows = [r for r in (blocks.get("right") or [])
            if r and not re.match(r"^(INVOICE\s*ADDRESS|TEL|FAX|E-?MAIL|\*+|REQUIRED|Tax\s*ID)", r, re.I)]
    rows = [re.sub(r"\(\s*\d{6,}\s*\)", "", r).strip() for r in rows]
    rows = [r for r in rows if len(r) > 2]
    if rows:
        name = re.sub(r"\s*(PURCHASING\s+OFFICER|ATTN\.?\s.*|TEL\s*[:.].*|CONTACT.*)$", "",
                      rows[0], flags=re.I).strip()
        addr = " ".join(rows[1:])[:400]
        if is_own_company(name) and len(rows) > 1:       # คอลัมน์สลับข้าง
            left = [r for r in (blocks.get("left") or []) if len(r) > 2]
            if left:
                name, addr = left[0], " ".join(left[1:])[:400]
        return name[:200], addr
    return "", ""


# ---------------------------------------------------------------- demo data
DEMO = {
    "SO": [
        {"name": "PO-SCI-6801234.pdf", "label": "ใบสั่งซื้อลูกค้า — ข้อมูลครบ", "confidence": 0.97,
         "header": {"docType": "PURCHASE ORDER", "poNo": "PO-6801234", "poDate": "2026-08-10",
                    "customerName": "บริษัท สยาม เคมิคอล อินดัสทรี จำกัด", "customerTaxId": "0105533012345",
                    "shipToName": "คลังสินค้า บางปู",
                    "shipToAddress": "นิคมอุตสาหกรรมบางปู ซ.7 ต.แพรกษา อ.เมือง สมุทรปราการ 10280",
                    "deliveryDate": "2026-08-25", "currency": "THB", "paymentTerms": "เครดิต 30 วัน",
                    "incoterms": "DDP", "totalAmount": 385000, "remark": "ส่งของช่วงเช้า 08:00-11:00"},
         "lines": [{"extCode": "SCI-TIO2-902", "desc": "TIO2 R902 ถุง 25 กก.", "qty": 5000, "uom": "KG", "price": 62, "amount": 310000},
                   {"extCode": "SCI-CACO3-800", "desc": "แคลเซียมคาร์บอเนต CC800", "qty": 2500, "uom": "KG", "price": 30, "amount": 75000}]},
        {"name": "PO-TPG-2026-0842.pdf", "label": "ใบสั่งซื้อลูกค้า — สินค้าไม่พบใน Master", "confidence": 0.91,
         "header": {"docType": "PURCHASE ORDER", "poNo": "TPG-2026-0842", "poDate": "2026-08-12",
                    "customerName": "THAI POLYMER GROUP PCL.", "customerTaxId": "0107536000123",
                    "shipToName": "โรงงานอยุธยา (โรจนะ)",
                    "shipToAddress": "สวนอุตสาหกรรมโรจนะ ต.คานหาม อ.อุทัย พระนครศรีอยุธยา 13210",
                    "deliveryDate": "2026-08-28", "currency": "THB", "paymentTerms": "เครดิต 60 วัน",
                    "incoterms": "DDP", "totalAmount": 640000, "remark": ""},
         "lines": [{"extCode": "TPG-PP1100", "desc": "PP HOMO 1100N", "qty": 10000, "uom": "KG", "price": 52, "amount": 520000},
                   {"extCode": "TPG-XY-500", "desc": "XYLENE INDUSTRIAL GRADE", "qty": 3000, "uom": "L", "price": 40, "amount": 120000}]},
        {"name": "PO-UNKNOWN-9931.jpg", "label": "ใบสั่งซื้อ — ลูกค้าใหม่ (ไม่พบใน Master)", "confidence": 0.86,
         "header": {"docType": "PURCHASE ORDER", "poNo": "PO-9931", "poDate": "2026-08-13",
                    "customerName": "บริษัท นิว เวิลด์ เทรดดิ้ง จำกัด", "customerTaxId": "0105566001111",
                    "shipToName": "คลังสินค้าลาดกระบัง",
                    "shipToAddress": "ถ.ฉลองกรุง แขวงลำปลาทิว เขตลาดกระบัง กรุงเทพฯ 10520",
                    "deliveryDate": "2026-08-30", "currency": "THB", "paymentTerms": "เงินสด",
                    "incoterms": "EXW", "totalAmount": 124000, "remark": ""},
         "lines": [{"extCode": "NW-EP828", "desc": "อีพ็อกซี่เรซิน EP-828", "qty": 2000, "uom": "KG", "price": 62, "amount": 124000}]},
    ],
    "AP": [
        {"name": "INV-UC-25080456.pdf", "label": "ใบกำกับภาษี/ใบส่งของ — ข้อมูลครบ", "confidence": 0.96,
         "header": {"docType": "ใบกำกับภาษี/ใบส่งของ", "invoiceNo": "UC-25080456", "invoiceDate": "2026-08-08",
                    "postingDate": "2026-08-08", "vendorName": "บริษัท ยูนิเวอร์แซล เคมีคอล ซัพพลาย จำกัด",
                    "vendorTaxId": "0105546007788", "branch": "สำนักงานใหญ่", "poRef": "4500012345",
                    "currency": "THB", "paymentTerms": "เครดิต 30 วัน", "subTotal": 268000, "vatRate": 7,
                    "vatAmount": 18760, "whtAmount": 0, "totalAmount": 286760},
         "lines": [{"extCode": "UC-TL-100", "desc": "TOLUENE INDUSTRIAL", "qty": 4000, "uom": "L", "price": 42, "amount": 168000},
                   {"extCode": "UC-MEK-995", "desc": "MEK 99.5 PCT", "qty": 2000, "uom": "L", "price": 50, "amount": 100000}]},
        {"name": "INV-NC-LOG-6808.pdf", "label": "ใบแจ้งหนี้ค่าขนส่ง — มีภาษีหัก ณ ที่จ่าย", "confidence": 0.93,
         "header": {"docType": "ใบแจ้งหนี้", "invoiceNo": "NC-LOG-6808", "invoiceDate": "2026-08-11",
                    "postingDate": "2026-08-11", "vendorName": "บริษัท เอ็น.ซี. โลจิสติกส์ เซอร์วิส จำกัด",
                    "vendorTaxId": "0115551002233", "branch": "สำนักงานใหญ่", "poRef": "",
                    "currency": "THB", "paymentTerms": "เครดิต 15 วัน", "subTotal": 85000, "vatRate": 7,
                    "vatAmount": 5950, "whtAmount": 2550, "totalAmount": 88400},
         "lines": [{"extCode": "NC-FREIGHT", "desc": "ค่าขนส่ง เดือน ก.ค. 2569", "qty": 1, "uom": "AU", "price": 85000, "amount": 85000}]},
        {"name": "INV-SCAN-77120.jpg", "label": "ใบแจ้งหนี้สแกน — ผู้ขายและสินค้าไม่พบ", "confidence": 0.78,
         "header": {"docType": "ใบกำกับภาษี", "invoiceNo": "77120", "invoiceDate": "2026-08-12",
                    "postingDate": "2026-08-12", "vendorName": "หจก. รุ่งเรือง เคมีภัณฑ์",
                    "vendorTaxId": "0103552009999", "branch": "สำนักงานใหญ่", "poRef": "",
                    "currency": "THB", "paymentTerms": "เงินสด", "subTotal": 52000, "vatRate": 7,
                    "vatAmount": 3640, "whtAmount": 0, "totalAmount": 55640},
         "lines": [{"extCode": "RR-ACET", "desc": "ACETONE 99%", "qty": 1300, "uom": "L", "price": 40, "amount": 52000}]},
    ],
    "II": [
        {"name": "II-BANGKOK-AUDIT-6808.pdf", "label": "ใบแจ้งหนี้ค่าที่ปรึกษา — ไม่มี PO อ้างอิง", "confidence": 0.9,
         "header": {"docType": "ใบแจ้งหนี้", "transaction": "R", "invoiceNo": "AUD-6808", "invoiceDate": "2026-08-14",
                    "postingDate": "2026-08-14", "vendorName": "บริษัท กรุงเทพ ออดิท จำกัด",
                    "vendorTaxId": "0105558003344", "sapDocType": "KR", "currency": "THB",
                    "calculateTax": "", "taxCode": "", "businessPlace": "", "headerText": "ค่าที่ปรึกษาบัญชีเดือน ก.ค.",
                    "subTotal": 40000, "vatRate": 7, "vatAmount": 2800, "whtAmount": 1200, "totalAmount": 42800,
                    "glItems": [{"glAccount": "5200100", "drCr": "D", "amount": 40000, "taxCode": "V7",
                                 "assignment": "AUD-6808", "itemText": "ค่าที่ปรึกษาบัญชี", "costCenter": "CC-1001"}]},
         "lines": []},
    ],
    "PODP": [
        {"name": "PODP-4500098765.pdf", "label": "เงินมัดจำล่วงหน้าตาม PO", "confidence": 0.9,
         "header": {"docType": "PO Down Payment", "invoiceNo": "DP-4500098765", "invoiceDate": "2026-08-15",
                    "postingDate": "2026-08-15", "vendorName": "บริษัท ไทยเอ็นจิเนียริ่ง แอนด์ คอนสตรัคชั่น จำกัด",
                    "vendorTaxId": "0105549001122", "poRef": "4500098765", "currency": "THB",
                    "paymentTerms": "เครดิต 30 วัน", "totalAmount": 150000},
         "lines": []},
    ],
}


def demo_doc(module: str, index: int = 0) -> dict:
    s = DEMO[module][index % len(DEMO[module])]
    return {"header": dict(s["header"]), "lines": [dict(x) for x in s["lines"]],
            "confidence": s["confidence"], "provider": "demo", "rawText": "",
            "sampleName": s["name"]}


# ---------------------------------------------------------------- entry point
# รายการ engine ที่เลือกได้ในหน้าเว็บ — id ต้องตรงกับค่าที่ extract() รู้จัก
OCR_PROVIDERS = [
    {"id": "auto", "label": "อัตโนมัติ (แนะนำ)",
     "desc": "อ่านข้อความในไฟล์ก่อน ถ้าเป็นไฟล์สแกนจะใช้ Tesseract OCR ให้อัตโนมัติ — ไม่มีค่าใช้จ่าย", "ready": True},
    {"id": "text", "label": "ข้อความในไฟล์เท่านั้น",
     "desc": "เร็วที่สุด แต่ใช้ไม่ได้กับไฟล์สแกน/รูปภาพ", "ready": True},
    {"id": "tesseract", "label": "Tesseract OCR (ในเครื่อง)",
     "desc": "บังคับอ่านด้วย OCR แม้ไฟล์จะมีชั้นข้อความอยู่แล้ว — ไม่มีค่าใช้จ่าย",
     "ready": bool(config.TESSERACT_CMD)},
    {"id": "typhoon", "label": "Typhoon OCR (ไทยโดยเฉพาะ)",
     "desc": "โมเดล OCR ไทย/อังกฤษของ SCB 10X แม่นกว่า Tesseract มากสำหรับเอกสารไทย ลายมือ/ตารางซับซ้อน — "
             "ต้องตั้งค่า TYPHOON_API_KEY ใน .env (มีค่าใช้จ่ายต่อหน้า ดูราคาที่ opentyphoon.ai)",
     "ready": bool(config.TYPHOON_API_KEY)},
    {"id": "azure", "label": "Azure Document Intelligence",
     "desc": "แม่นกว่ามากสำหรับฟอร์ม/ตาราง — ต้องตั้งค่า AZURE_DI_ENDPOINT/AZURE_DI_KEY ใน .env (มีค่าใช้จ่ายต่อหน้า)",
     "ready": bool(config.AZURE_DI_ENDPOINT and config.AZURE_DI_KEY)},
    {"id": "claude_text", "label": "OCR + Claude จัดโครงสร้าง (ประหยัด)",
     "desc": "อ่านข้อความด้วย Tesseract/pdfplumber ก่อน (ฟรี) แล้วส่งข้อความให้ Claude จัดเป็น JSON — "
             "ถูกกว่า Claude Vision มาก เหมาะกับเอกสารซ้ำ ๆ (PO/Invoice) ที่ OCR อ่านตัวอักษรออกมาได้ระดับหนึ่ง "
             "ต้องตั้งค่า ANTHROPIC_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง แต่ถูกกว่า Claude Vision)",
     "ready": bool(config.ANTHROPIC_API_KEY)},
    {"id": "claude", "label": "Claude Vision (AI)",
     "desc": "แม่นที่สุดสำหรับเอกสารยุ่งเหยิง/ตารางซับซ้อน เข้าใจบริบทได้ — ต้องตั้งค่า ANTHROPIC_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง)",
     "ready": bool(config.ANTHROPIC_API_KEY)},
    {"id": "gemini", "label": "Gemini Vision (AI)",
     "desc": "โมเดล Vision ของ Google อ่านภาพเอกสารโดยตรง เข้าใจบริบทได้ — ต้องตั้งค่า GEMINI_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง)",
     "ready": bool(config.GEMINI_API_KEY)},
    {"id": "openai", "label": "ChatGPT Vision (AI)",
     "desc": "โมเดล GPT-4o/GPT-5 ของ OpenAI อ่านภาพเอกสารโดยตรง เข้าใจบริบทได้ — ต้องตั้งค่า OPENAI_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง)",
     "ready": bool(config.OPENAI_API_KEY)},
    {"id": "demo", "label": "ข้อมูลตัวอย่าง (ทดสอบ)",
     "desc": "ไม่อ่านไฟล์จริง ใช้สำหรับทดสอบขั้นตอน Mapping/ส่ง SAP เท่านั้น", "ready": True},
]


def _demo_fallback(path: Path, module: str, note: str) -> dict:
    d = demo_doc(module, abs(hash(path.name)) % len(DEMO[module]))
    d["provider"] = "demo"
    d["_note"] = note
    return d


def _read_failed(path: Path, module: str, note: str) -> dict:
    """เมื่ออ่านเอกสารไม่สำเร็จจริง ๆ (ต่างจากการเลือก 'ข้อมูลตัวอย่าง' โดยตั้งใจ) — คืนข้อมูลว่างเปล่าตรง ๆ
    ห้ามใช้ engine อื่นหรือข้อมูลตัวอย่างมาแทนแบบเงียบ ๆ เพราะจะทำให้ผู้ใช้เข้าใจผิดว่าเป็นข้อมูลจริงจากเอกสาร
    ผู้ใช้ต้องเห็น popup แจ้งเตือนชัดเจน แล้วแนบภาพเอกสารไปคุยกับแชท AI เพื่อให้อ่านให้แทน"""
    return {"header": _blank_header(module), "lines": [], "confidence": 0.0, "provider": "failed", "_note": note}


# ฟิลด์สำคัญต่อโมดูล ใช้สร้างเหตุผลอธิบายว่าทำไมความแม่นยำไม่ถึง 100% — ไม่รวมทุกฟิลด์ เพราะบางฟิลด์
# (เช่น remark, incoterms) ว่างได้ตามปกติโดยไม่ถือว่าอ่านไม่ครบ
_AP_IMPORTANT = {"invoiceNo": "เลขที่ใบกำกับภาษี/ใบแจ้งหนี้", "invoiceDate": "วันที่เอกสาร",
                 "vendorName": "ชื่อผู้ขาย", "vendorTaxId": "เลขทะเบียนผู้เสียภาษีของผู้ขาย",
                 "totalAmount": "ยอดรวมทั้งสิ้น"}
_SO_IMPORTANT = {"poNo": "เลขที่ใบสั่งซื้อ", "poDate": "วันที่เอกสาร", "customerName": "ชื่อลูกค้า",
                 "customerTaxId": "เลขทะเบียนผู้เสียภาษีของลูกค้า", "totalAmount": "ยอดรวมทั้งสิ้น"}
_PROVIDER_CAVEAT = {
    "ocr": "อ่านด้วย Tesseract OCR จากไฟล์สแกน ซึ่งแม่นยำต่ำกว่าอ่านข้อความจากไฟล์ต้นฉบับโดยตรง",
    "typhoon": "อ่านด้วย Typhoon OCR จากภาพเอกสาร อาจมีข้อผิดพลาดจากคุณภาพภาพ/ลายมือ",
    "azure": "อ่านด้วย Azure Document Intelligence จากภาพเอกสาร",
    "claude": "อ่านด้วย Claude Vision จากภาพเอกสาร อาจตีความคลาดเคลื่อนได้ในบางจุด",
    "claude_text": "ใช้ OCR อ่านข้อความก่อนแล้วให้ Claude จัดโครงสร้าง ความแม่นยำขึ้นกับคุณภาพข้อความจาก OCR รอบแรก",
    "gemini": "อ่านด้วย Gemini Vision จากภาพเอกสาร อาจตีความคลาดเคลื่อนได้ในบางจุด",
    "openai": "อ่านด้วย ChatGPT Vision จากภาพเอกสาร อาจตีความคลาดเคลื่อนได้ในบางจุด",
}


def _confidence_note(module: str, header: dict, lines: list[dict], provider: str) -> str:
    """สร้างคำอธิบายว่าทำไมความแม่นยำ OCR ไม่ถึง 100% — ใช้เก็บลง Document.OcrConfidenceNote
    เพื่อให้ผู้ใช้เห็นเหตุผลตรง ๆ แทนที่จะเห็นแค่ตัวเลข % เฉย ๆ"""
    important = _SO_IMPORTANT if module == "SO" else _AP_IMPORTANT
    missing = [label for key, label in important.items()
              if str(header.get(key) or "").strip() in ("", "0", "0.0")]
    reasons = []
    if missing:
        reasons.append("ไม่พบข้อมูล: " + ", ".join(missing))
    if not lines:
        reasons.append("ไม่พบรายการสินค้า/บริการ (Item Detail)")
    caveat = _PROVIDER_CAVEAT.get(provider)
    if caveat:
        reasons.append(caveat)
    return " / ".join(reasons)


_TOKEN_PRICE = {                            # (ราคาต่อ 1 ล้าน input token, ราคาต่อ 1 ล้าน output token) เป็น USD
    "claude": (config.PRICE_CLAUDE_IN, config.PRICE_CLAUDE_OUT),
    "claude_text": (config.PRICE_CLAUDE_IN, config.PRICE_CLAUDE_OUT),
    "gemini": (config.PRICE_GEMINI_IN, config.PRICE_GEMINI_OUT),
    "openai": (config.PRICE_OPENAI_IN, config.PRICE_OPENAI_OUT),
}


def _estimate_cost(provider: str, tokens_in, tokens_out):
    """ประมาณค่าใช้จ่าย (USD) จากจำนวน token ที่ใช้จริง x ราคาต่อ token ของ provider นั้น ๆ แยกส่วน input/output
    คืนค่า (costIn, costOut, costTotal, currency) หรือ (None, None, None, None) ถ้า provider นี้ไม่มีค่าใช้จ่ายต่อ token
    (Tesseract/text/demo ฯลฯ)"""
    price = _TOKEN_PRICE.get(provider)
    if not price or tokens_in is None or tokens_out is None:
        return None, None, None, None
    price_in, price_out = price
    cost_in = round((tokens_in / 1_000_000) * price_in, 4)
    cost_out = round((tokens_out / 1_000_000) * price_out, 4)
    return cost_in, cost_out, round(cost_in + cost_out, 4), "USD"


def extract(path: Path, module: str, provider_override: str | None = None) -> dict:
    """ห่อ _extract_dispatch() อีกชั้น เพื่อเติม confidenceNote (เหตุผลว่าทำไมความแม่นยำไม่ถึง 100%)
    และค่าใช้จ่ายโดยประมาณ (cost/currency) ให้ผลลัพธ์ทุก provider อย่างสม่ำเสมอ โดยไม่ต้องแก้ทุก return ใน _extract_dispatch()"""
    out = _extract_dispatch(path, module, provider_override)
    if out.get("provider") == "demo":
        out["confidenceNote"] = out.get("_note") or "ใช้ข้อมูลตัวอย่าง (demo) ไม่ได้อ่านจากไฟล์จริง"
    elif out.get("provider") == "failed":
        out["confidenceNote"] = out.get("_note") or "อ่านเอกสารไม่สำเร็จ"
    else:
        out["confidenceNote"] = _confidence_note(module, out.get("header") or {}, out.get("lines") or [],
                                                  out.get("provider") or "")
    cost_in, cost_out, cost_total, currency = _estimate_cost(
        out.get("provider") or "", out.get("tokensIn"), out.get("tokensOut"))
    out["costIn"] = cost_in
    out["costOut"] = cost_out
    out["cost"] = cost_total
    out["costCurrency"] = currency
    return out


def _extract_dispatch(path: Path, module: str, provider_override: str | None = None) -> dict:
    """provider_override: ค่าจากตัวเลือก engine ที่ผู้ใช้กดในหน้าเว็บ (ถ้าระบุ จะ 'บังคับ' ใช้ตัวนั้น
    ไม่ fallback ไปตัวอื่นเงียบ ๆ) — ปล่อยว่างหรือ 'auto' จะใช้สายข้อความ→OCR ในเครื่องตามปกติ
    (ไม่เรียก Azure/Claude อัตโนมัติ เพราะมีค่าใช้จ่าย ต้องเลือกเองเท่านั้น)"""
    provider = (provider_override or config.OCR_PROVIDER or "auto").lower()
    ext = path.suffix.lower()

    if provider == "azure":
        data = azure_extract(path)
        if data:
            return _from_azure(data, module)
        return _read_failed(path, module,
                            "เชื่อมต่อ Azure Document Intelligence ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า AZURE_DI_ENDPOINT/AZURE_DI_KEY ใน .env")
    if provider == "claude":
        out = claude_vision_extract(path, module)
        if out:
            return out
        return _read_failed(path, module,
                            "เชื่อมต่อ Claude Vision ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า ANTHROPIC_API_KEY ใน .env")
    if provider == "gemini":
        out = gemini_vision_extract(path, module)
        if out:
            return out
        return _read_failed(path, module,
                            "เชื่อมต่อ Gemini Vision ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า GEMINI_API_KEY ใน .env")
    if provider == "openai":
        out = openai_vision_extract(path, module)
        if out:
            return out
        return _read_failed(path, module,
                            "เชื่อมต่อ ChatGPT Vision ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า OPENAI_API_KEY ใน .env")
    if provider == "claude_text":
        pre_text = pdf_text(path) if ext == ".pdf" else ""
        if not pre_text.strip() and (ext in IMAGE_EXT or ext == ".pdf"):
            pre_text = tesseract_text(path)
        if not pre_text.strip():
            return _read_failed(path, module, "OCR อ่านข้อความจากไฟล์ไม่ได้ จึงส่งให้ Claude จัดโครงสร้างไม่ได้")
        out = claude_text_extract(path, module, pre_text)
        if out:
            return out
        return _read_failed(path, module,
                            "เชื่อมต่อ Claude (จัดโครงสร้างจากข้อความ) ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า ANTHROPIC_API_KEY ใน .env")
    if provider == "typhoon":
        text, err = typhoon_text(path)
        if not text.strip():
            return _read_failed(path, module, err or "Typhoon OCR ไม่คืนข้อความใด ๆ กลับมา")
        out = parse_text(text, module, pdf_blocks(path) if ext == ".pdf" else None, provider="typhoon")
        if out["lines"] or out["header"].get("vendorTaxId") or out["header"].get("customerTaxId"):
            return out
        out["confidence"] = 0.3
        return out
    if provider == "demo":
        return _demo_fallback(path, module, "")

    text, src = "", "text"
    if provider in ("auto", "text") and ext == ".pdf":
        text = pdf_text(path)
    if not text.strip() and provider in ("auto", "tesseract") and (ext in IMAGE_EXT or ext == ".pdf"):
        text = tesseract_text(path)
        src = "ocr"
    if text.strip() and len(re.sub(r"\s", "", text)) > 40:
        out = parse_text(text, module, pdf_blocks(path) if ext == ".pdf" else None, provider=src)
        if out["lines"] or out["header"].get("vendorTaxId") or out["header"].get("customerTaxId"):
            return out
        out["confidence"] = 0.3
        return out

    return _read_failed(path, module, "อ่านข้อความจากไฟล์ไม่ได้ (ไฟล์สแกน/ยังไม่ได้ตั้งค่า OCR engine)")
