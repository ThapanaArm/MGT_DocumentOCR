import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Line,
  LineChart,
  Pie,
  PieChart,
  PolarAngleAxis,
  RadialBar,
  RadialBarChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { useThemeColors } from '../../hooks/useThemeColors';
import { moduleLabel } from '../../utils/format';
import type { ByModuleRow, OcrDailyRow } from '../../api/dashboard';

/* Recharts replacements for the hand-drawn SVG charts in app.js
   (lineChart2 / donutChart / hBarChart / radialGauge). Colors come from
   the live CSS theme variables via useThemeColors so light/dark match. */

export interface DonutSeg {
  label: string;
  value: number;
  color: string;
}

export function DocTrendChart({ data }: { data: OcrDailyRow[] }) {
  const c = useThemeColors();
  return (
    <ResponsiveContainer width="100%" height={220}>
      <LineChart data={data} margin={{ top: 8, right: 12, left: -18, bottom: 0 }}>
        <CartesianGrid stroke={c.line} vertical={false} />
        <XAxis
          dataKey="date"
          tickFormatter={(d: string) => (d || '').slice(5)}
          tick={{ fontSize: 10, fill: c.muted }}
          stroke={c.line}
          interval="preserveStartEnd"
          minTickGap={24}
        />
        <YAxis tick={{ fontSize: 10, fill: c.muted }} stroke={c.line} allowDecimals={false} width={34} />
        <Tooltip
          contentStyle={{
            background: c.card,
            border: `1px solid ${c.line}`,
            borderRadius: 8,
            fontSize: 12,
          }}
        />
        <Line
          type="monotone"
          dataKey="docCount"
          name="Documents"
          stroke={c.brand}
          strokeWidth={2.5}
          dot={{ r: 3 }}
        />
        <Line
          type="monotone"
          dataKey="okCount"
          name="OCR Successful"
          stroke={c.info}
          strokeWidth={2.5}
          strokeDasharray="6 4"
          dot={{ r: 3 }}
        />
      </LineChart>
    </ResponsiveContainer>
  );
}

export function StatusDonut({
  segments,
  centerValue,
  centerLabel,
}: {
  segments: DonutSeg[];
  centerValue: string;
  centerLabel: string;
}) {
  const c = useThemeColors();
  const data = segments.length ? segments : [{ label: '—', value: 1, color: c.line }];
  return (
    <div style={{ position: 'relative', width: 180, height: 180, margin: '0 auto' }}>
      <ResponsiveContainer width="100%" height="100%">
        <PieChart>
          <Pie
            data={data}
            dataKey="value"
            nameKey="label"
            cx="50%"
            cy="50%"
            innerRadius={57}
            outerRadius={80}
            startAngle={90}
            endAngle={-270}
            stroke="none"
          >
            {data.map((s, i) => (
              <Cell key={i} fill={s.color} />
            ))}
          </Pie>
          <Tooltip
            contentStyle={{
              background: c.card,
              border: `1px solid ${c.line}`,
              borderRadius: 8,
              fontSize: 12,
            }}
          />
        </PieChart>
      </ResponsiveContainer>
      <div
        style={{
          position: 'absolute',
          inset: 0,
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          pointerEvents: 'none',
        }}
      >
        <div style={{ fontSize: 22, fontWeight: 700, color: c.text }}>{centerValue}</div>
        <div style={{ fontSize: 11, color: c.muted }}>{centerLabel}</div>
      </div>
    </div>
  );
}

export function ModuleBarChart({ data }: { data: ByModuleRow[] }) {
  const c = useThemeColors();
  const rows = data.map((m) => ({ label: moduleLabel(m.module), value: m.count, module: m.module }));
  return (
    <ResponsiveContainer width="100%" height={Math.max(120, rows.length * 46)}>
      <BarChart data={rows} layout="vertical" margin={{ top: 4, right: 16, left: 8, bottom: 4 }}>
        <XAxis type="number" hide allowDecimals={false} />
        <YAxis
          type="category"
          dataKey="label"
          width={110}
          tick={{ fontSize: 13, fill: c.text }}
          stroke={c.line}
        />
        <Tooltip
          cursor={{ fill: c.line, opacity: 0.3 }}
          contentStyle={{
            background: c.card,
            border: `1px solid ${c.line}`,
            borderRadius: 8,
            fontSize: 12,
          }}
        />
        <Bar dataKey="value" name="Documents" fill={c.brand} radius={[0, 100, 100, 0]} barSize={18} />
      </BarChart>
    </ResponsiveContainer>
  );
}

export function OcrGauge({ pct, label }: { pct: number; label: string }) {
  const c = useThemeColors();
  const value = Math.max(0, Math.min(100, pct));
  const data = [{ name: label, value }];
  return (
    <div style={{ position: 'relative', width: 150, height: 150, margin: '0 auto' }}>
      <ResponsiveContainer width="100%" height="100%">
        <RadialBarChart
          data={data}
          innerRadius="72%"
          outerRadius="100%"
          startAngle={90}
          endAngle={-270}
          barSize={15}
        >
          <PolarAngleAxis type="number" domain={[0, 100]} tick={false} />
          <RadialBar dataKey="value" cornerRadius={100} fill={c.brand} background={{ fill: c.line }} />
        </RadialBarChart>
      </ResponsiveContainer>
      <div
        style={{
          position: 'absolute',
          inset: 0,
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          pointerEvents: 'none',
        }}
      >
        <div style={{ fontSize: 24, fontWeight: 700, color: c.text }}>{pct}%</div>
        <div style={{ fontSize: 10, color: c.muted }}>{label}</div>
      </div>
    </div>
  );
}
