/* Ports stepsHtml() — the 3-step progress header.
   Step 3's label follows the actual destination (F01: the stepper used to say "Submit to SAP"
   even for MGT documents, which are reviewed and sent to Zoho CRM in Step 3, not SAP — the same
   isMgt flag DocumentPage already uses to pick the Step 3 button/action bar wording). */
const STEPS_HEAD: [string, string][] = [
  ['อ่านเอกสาร', 'ระบบดึงข้อมูลจากเอกสาร'],
  ['จับคู่ข้อมูล', 'ตรวจสอบกับข้อมูลหลัก'],
];
const STEP3_SAP: [string, string] = ['ตรวจและส่ง SAP', 'ตรวจข้อมูลก่อนสร้างรายการ'];
const STEP3_ZOHO: [string, string] = ['ตรวจและส่ง Zoho', 'ตรวจ Sales Order ก่อนส่ง'];

export default function Steps({ current, isMgt }: { current: number; isMgt?: boolean }) {
  const STEPS: [string, string][] = [...STEPS_HEAD, isMgt ? STEP3_ZOHO : STEP3_SAP];
  return (
    <div className="steps">
      {STEPS.map((s, i) => {
        const n = i + 1;
        const cls = n < current ? 'done' : n === current ? 'on' : '';
        return (
          <div className={'step ' + cls} key={n}>
            <div className="n">{n < current ? <i className="fa-solid fa-check" /> : n}</div>
            <div className="t">
              <b>{s[0]}</b>
              {s[1]}
            </div>
          </div>
        );
      })}
    </div>
  );
}
