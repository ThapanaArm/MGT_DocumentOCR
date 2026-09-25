using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

// Shared JSON-schema extraction prompt + response parser used by every "send the page image (or
// its OCR'd text) straight to an LLM and get back structured JSON" provider (Claude Vision,
// Claude-text-structuring, and — new in the .NET port — Gemini Vision and OpenAI Vision). Ported
// from _claude_prompt()/claude_vision_extract()'s response-parsing tail in ocr_engine.py, but
// factored out since it's now shared by more than one model family.
public static partial class VisionPrompt
{
    // mode="image": ask the model to read the page image itself (Claude/Gemini/OpenAI Vision).
    // mode="text": ask the model to structure OCR'd text handed to it (claude_text 2-tier mode).
    public static string Build(string module, string mode = "image")
    {
        string fields = module == "SO" ? """
        {
          "header": {
            "docType": "ประเภทเอกสาร เช่น PURCHASE ORDER",
            "poNo": "เลขที่ใบสั่งซื้อของลูกค้า",
            "poDate": "วันที่เอกสาร รูปแบบ YYYY-MM-DD",
            "customerName": "ชื่อลูกค้า (นิติบุคคลที่ออกใบสั่งซื้อ ไม่ใช่บริษัทผู้ขาย/ผู้รับเอกสาร)",
            "customerTaxId": "เลขทะเบียนนิติบุคคล/ผู้เสียภาษี 13 หลักของลูกค้า",
            "customerAddress": "ที่อยู่ของลูกค้า/บริษัทผู้ออกใบสั่งซื้อ (ที่อยู่จดทะเบียน หรือที่อยู่ที่ปรากฏใกล้ชื่อลูกค้าตอนต้นเอกสาร ใช้สำหรับออกใบกำกับภาษี/เอกสาร ไม่ใช่ที่อยู่จัดส่งสินค้า) — ถ้าเอกสารมีที่อยู่เดียว ให้ใส่ที่อยู่นั้นตรงนี้",
            "shipToCode": "รหัส Ship-to ที่ระบุในเอกสารเท่านั้น หากไม่พบให้เป็นค่าว่าง ห้ามใช้รหัส SAP แทน",
            "shipToName": "ชื่อสถานที่ส่งสินค้า (เฉพาะกรณีเอกสารระบุหัวข้อ Ship To/Deliver To/สถานที่จัดส่ง แยกต่างหากจากที่อยู่ลูกค้าเท่านั้น)",
            "shipToAddress": "ที่อยู่สำหรับจัดส่งสินค้า เฉพาะกรณีเอกสารระบุหัวข้อ Ship To/Deliver To/สถานที่จัดส่ง แยกต่างหากเท่านั้น — ถ้าเอกสารไม่มีที่อยู่จัดส่งแยกต่างหาก ให้ใส่สตริงว่าง "" (ห้ามใส่ค่าเดียวกับ customerAddress ซ้ำ)",
            "deliveryDate": "วันที่ต้องการรับสินค้า YYYY-MM-DD",
            "currency": "รหัสสกุลเงิน 3 ตัวอักษร เช่น THB", "paymentTerms": "เงื่อนไขการชำระเงิน",
            "incoterms": "Incoterms ถ้ามี", "subTotal": 0, "vatAmount": 0, "totalAmount": 0, "remark": ""
          },
          "lines": [{"extCode": "รหัสสินค้าตามเอกสาร", "desc": "ชื่อ/รายละเอียดสินค้า",
                     "qty": 0, "uom": "หน่วยนับ", "price": 0, "amount": 0,
                     "deliveryDate": "วันที่ต้องการรับสินค้าของรายการนี้ รูปแบบ YYYY-MM-DD เฉพาะกรณีเอกสารระบุวันส่งแยกไว้ต่อบรรทัด (เช่น มีคอลัมน์วันส่ง/กำหนดส่งในตารางสินค้า) หากรายการนี้ไม่ได้ระบุวันแยก ให้ใส่สตริงว่าง (ระบบจะใช้วันส่งรวมของเอกสารแทน)",
                     "itemNote": "หมายเหตุเฉพาะรายการสินค้านี้ ถ้าเอกสารระบุไว้แยกต่อบรรทัด เช่น รายละเอียดการจัดส่ง/ล็อต/สเปกเฉพาะรายการ หากไม่มีให้ใส่สตริงว่าง"}]
        }
        """ : """
        {
          "header": {
            "docType": "ประเภทเอกสาร เช่น ใบกำกับภาษี/ใบแจ้งหนี้",
            "invoiceNo": "เลขที่ใบกำกับภาษี/ใบแจ้งหนี้", "invoiceDate": "วันที่เอกสาร YYYY-MM-DD",
            "postingDate": "วันที่เดียวกับ invoiceDate ถ้าไม่มีระบุแยก",
            "vendorName": "ชื่อผู้ขาย/ผู้ออกใบกำกับภาษี (ไม่ใช่บริษัทผู้ซื้อ/ผู้รับเอกสาร)",
            "vendorTaxId": "เลขทะเบียนนิติบุคคล/ผู้เสียภาษี 13 หลักของผู้ขาย", "branch": "สาขาของผู้ขาย",
            "poRef": "เลขที่ใบสั่งซื้ออ้างอิงถ้ามี",
            "refDocType": "ประเภทของรายการในเอกสารนี้ ตอบเป็น \"1\" \"2\" \"3\" หรือ \"\": 1 = มีแต่รายการสินค้า/บริการ (goods/service items), 2 = มีแต่ต้นทุนอื่นที่วางแผนไว้ เช่น ค่าขนส่ง ค่าประกัน ค่าพิธีการศุลกากร ค่าคลังสินค้า (planned delivery costs), 3 = มีทั้งสองอย่าง, ถ้าดูไม่ออกให้ใส่ \"\"",
            "currency": "รหัสสกุลเงิน 3 ตัวอักษร เช่น THB", "paymentTerms": "เงื่อนไขการชำระเงิน",
            "subTotal": 0, "vatRate": 7, "vatAmount": 0, "whtAmount": 0, "totalAmount": 0
          },
          "lines": [{"extCode": "รหัสสินค้า/บริการถ้ามี", "desc": "ชื่อ/รายละเอียดสินค้าหรือบริการ",
                     "qty": 0, "uom": "หน่วยนับ", "price": 0, "amount": 0,
                     "vendorCode": "รหัสผู้ขายของรายการนี้ ถ้าเอกสารมีคอลัมน์ VENDOR ระบุไว้ ไม่มีให้ใส่สตริงว่าง",
                     "taxKind": "เฉพาะแถว VAT: \"INPUT\" ถ้ามาจากใบกำกับภาษี/ใบเสร็จรับเงิน หรือ \"DEFERRED\" ถ้ามาจากใบแจ้งหนี้/ใบวางบิล แถวอื่นใส่สตริงว่าง",
                     "taxDocNo": "เฉพาะแถว VAT: เลขที่ใบกำกับภาษีของใบนั้น แถวอื่นใส่สตริงว่าง",
                     "taxDocDate": "เฉพาะแถว VAT: วันที่ของใบกำกับภาษีนั้น YYYY-MM-DD แถวอื่นใส่สตริงว่าง",
                     "issuerName": "เฉพาะแถว VAT: ชื่อผู้ออกใบกำกับภาษีตามที่พิมพ์ในใบ แถวอื่นใส่สตริงว่าง",
                     "issuerTaxId": "เฉพาะแถว VAT: เลขประจำตัวผู้เสียภาษี 13 หลักของผู้ออกใบ (ตัวเลขล้วน ไม่ต้องมีขีด) แถวอื่นใส่สตริงว่าง",
                     "issuerBranch": "เฉพาะแถว VAT: รหัสสาขาของผู้ออกใบ 5 หลัก สำนักงานใหญ่ = \"00000\" สาขาที่ 1 = \"00001\" แถวอื่นใส่สตริงว่าง",
                     "baseAmount": 0}]
        }
        """;

        var intro = mode == "image"
            ? "อ่านเอกสารในภาพนี้ (ใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อภาษาไทยหรืออังกฤษ) แล้วดึงข้อมูลออกมา\n"
            : "ข้อความด้านล่างนี้ได้จากการอ่าน OCR เอกสารใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อ อาจมีช่องว่างแทรก" +
              "ระหว่างตัวอักษรไทยผิดปกติ ตัวเลข/ตัวอักษรบางจุดอ่านผิด หรือลำดับคอลัมน์สลับกัน " +
              "ให้ตีความเนื้อหาอย่างชาญฉลาดแล้วดึงข้อมูลออกมาให้ถูกต้องที่สุด\n";

        return intro +
            $"ตอบกลับเป็น JSON ล้วน ๆ ตามโครงสร้างนี้เท่านั้น ห้ามมีข้อความอื่นนอก JSON:\n{fields}\n\n" +
            "กติกา:\n" +
            "- ตัวเลขทุกช่อง (qty, price, amount, subTotal, vatAmount, totalAmount ฯลฯ) ต้องเป็นตัวเลขล้วน " +
            "ไม่มีคอมมา/สัญลักษณ์สกุลเงิน\n" +
            "- วันที่ทุกช่องต้องอยู่ในรูปแบบ YYYY-MM-DD (แปลง พ.ศ. เป็น ค.ศ. โดยลบ 543)\n" +
            "- ช่องไหนหาไม่เจอในเอกสารให้ใส่สตริงว่าง \"\" หรือ 0 ตามชนิดข้อมูล อย่าเดา\n" +
            "- ห้ามใช้ชื่อ/เลขทะเบียนของบริษัทที่เป็น 'ผู้รับเอกสาร' (Megachem (Thailand)) เป็นชื่อคู่ค้าเด็ดขาด\n" +
            "- customerAddress คือที่อยู่ของลูกค้าเอง (สำหรับออกเอกสาร/ใบกำกับภาษี) ส่วน shipToAddress คือที่อยู่ปลายทางจัดส่งสินค้าเท่านั้น " +
            "ถ้าเอกสารมีที่อยู่เดียวไม่ได้แยกที่อยู่จัดส่งไว้ต่างหาก ให้ใส่ในช่อง customerAddress อย่างเดียว อย่าเติมซ้ำใน shipToAddress\n" +
            "- ตรวจสอบผลรวม: subTotal + vatAmount ควรใกล้เคียง totalAmount\n" +
            "- ค่าที่เป็นข้อความทุกช่อง (docType, branch, paymentTerms, desc ของรายการ ฯลฯ) ให้ตอบเป็นภาษาอังกฤษเสมอ " +
            "ถ้าเอกสารเขียนเป็นภาษาไทยหรือภาษาอื่น ให้แปลเป็นอังกฤษ เช่น สำนักงานใหญ่ = Head Office, สาขา 1 = Branch 1, " +
            "30 วัน = 30 days, ใบแจ้งหนี้/ใบวางบิล = Invoice, ใบกำกับภาษี = Tax Invoice, ใบเสร็จรับเงิน = Receipt, " +
            "ภาษีสรรพสามิต = Excise Tax, ภาษีเก็บเพิ่มเพื่อมหาดไทย = Interior Tax, ค่าขนส่ง = Transportation, " +
            "ค่าพิธีการศุลกากร = Customs Clearance Fee, ค่าคลังสินค้า = Storage Charge " +
            "ยกเว้นชื่อบริษัท/ชื่อบุคคล และเลขที่เอกสาร ให้คงไว้ตามที่ปรากฏในเอกสาร" +
            (module is "AP" or "II" ? IiBundleRules : ""); // AP: uploads are read as AP, routed to II afterwards
    }

