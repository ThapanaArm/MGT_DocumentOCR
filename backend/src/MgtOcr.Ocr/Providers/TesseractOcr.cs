using System.Diagnostics;
using System.Text;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr.Providers;

// tesseract_text(): OCR (Thai+English) — shells out to the existing local tesseract.exe rather
// than a NuGet wrapper, so the underlying engine/version/tessdata is byte-identical to what
// pytesseract used on the Python side (pytesseract itself is just a subprocess wrapper around
// this same binary) — every regex in LineParser/HeaderParser was tuned against this engine's
// specific OCR-noise quirks (e.g. inter-character space insertion in Thai), so using a different
// OCR engine here would silently regress accuracy even with the parsing logic ported exactly.
public static class TesseractOcr
{
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp" };

    public static async Task<string> ExtractTextAsync(string path, AppConfig config)
    {
        var exe = string.IsNullOrEmpty(config.TesseractCmd) ? FindOnPath("tesseract") : config.TesseractCmd;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return "";
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf")
            {
                var pages = PdfRasterizer.RenderPagesToPng(path, maxPages: 5, dpi: 300); // cap at 5 pages, matches Python
                var parts = new List<string>();
                foreach (var png in pages)
                {
                    var tmp = Path.Combine(Path.GetTempPath(), $"mgtocr_{Guid.NewGuid():N}.png");
                    await File.WriteAllBytesAsync(tmp, png);
                    try { parts.Add(await RunTesseractAsync(exe, tmp, config)); }
                    finally { TryDelete(tmp); }
                }
                return string.Join("\n", parts);
            }
            return await RunTesseractAsync(exe, path, config);
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> RunTesseractAsync(string exe, string imagePath, AppConfig config)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            ArgumentList = { imagePath, "stdout", "-l", "tha+eng" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(config.TessdataPrefix))
            psi.EnvironmentVariables["TESSDATA_PREFIX"] = config.TessdataPrefix;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start tesseract");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }

    private static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : new[] { "" };
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, exeName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
