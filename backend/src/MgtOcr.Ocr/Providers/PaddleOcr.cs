using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr.Providers;

// PaddleOCR (local, free) — shells out to the PaddleOCR-Standalone executable
// (https://github.com/timminator/PaddleOCR-Standalone), same subprocess pattern as TesseractOcr, so
// no Python is needed on the server.
//
// PaddleOCR only returns raw text boxes (text + position) — it does not know which box is the PO No.
// or the customer. So the result is used two ways (see OcrEngine):
//   "paddle"        → text goes to the local regex parsers (HeaderParser/LineParser), free
//   "paddle_gemini" → text goes to Gemini (text only, no images) to structure as JSON, cheap
//
// Thai: PP-OCRv6 does not cover Thai; "--lang th" selects PP-OCRv5 th_PP-OCRv5_mobile_rec. The models
// are downloaded on the FIRST run (needs internet once) into %USERPROFILE%\.paddlex, or next to the exe
// when the standalone folder has portable_mode.txt.
public static class PaddleOcr
{
    public static string ResolveExe(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.PaddleOcrCmd)) return config.PaddleOcrCmd;
        string[] candidates =
        [
            @"C:\Program Files\PaddleOCR-CPU\paddleocr.exe",
            @"C:\Program Files\PaddleOCR-GPU\paddleocr.exe",
        ];
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    public static bool IsReady(AppConfig config) => ResolveExe(config) is { Length: > 0 } exe && File.Exists(exe);

    /// <summary>Reads every page with one PaddleOCR process (the model load is the slow part, so all
    /// pages go in as one input folder) and rebuilds reading-order text lines from the box positions.</summary>
    public static async Task<(string Text, string Error)> ExtractTextAsync(string path, AppConfig config)
    {
        var exe = ResolveExe(config);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return ("", "PaddleOCR executable not found — install PaddleOCR-Standalone and set Ocr:PaddleOcrCmd in appsettings.json (e.g. C:\\Program Files\\PaddleOCR-CPU\\paddleocr.exe)");

        var work = Path.Combine(Path.GetTempPath(), $"mgtocr_paddle_{Guid.NewGuid():N}");
        var inDir = Path.Combine(work, "in");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf")
            {
                var pages = PdfRasterizer.RenderPagesToPng(path, maxPages: Math.Max(1, config.PaddleOcrMaxPages), dpi: 200);
                if (pages.Count == 0) return ("", $"Could not rasterize '{Path.GetFileName(path)}' to images for PaddleOCR");
                for (var i = 0; i < pages.Count; i++)
                    await File.WriteAllBytesAsync(Path.Combine(inDir, $"page_{i + 1:D3}.png"), pages[i]);
            }
            else if (TesseractOcr.ImageExt.Contains(ext))
            {
                File.Copy(path, Path.Combine(inDir, "page_001" + ext));
            }
            else
            {
                return ("", "PaddleOCR reads PDF or image files only");
            }

            var (exit, stderr) = await RunAsync(exe, inDir, outDir, config);

            var jsons = Directory.GetFiles(outDir, "*_res.json", SearchOption.AllDirectories)
                                 .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
            if (jsons.Count == 0)
            {
                var why = exit == int.MinValue
                    ? $"PaddleOCR did not finish within {config.PaddleOcrTimeoutSec}s (the first run downloads models and is slow)"
                    : $"PaddleOCR produced no result (exit code {exit})";
                return ("", why + (stderr.Length > 0 ? ": " + Tail(stderr, 500) : ""));
            }

            var sb = new StringBuilder();
            for (var p = 0; p < jsons.Count; p++)
            {
                if (jsons.Count > 1) sb.AppendLine($"--- Page {p + 1} ---");
                sb.AppendLine(ToReadingOrder(await File.ReadAllTextAsync(jsons[p], Encoding.UTF8)));
            }
            var text = sb.ToString();
            return string.IsNullOrWhiteSpace(text) ? ("", "PaddleOCR found no text in the document") : (text, "");
        }
        catch (Exception ex)
        {
            return ("", $"PaddleOCR failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Returns (exit code, stderr tail). Exit code int.MinValue = timed out and killed.</summary>
    private static async Task<(int Exit, string StdErr)> RunAsync(string exe, string inDir, string outDir, AppConfig config)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        foreach (var a in new[]
        {
            "ocr", "-i", inDir, "--save_path", outDir,
            "--lang", string.IsNullOrWhiteSpace(config.PaddleOcrLang) ? "th" : config.PaddleOcrLang,
            "--use_doc_orientation_classify", "False",
            "--use_doc_unwarping", "False",
            "--use_textline_orientation", "False",
        }) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(config.PaddleOcrDevice)) { psi.ArgumentList.Add("--device"); psi.ArgumentList.Add(config.PaddleOcrDevice); }
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start PaddleOCR");
        // Drain both streams while waiting — PaddleOCR logs a lot, and an unread pipe would block it.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, config.PaddleOcrTimeoutSec)));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (int.MinValue, "");
        }
        await Task.WhenAll(outTask, errTask);
        var err = errTask.Result;
        if (proc.ExitCode != 0) Console.WriteLine($"[PaddleOCR] exit {proc.ExitCode}: {Tail(err, 1500)}");
        return (proc.ExitCode, proc.ExitCode == 0 ? "" : err);
    }

    private record Box(string Text, double X1, double Y1, double X2, double Y2)
    {
        public double Yc => (Y1 + Y2) / 2;
        public double H => Math.Max(1, Y2 - Y1);
    }

    /// <summary>PaddleOCR returns one box per text fragment. The parsers (and Gemini) need rows, so
    /// boxes whose vertical centres are close are put on the same line, left to right; a wide gap
    /// between boxes becomes a run of spaces so table columns stay apart.</summary>
    private static string ToReadingOrder(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("rec_texts", out var texts) || texts.ValueKind != JsonValueKind.Array) return "";
        root.TryGetProperty("rec_boxes", out var boxesEl);
        root.TryGetProperty("rec_polys", out var polysEl);

        var boxes = new List<Box>();
        var i = 0;
        foreach (var t in texts.EnumerateArray())
        {
            var s = t.GetString() ?? "";
            double x1 = 0, y1 = i * 20, x2 = 0, y2 = i * 20 + 10; // no geometry → keep given order
            if (boxesEl.ValueKind == JsonValueKind.Array && i < boxesEl.GetArrayLength())
            {
                var b = boxesEl[i];
                if (b.ValueKind == JsonValueKind.Array && b.GetArrayLength() >= 4)
                { x1 = b[0].GetDouble(); y1 = b[1].GetDouble(); x2 = b[2].GetDouble(); y2 = b[3].GetDouble(); }
            }
            else if (polysEl.ValueKind == JsonValueKind.Array && i < polysEl.GetArrayLength())
            {
                var pts = polysEl[i].EnumerateArray().Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToList();
                if (pts.Count > 0) { x1 = pts.Min(p => p.X); x2 = pts.Max(p => p.X); y1 = pts.Min(p => p.Y); y2 = pts.Max(p => p.Y); }
            }
            if (!string.IsNullOrWhiteSpace(s)) boxes.Add(new Box(s.Trim(), x1, y1, x2, y2));
            i++;
        }
        if (boxes.Count == 0) return "";

        var rows = new List<List<Box>>();
        foreach (var b in boxes.OrderBy(b => b.Yc))
        {
            var row = rows.LastOrDefault();
            if (row != null && Math.Abs(b.Yc - row.Average(r => r.Yc)) <= 0.5 * Math.Min(b.H, row.Average(r => r.H)))
                row.Add(b);
            else
                rows.Add([b]);
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(b => b.X1).ToList();
            var line = new StringBuilder(ordered[0].Text);
            for (var k = 1; k < ordered.Count; k++)
            {
                var gap = ordered[k].X1 - ordered[k - 1].X2;
                var charW = (ordered[k - 1].X2 - ordered[k - 1].X1) / Math.Max(1, ordered[k - 1].Text.Length);
                line.Append(gap > 2 * Math.Max(4, charW) ? "    " : " ");
                line.Append(ordered[k].Text);
            }
            sb.AppendLine(line.ToString());
        }
        return sb.ToString();
    }

    private static string Tail(string s, int n) => s.Length <= n ? s.Trim() : "…" + s[^n..].Trim();
}
