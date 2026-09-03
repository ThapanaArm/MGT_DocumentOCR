import { useEffect, useState } from 'react';
import { useAppState } from '../state/AppState';

/* =====================================================================
   Recharts needs concrete color strings, but the design lives in CSS
   custom properties (--brand, --info, …) that flip with the theme. This
   hook resolves those variables to their current computed values and
   recomputes whenever the theme mode changes.
   ===================================================================== */

const VARS = [
  'brand',
  'info',
  'orange',
  'red',
  'green',
  'muted',
  'line',
  'text',
  'card',
] as const;

export type ThemeColors = Record<(typeof VARS)[number], string>;

function read(): ThemeColors {
  const cs = getComputedStyle(document.documentElement);
  const out = {} as ThemeColors;
  for (const v of VARS) {
    out[v] = cs.getPropertyValue('--' + v).trim() || '#888';
  }
  return out;
}

export function useThemeColors(): ThemeColors {
  const { themeMode } = useAppState();
  const [colors, setColors] = useState<ThemeColors>(() => read());
  useEffect(() => {
    // Read after the data-theme attribute has been applied for this mode.
    setColors(read());
  }, [themeMode]);
  return colors;
}
