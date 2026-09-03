/* Ports stepsHtml() — the 3-step progress header. */
const STEPS: [string, string][] = [
  ['นำเข้า & อ่านเอกสาร', 'OCR → Header / Detail'],
  ['Mapping ข้อมูล', 'ตรวจกับ Master Data'],
  ['ส่งเข้า SAP', 'สร้างเอกสารใน S/4HANA'],
];

export default function Steps({ current }: { current: number }) {
  return (
    <div className="steps">
      {STEPS.map((s, i) => {
        const n = i + 1;
        const cls = n < current ? 'done' : n === current ? 'on' : '';
        return (
          <div className={'step ' + cls} key={n}>
            <div className="n">{n < current ? '✓' : n}</div>
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
