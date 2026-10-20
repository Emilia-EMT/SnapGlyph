using System.IO;
using SnapGlyph.App;
using SkiaSharp;

if (args.Length < 2 || !long.TryParse(args[0], out var rawHandle))
{
    Console.Error.WriteLine("Usage: CaptureSmoke <hwnd> <output-prefix>");
    return 2;
}

var target = CaptureEnumerator.GetWindowInfo(new IntPtr(rawHandle));
if (target.Handle == IntPtr.Zero || target.Bounds.Width <= 0)
{
    Console.Error.WriteLine("Invalid target window");
    return 3;
}

var candidates = CaptureEnumerator.Enumerate(target);
Console.WriteLine($"target={target.Title} {target.Bounds.Width}x{target.Bounds.Height} candidates={candidates.Count}");
foreach (var candidate in candidates.Take(20))
{
    Console.WriteLine($"{candidate.Kind} | {candidate.Name} | {candidate.ScreenRect}");
}

var selected = candidates.FirstOrDefault(candidate => !candidate.IsWindowRoot)
    ?? candidates.FirstOrDefault();
if (selected is null)
{
    Console.Error.WriteLine("No capture candidate");
    return 4;
}

using var bitmap = CaptureService.Capture(target.Handle, selected.ScreenRect);
using var image = SKImage.FromBitmap(bitmap);
using var data = image.Encode(SKEncodedImageFormat.Png, 100);
var path = $"{args[1]}.png";
using (var stream = File.Create(path))
{
    data.SaveTo(stream);
}
Console.WriteLine($"captured {selected.Kind} {selected.ScreenRect.Width}x{selected.ScreenRect.Height} -> {path}");
return 0;
