using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Checks the store screenshot tool's promises that hold without a Game view: a capture comes back as plain RGB — no
// alpha channel, which App Store Connect refuses — with every pixel the colour it was captured; and the two sizes of one
// shot share a number, one past whatever is already in the folder. The capture itself needs a Game view, so it is
// checked by using it: Docs/App-Store-Submission.md.
//
// Usage: Tools > RoboSim > Validate > Validate Store Screenshots, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod StoreScreenshotValidation.RunBatchValidate
public static class StoreScreenshotValidation
{
    private const string Title = "Validate Store Screenshots";

    [MenuItem("Tools/RoboSim/Validate/Validate Store Screenshots", false, 57)]
    private static void RunInteractive() => ValidationUtil.RunInteractive(Title, Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch(Title, Run);

    private static string Run()
    {
        var checks = new ValidationUtil.Checks();
        AlphaComesOut(checks);
        ShotNumbers(checks);

        if (checks.Failures.Count == 0) return $"{Title}: PASSED ({checks.Count} checks).";
        var report = new StringBuilder($"{checks.Failures.Count} of {checks.Count} checks failed:");
        foreach (string failure in checks.Failures) report.Append("\n  - ").Append(failure);
        throw new InvalidOperationException(report.ToString());
    }

    // A made-up capture with alpha running from clear to nearly opaque, in colours that would show any mix-up:
    // premultiplying by the alpha, red and blue swapped, a row dropped.
    private static void AlphaComesOut(ValidationUtil.Checks checks)
    {
        const int width = 7, height = 5;
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = new Color32((byte)(30 * x + 10), (byte)(50 * y + 5), (byte)(200 - 20 * x), (byte)(40 * x));
        }
        byte[] capture;
        var source = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            source.SetPixels32(pixels);
            source.Apply();
            capture = source.EncodeToPNG();
        }
        finally
        {
            Object.DestroyImmediate(source);
        }

        // The made-up capture has to carry an alpha channel the way Unity's does, or nothing below it proves anything.
        StoreScreenshotCapture.TryReadPngHeader(capture, out _, out _, out int before);
        checks.That(before == StoreScreenshotCapture.PngRgba,
            $"the test capture should be RGBA like Unity's (PNG colour type 6); it is colour type {before}");

        byte[] saved = StoreScreenshotCapture.WithoutAlpha(capture, out int savedWidth, out int savedHeight);
        bool header = StoreScreenshotCapture.TryReadPngHeader(saved, out int headerWidth, out int headerHeight, out int after);
        checks.That(header && after == StoreScreenshotCapture.PngRgb,
            $"a saved screenshot must be plain RGB (PNG colour type 2) — App Store Connect refuses any PNG with an alpha " +
            $"channel; it is colour type {after}");
        checks.That(savedWidth == width && savedHeight == height && headerWidth == width && headerHeight == height,
            $"a {width}x{height} capture was saved as {headerWidth}x{headerHeight}");

        // Every pixel the colour it was captured, the clear ones too: a premultiplying encoder would have turned those
        // black.
        var back = new Texture2D(2, 2);
        try
        {
            checks.That(back.LoadImage(saved), "a saved screenshot won't load back as a PNG");
            Color32[] read = back.GetPixels32();
            int changed = 0;
            string first = null;
            for (int i = 0; i < pixels.Length && i < read.Length; i++)
            {
                if (read[i].r == pixels[i].r && read[i].g == pixels[i].g && read[i].b == pixels[i].b && read[i].a == 255) continue;
                changed++;
                first ??= read[i].r == pixels[i].r && read[i].g == pixels[i].g && read[i].b == pixels[i].b
                    ? $"pixel {i} kept an alpha of {read[i].a}"
                    : $"pixel {i} came back {read[i]} for {pixels[i]}";
            }
            checks.That(read.Length == pixels.Length && changed == 0,
                $"{changed} of {pixels.Length} pixels didn't come back as the colour captured, fully opaque; {first}");
        }
        finally
        {
            Object.DestroyImmediate(back);
        }
    }

    private static void ShotNumbers(ValidationUtil.Checks checks)
    {
        void Next(string[] names, int expected, string what)
        {
            int actual = StoreScreenshotCapture.NextShotNumber(names);
            checks.That(actual == expected, $"{what}: the next shot would be {actual}, expected {expected}");
        }

        Next(new string[0], 1, "an empty folder");
        Next(new[] { "iPhone-6.5-2778x1284-01.png", "iPad-13-2752x2064-01.png" }, 2, "one shot at both sizes");
        // 1.0's folder: a gap, a 12.9" iPad resize and a wrong-size capture this tool no longer makes, a leftover.
        Next(new[]
        {
            "iPhone-6.5-2778x1284-08.png", "iPad-13-2752x2064-03.png", "iPad-12.9-2732x2048-11.png",
            "WRONG-SIZE-2868x1320-20.png", "pending.png",
        }, 9, "a folder with a gap in it, another size and strays");
        Next(new[] { "iPad-13-2752x2064-12.png" }, 13, "a shot that only reached one size");

        // Both sizes of one shot carry its number.
        string phone = StoreScreenshotCapture.FileName(StoreScreenshotCapture.Sizes[0], 9);
        string pad = StoreScreenshotCapture.FileName(StoreScreenshotCapture.Sizes[1], 9);
        checks.That(phone == "iPhone-6.5-2778x1284-09.png" && pad == "iPad-13-2752x2064-09.png",
            $"shot 9 should be iPhone-6.5-2778x1284-09.png and iPad-13-2752x2064-09.png; it is {phone} and {pad}");
        Next(new[] { phone, pad }, 10, "the tool's own names, read back");
    }
}