    // Incoming Invoice (FB60) files are often a whole shipment bundle: several vendor invoices /
    // receipts plus a "FORM SHIPPING EXPENSE" summary sheet (usually the last page). Per Finance:
    // the line items come from that form's cost table, and the withholding tax — which the form's
    // cost table does not include — is taken from the individual invoices and appended as extra
    // lines (extCode "WHT"; the UI seeds those as credit G/L rows).
    private const string IiBundleRules =
        "\n- ไฟล์อาจมีหลายหน้าและรวมเอกสารหลายใบ ให้ดูทุกหน้า" +
        "\n- ถ้ามีหน้า \"FORM SHIPPING EXPENSE\" ให้ lines มาจากตารางในฟอร์มนั้นเท่านั้น: 1 แถวต่อ 1 รายการ, " +
        "desc = คอลัมน์ DESCRIPTION, amount = price = คอลัมน์ AMOUNT, qty = 1, extCode = \"\", " +
        "vendorCode = คอลัมน์ VENDOR ของแถวนั้น (ถ้าว่างหรือเป็น #N/A ให้ใส่ \"\") " +
        "ข้ามแถวที่ AMOUNT เป็น \"-\" หรือ 0 และห้ามใส่แถว TOTAL/Cost variance/MEMO" +
        "\n- (เฉพาะกรณีที่มีหน้า FORM SHIPPING EXPENSE) แถว DO ในตารางให้ใส่เป็น line เฉพาะเมื่อผู้ให้บริการชิปปิ้ง (ช่อง SHIPPING ของฟอร์ม) " +
        "เป็น PROMPT หรือ CHEETAH เท่านั้น ถ้าเป็นรายอื่นห้ามใส่แถว DO แม้ในฟอร์มจะมียอดอยู่" +
        "\n- (เฉพาะกรณีที่มีหน้า FORM SHIPPING EXPENSE) แถวที่เป็นสกุลเงินต่างประเทศ เช่น \"OTHER : Oversea Charge (USD)\" " +
        "ให้แปลงเป็นเงินบาทก่อนใส่ใน amount/price โดยใช้อัตราแลกเปลี่ยนของวันที่ของเข้า (ETA) ตามที่ปรากฏในไฟล์ " +
        "ต้องใช้คอลัมน์ \"อัตราขายถัวเฉลี่ย\" (Selling rate / averaged selling rate) เท่านั้น ห้ามใช้อัตราซื้อ (Buying) หรืออัตรากลาง " +
        "ดูจากหน้าอัตราแลกเปลี่ยนที่แนบมา หรือช่อง DATE OF EX-RATE / 1 USD / 100 JPY / 1 EUR / 1 GBP ในส่วน MEMO ของฟอร์ม " +
        "แล้วเขียน desc เป็น \"Oversea Charge (USD 188.00 x 32.5000)\" เพื่อให้ตรวจสอบย้อนได้ ห้ามใส่ยอดที่ยังเป็นสกุลต่างประเทศ " +
        "ถ้าหาอัตราแลกเปลี่ยนในไฟล์ไม่เจอ ให้คงยอดเดิมไว้และเขียน desc ว่า \"... (USD, no FX rate found)\"" +
        "\n- (เฉพาะกรณีที่มีหน้า FORM SHIPPING EXPENSE) แถว EXCISE FEE ในฟอร์มเป็นยอดรวมของภาษีสรรพสามิตกับภาษีเก็บเพิ่มเพื่อมหาดไทย " +
        "ให้แยกเป็น 2 แถวตามใบเสร็จกรมสรรพสามิตที่แนบมาในไฟล์: desc = \"ภาษีสรรพสามิต\" และ desc = \"ภาษีเก็บเพิ่มเพื่อมหาดไทย\" " +
        "ใช้ vendorCode เดียวกับแถว EXCISE FEE เดิม ห้ามใส่แถวยอดรวมซ้ำอีก" +
        "\n- (เฉพาะกรณีที่มีหน้า FORM SHIPPING EXPENSE) ค่าธรรมเนียมและภาษีในใบเสร็จกรมศุลกากร/ใบขนสินค้าขาเข้าที่แนบมาในไฟล์ " +
        "ให้ใส่เป็นแถวต่อท้าย lines โดยใช้ extCode = \"DUTY\" (ไม่ใช่รายการค่าใช้จ่ายในฟอร์ม) แถวละรายการ ได้แก่ " +
        "อากรขาเข้า (desc = \"Import Duty\"), ภาษีสรรพสามิต (\"Excise Tax\"), ภาษีเก็บเพิ่มเพื่อมหาดไทย (\"Interior Tax\") " +
        "และค่าธรรมเนียมอื่นของกรมศุลกากรถ้ามี ใช้ vendorCode ของกรมศุลกากรตามที่ปรากฏในฟอร์ม (แถวเดียวกับ CUSTOMS FEE) " +
        "ข้ามรายการที่ยอดเป็น 0 ส่วนภาษีมูลค่าเพิ่มของใบนั้นให้ไปอยู่ในแถว VAT ตามปกติ" +
        "\n- (เฉพาะกรณีที่มีหน้า FORM SHIPPING EXPENSE) จากนั้นให้ต่อท้าย lines ด้วยภาษีหัก ณ ที่จ่าย (หัก ณ ที่จ่าย / WITHHOLDING TAX) ทุกรายการที่พบในใบแจ้งหนี้/ใบเสร็จ/ใบกำกับภาษีหน้าอื่นในไฟล์ " +
        "เฉพาะใบที่ออกโดยผู้ขายรายเดียวกับ header.vendorName (รายที่เราจ่ายเงินโดยตรง) เท่านั้น " +
        "ใบของผู้ขายรายอื่นในชุด ตัวแทนชิปปิ้งหักและนำส่งแทนไปแล้ว ห้ามใส่ " +
        "1 แถวต่อ 1 รายการ: extCode = \"WHT\", desc = \"Withholding Tax <อัตรา>% <ชื่อผู้ออกเอกสาร> <เลขที่เอกสาร>\", " +
        "qty = 1, amount = price = ยอดที่หัก (ตัวเลขบวก) และใส่ header.whtAmount = ผลรวมยอดหัก ณ ที่จ่ายทั้งหมด" +
        "\n- (เฉพาะกรณีที่มีหน้า FORM SHIPPING EXPENSE) ต่อท้าย lines ด้วยภาษีมูลค่าเพิ่ม (VAT/ภาษีมูลค่าเพิ่ม 7%) ทุกรายการที่พบในใบแจ้งหนี้/ใบเสร็จ/ใบกำกับภาษีทุกหน้าในไฟล์ " +
        "1 แถวต่อ 1 ใบ: extCode = \"VAT\", desc = \"VAT 7% <ชื่อผู้ออกเอกสาร> <เลขที่เอกสาร>\", qty = 1, amount = price = ยอด VAT ของใบนั้น " +
        "และใส่ vendorCode ของแถว VAT/WHT ให้ตรงกับรหัส vendor ในคอลัมน์ VENDOR ของฟอร์มที่เป็นเจ้าของค่าใช้จ่ายใบนั้น ถ้าจับคู่ไม่ได้ให้ใส่สตริงว่าง " +
        "และระบุ taxKind ของแถวนั้นด้วย: \"INPUT\" ถ้าใบนั้นเป็นใบกำกับภาษี/ใบเสร็จรับเงิน (ภาษีซื้อขอคืนได้งวดนี้) " +
        "หรือ \"DEFERRED\" ถ้าเป็นใบแจ้งหนี้/ใบวางบิลที่ยังไม่ใช่ใบกำกับภาษี (ภาษีซื้อรอเรียกเก็บ) " +
        "และใส่ header.vatAmount = ผลรวม VAT ทุกใบ (ห้ามใส่ยอด VAT ของใบเดียว) " +
        "ต้องไล่ดูทุกหน้าให้ครบทุกใบ รวมใบยอดน้อย เช่น ค่าบริการ 400 บาทที่มี VAT 28 บาท ก็ต้องมีแถวของตัวเอง " +
        "ตรวจทานด้วยว่าแต่ละใบ VAT ≈ 7% ของฐานภาษีในใบนั้น และจำนวนแถว VAT เท่ากับจำนวนใบกำกับภาษี/ใบเสร็จที่มี VAT ในไฟล์ " +
        "ยกเว้น VAT จากใบเสร็จของสถานกงสุล/กงสุล (Consular) ห้ามใส่เป็นแถว VAT และห้ามรวมใน header.vatAmount " +
        "\n- ทุกแถว VAT ต้องกรอกข้อมูลของใบกำกับภาษีใบนั้นให้ครบด้วย เพราะต้องนำไปออกรายงานภาษีซื้อ: " +
        "issuerName = ชื่อผู้ออกใบกำกับภาษี (ผู้ขาย ไม่ใช่ MEGACHEM/GREEN LEAF ที่เป็นผู้ซื้อ), " +
        "issuerTaxId = เลขประจำตัวผู้เสียภาษี 13 หลักของผู้ออกใบ ตัวเลขล้วนไม่ต้องมีขีดหรือเว้นวรรค, " +
        "issuerBranch = รหัสสาขา 5 หลัก (สำนักงานใหญ่/HEAD OFFICE = \"00000\" สาขาที่ 1 = \"00001\") ถ้าใบไม่ระบุให้ใส่ \"00000\", " +
        "taxDocNo = เลขที่ใบกำกับภาษี, taxDocDate = วันที่ในใบกำกับภาษี YYYY-MM-DD, " +
        "baseAmount = มูลค่าสินค้า/บริการก่อนภาษีของใบนั้น (ต้อง ≈ ยอด VAT หารด้วย 0.07) " +
        "ข้อมูลเหล่านี้ให้อ่านจากหัวของใบกำกับภาษีใบนั้นโดยตรง ห้ามคัดลอกของใบอื่นหรือของบริษัทผู้ซื้อ" +
        "ส่วนภาษีมูลค่าเพิ่มในใบเสร็จกรมศุลกากร (ภาษีซื้อขาเข้า) ให้ใส่เป็นแถว VAT ด้วย โดยใช้ vendorCode ของกรมศุลกากรตามที่ปรากฏในฟอร์ม " +
        "(แถวเดียวกับ CUSTOMS FEE / EXCISE FEE) เพื่อให้ไปอยู่กับเอกสาร MIRO ของ vendor รายนั้น";


