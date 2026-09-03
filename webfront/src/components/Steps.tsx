/* Ports stepsHtml() — the 3-step progress header. */
const STEPS: [string, string][] = [
  ['Import & Read Document', 'OCR → Header / Detail'],
  ['Data Mapping', 'Validate against Master Data'],
  ['Submit to SAP', 'Create document in S/4HANA'],
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
