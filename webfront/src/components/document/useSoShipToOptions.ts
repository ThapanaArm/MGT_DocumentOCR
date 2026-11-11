import { useEffect, useState } from 'react';
import { findSapPartnerFunctions } from '../../api/sap';
import { getZohoAccountShipTosByCode } from '../../api/zoho';
import { shipToValue, type GroupShipToOption } from './SoGroupPanel';

/* Ship-to choices for one Sales Order's SO Groups (Split board + Step 3 group table).
   GLC/SAP: the SAP ship-to parties (partner function SH) of the matched sold-to -- the same lookup
   as the Ship-to card's "search SAP"; stored as { code, name, address }.
   MGT/Zoho: every Zoho ship-to on file for the matched Account -- the same list the Ship-to card
   offers; stored as the ZohoShipToInfo fields. Loaded only while `enabled`. */
export function useSoShipToOptions({
  enabled,
  isMgt,
  customerCode,
  soldToSapCode,
  salesOrg,
}: {
  enabled: boolean;
  isMgt: boolean;
  /** MGT: the matched customer (Zoho Account) code. */
  customerCode: string;
  /** GLC: the matched sold-to's SAP Business Partner code. */
  soldToSapCode: string;
  salesOrg: string;
}) {
  const [options, setOptions] = useState<GroupShipToOption[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const key = isMgt ? customerCode.trim() : soldToSapCode.trim();

  useEffect(() => {
    if (!enabled || !key) {
      setOptions([]);
      setError(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    const load = async (): Promise<GroupShipToOption[]> => {
      const seen = new Set<string>();
      const out: GroupShipToOption[] = [];
      if (isMgt) {
        const r = await getZohoAccountShipTosByCode(key);
        for (const info of r?.shipTos || []) {
          const data = info as unknown as Record<string, unknown>;
          const value = shipToValue(data);
          if (seen.has(value)) continue;
          seen.add(value);
          out.push({
            value,
            label: [info.code, info.address].filter((x) => String(x || '').trim()).join(' — ') || '(no address)',
            data,
          });
        }
      } else {
        const r = await findSapPartnerFunctions(key, salesOrg, 'SH');
        for (const link of r?.results || []) {
          const code = String(link.partnerCustomer || '').trim();
          if (!code) continue;
          const p = link.partner;
          const name = String(p?.businessPartnerName || '').trim();
          const address = [p?.addressStreet, p?.addressDistrict, p?.addressCity, p?.addressPostalCode]
            .map((x) => String(x || '').trim())
            .filter(Boolean)
            .join(' ');
          const data = { code, name, address };
          const value = shipToValue(data);
          if (seen.has(value)) continue;
          seen.add(value);
          out.push({ value, label: [code, name, address].filter(Boolean).join(' — '), data });
        }
      }
      return out;
    };
    load()
      .then((opts) => { if (!cancelled) setOptions(opts); })
      .catch((e) => {
        if (!cancelled) {
          setOptions([]);
          setError(e?.message || (isMgt ? 'Zoho CRM lookup failed' : 'SAP lookup failed'));
        }
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [enabled, isMgt, key, salesOrg]);

  return { options, loading, error };
}