    // Parses a model's raw text response (expected to contain one JSON object, possibly with
    // surrounding prose despite instructions not to) into a ParsedDocument, or null if no JSON
    // object could be found/parsed.
    public static ParsedDocument? ParseResponse(string raw, string module, string provider, double confidence, string rawTextForRecord)
    {
        var m = Regex.Match(raw, @"\{.*\}", RegexOptions.Singleline);
        if (!m.Success) return null;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(m.Value); }
        catch (JsonException) { return null; }

        var h = HeaderParser.BlankHeader(module);
        if (parsed.RootElement.TryGetProperty("header", out var hEl) && hEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in hEl.EnumerateObject())
                if (h.ContainsKey(prop.Name))
                    h[prop.Name] = JsonElementToObject(prop.Value);
        }

        var lines = new List<LineItem>();
        if (parsed.RootElement.TryGetProperty("lines", out var lEl) && lEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var ln in lEl.EnumerateArray().Take(60))
            {
                lines.Add(new LineItem
                {
                    ExtCode = GetStr(ln, "extCode"), Desc = GetStr(ln, "desc"),
                    Qty = GetNum(ln, "qty"), Uom = GetStr(ln, "uom") is { Length: > 0 } u ? u : "EA",
                    VendorCode = GetStr(ln, "vendorCode") is var vc && vc != "#N/A" ? vc.Trim() : "",
                    TaxKind = NormTaxKind(GetStr(ln, "taxKind")),
                    TaxDocNo = GetStr(ln, "taxDocNo").Trim(),
                    TaxDocDate = GetStr(ln, "taxDocDate").Trim(),
                    IssuerName = GetStr(ln, "issuerName").Trim(),
                    IssuerTaxId = DigitsOnly().Replace(GetStr(ln, "issuerTaxId"), ""),
                    IssuerBranch = NormBranch(GetStr(ln, "issuerBranch")),
                    BaseAmount = GetNum(ln, "baseAmount"),
                    Price = GetNum(ln, "price"), Amount = GetNum(ln, "amount"),
                    DueDate = GetStr(ln, "deliveryDate"),
                    ItemNote = GetStr(ln, "itemNote"),
                });
            }
        }

        // When the read appended withholding-tax rows (extCode "WHT", shipping-bundle rule), the
        // header's whtAmount must be exactly their sum — the model's own total was read off a
        // different page and did not match the rows it returned (112.88 vs 63.00 + 9.17).
        // DO (delivery order) fee is only charged to us by PROMPT and CHEETAH; with any other
        // shipping agent the row on the form is not ours to record, so it is dropped even when it
        // carries an amount.
        var shipper = h.TryGetValue("vendorName", out var vName) ? vName?.ToString() ?? "" : "";
        if (!DoChargingShipper().IsMatch(shipper))
            lines.RemoveAll(l => DoFeeDesc().IsMatch(l.Desc ?? ""));

        // The FORM's EXCISE FEE is one figure but two taxes: excise plus the interior ("มหาดไทย")
        // tax, which Thai law sets at 10% of the excise. When the read returns it as a single row,
        // split it so each tax is its own line (98,656.00 + 9,865.60 = 108,521.60). If the read
        // already produced an interior-tax row it did the split itself — splitting again would
        // halve the excise a second time, so this only runs when there is no such row.
        var alreadySplit = lines.Any(l => InteriorTaxDesc().IsMatch(l.Desc ?? ""));
        for (var i = alreadySplit ? -1 : lines.Count - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (IsWht(l) || string.Equals(l.ExtCode, "VAT", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ExciseDesc().IsMatch(l.Desc ?? "") || InteriorTaxDesc().IsMatch(l.Desc ?? "")) continue;
            if (l.Amount <= 0) continue;
            var excise = Math.Round(l.Amount / 1.1, 2);
            var interior = Math.Round(l.Amount - excise, 2);
            if (interior <= 0) continue;
            l.Desc = "Excise Tax";
            l.Amount = excise;
            l.Price = excise;
            lines.Insert(i + 1, new LineItem
            {
                ExtCode = l.ExtCode, Desc = "Interior Tax", Qty = 1, Uom = l.Uom,
                Price = interior, Amount = interior, VendorCode = l.VendorCode,
            });
        }

        // Withholding tax is ours only for the vendor we pay directly (the document's own vendor).
        // In a shipping bundle the agent's sub-vendor invoices carry WHT the agent already withheld
        // and remitted, so those rows must not be posted again here.
        var vendorWords = VendorWords(h.TryGetValue("vendorName", out var vn) ? vn?.ToString() ?? "" : "");
        if (vendorWords.Count > 0
            && lines.Any(l => IsWht(l) && vendorWords.Any(w => l.Desc.Contains(w, StringComparison.OrdinalIgnoreCase))))
        {
            lines.RemoveAll(l => IsWht(l) && !vendorWords.Any(w => l.Desc.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        var whtRows = lines.Where(IsWht).ToList();
        if (whtRows.Count > 0) h["whtAmount"] = Math.Round(whtRows.Sum(l => l.Amount), 2);

        // Same for VAT: a bundle's VAT is spread over several invoices, so the header total is the
        // sum of the per-invoice "VAT" rows, not whichever single page the model happened to read.
        // Per Finance: consular-fee VAT is never ours to claim, so it is dropped here as well as in
        // the prompt. Customs import VAT is different — it IS claimable input tax and belongs to the
        // Customs Department's own MIRO document, so it stays (tagged with that vendor's code).
        lines.RemoveAll(l => string.Equals(l.ExtCode, "VAT", StringComparison.OrdinalIgnoreCase)
                             && GovernmentVatDesc().IsMatch(l.Desc ?? ""));

        var vatRows = lines.Where(l => string.Equals(l.ExtCode, "VAT", StringComparison.OrdinalIgnoreCase)).ToList();
        if (vatRows.Count > 0) h["vatAmount"] = Math.Round(vatRows.Sum(l => l.Amount), 2);

        // Several FORMs (one per PO) list the same cost rows, and Finance wants one line per cost:
        // merge rows with the same description and vendor by adding the amounts. Tax rows stay per
        // invoice — they are reported individually.
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.ExtCode is "WHT" or "VAT" or "DUTY") continue;
            for (var j = lines.Count - 1; j > i; j--)
            {
                var other = lines[j];
                if (other.ExtCode is "WHT" or "VAT" or "DUTY") continue;
                if (!string.Equals(other.Desc?.Trim(), l.Desc?.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(other.VendorCode?.Trim(), l.VendorCode?.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                l.Amount = Math.Round(l.Amount + other.Amount, 2);
                l.Price = l.Amount;
                l.Qty = 1;
                lines.RemoveAt(j);
            }
        }

        // A bundle that carries customs charges: the cost total is the FORM's rows only, while duty
        // and VAT ride in the Tax tab — so the header is rebuilt to match what the screen shows
        // (subTotal = costs, totalAmount = costs + duty + VAT = what this vendor is actually paid).
        var dutyRows = lines.Where(l => string.Equals(l.ExtCode, "DUTY", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dutyRows.Count > 0)
        {
            var costs = lines.Where(l => l.ExtCode is not ("WHT" or "VAT" or "DUTY")).Sum(l => l.Amount);
            var duty = dutyRows.Sum(l => l.Amount);
            var vat = vatRows.Sum(l => l.Amount);
            h["subTotal"] = Math.Round(costs, 2);
            h["totalAmount"] = Math.Round(costs + duty + vat, 2);
        }

        var text = rawTextForRecord.Length > 20000 ? rawTextForRecord[..20000] : rawTextForRecord;
        return new ParsedDocument { Header = h, Lines = lines, Confidence = confidence, Provider = provider, RawText = text };
    }

    // Shipping agents that bill us the DO fee, and the form row that carries it.
    [GeneratedRegex(@"prompt|cheetah|พรอมท์|พร้อมท์|ชีต้าร์|ชีตาร์|ธีตาร์", RegexOptions.IgnoreCase)]
    private static partial Regex DoChargingShipper();

    [GeneratedRegex(@"^\s*(do|d/o)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoFeeDesc();

    [GeneratedRegex(@"excise|สรรพสามิต", RegexOptions.IgnoreCase)]
    private static partial Regex ExciseDesc();

    [GeneratedRegex(@"มหาดไทย|interior", RegexOptions.IgnoreCase)]
    private static partial Regex InteriorTaxDesc();

    private static bool IsWht(LineItem l) => string.Equals(l.ExtCode, "WHT", StringComparison.OrdinalIgnoreCase);

    // Distinctive words of a vendor name, used to tell "this WHT is from our own vendor's invoice"
    // from a sub-vendor's. Legal-form words are dropped because every Thai company name has them.
    private static List<string> VendorWords(string vendorName)
    {
        if (string.IsNullOrWhiteSpace(vendorName)) return [];
        var cleaned = LegalForm().Replace(vendorName, " ");
        return cleaned.Split([' ', '\t', '.', ',', '(', ')', '-'], StringSplitOptions.RemoveEmptyEntries)
                      .Where(w => w.Length >= 3)
                      .ToList();
    }

    [GeneratedRegex(@"บริษัท|จำกัด|มหาชน|ห้างหุ้นส่วน|หจก\.?|บจก\.?|company|limited|public|co\.|ltd\.?|corp\.?|inc\.?",
        RegexOptions.IgnoreCase)]
    private static partial Regex LegalForm();

    // Issuers whose VAT stays out of the invoice: consulates (consular fees carry no claimable VAT).
    [GeneratedRegex(@"กงสุล|consul", RegexOptions.IgnoreCase)]
    private static partial Regex GovernmentVatDesc();

    // Tax IDs are printed with dashes and spaces ("0-1055-43000-12-3"); the input-VAT file wants
    // the 13 digits only.
    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();

    // Branch in the input-VAT file is 5 digits, head office = 00000. Accepts "HEAD OFFICE",
    // "สำนักงานใหญ่", "0000", "1" and so on.
    private static string NormBranch(string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "00000";
        if (v.Contains("สำนักงานใหญ่") || v.Contains("head", StringComparison.OrdinalIgnoreCase)) return "00000";
        var digits = DigitsOnly().Replace(v, "");
        if (digits.Length == 0) return "00000";
        return digits.Length >= 5 ? digits[^5..] : digits.PadLeft(5, '0');
    }

    // Only the two values the UI and the input-VAT report understand; anything else is dropped.
    private static string NormTaxKind(string value) => value.Trim().ToUpperInvariant() switch
    {
        "INPUT" => "INPUT",
        "DEFERRED" => "DEFERRED",
        _ => "",
    };

    private static string GetStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double GetNum(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => TextHelpers.F(v.GetString()),
            _ => 0,
        };
    }
    private static object? JsonElementToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
