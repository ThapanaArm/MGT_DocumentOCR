export type PaymentTerm = { code: string; description: string };

export function paymentTermOptions(rows: Array<{ Code?: unknown; Text?: unknown }> = []): PaymentTerm[] {
  const terms = new Map<string, PaymentTerm>();
  for (const row of rows) {
    const code = String(row.Code ?? '').trim().toUpperCase();
    if (!/^[A-Z0-9]{1,4}$/.test(code) || terms.has(code)) continue;
    terms.set(code, { code, description: String(row.Text ?? '').trim() });
  }
  return [...terms.values()];
}

export function expectedPaymentDueDate(baseline: string, description: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(baseline)) return '';
  const date = new Date(`${baseline}T00:00:00Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== baseline) return '';
  const term = description.trim();
  const days = /^(?:within\s+)?(\d{1,3})\s*days?(?:\s+(?:net|due\s+net))?$/i.exec(term);
  if (!days && !/^(?:pay immediately|payable immediately due net)$/i.test(term)) return '';
  date.setUTCDate(date.getUTCDate() + Number(days?.[1] ?? 0));
  return date.toISOString().slice(0, 10);
}

export function paymentDateForDisplay(value: string): string {
  const parts = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return parts ? `${parts[3]}.${parts[2]}.${parts[1]}` : '—';
}
