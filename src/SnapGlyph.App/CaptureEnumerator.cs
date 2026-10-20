using System.Windows.Automation;
using SkiaSharp;

namespace SnapGlyph.App;

public sealed record CaptureWindowInfo(
    IntPtr Handle,
    SKRectI Bounds,
    string Title);

public sealed record CaptureCandidate(
    SKRectI ScreenRect,
    string Name,
    string Kind,
    bool IsWindowRoot = false)
{
    public long Area => (long)Math.Max(0, ScreenRect.Width) * Math.Max(0, ScreenRect.Height);
}

public static class CaptureEnumerator
{
    private static readonly HashSet<string> IgnoredControlTypes =
    [
        "ControlType.Text",
        "ControlType.Inline",
    ];

    public static CaptureWindowInfo GetForegroundWindowInfo()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var title = NativeMethods.WindowText(hwnd);
        return new CaptureWindowInfo(hwnd, NativeMethods.ExtendedBounds(hwnd), title);
    }

    public static CaptureWindowInfo GetWindowInfo(IntPtr hwnd) =>
        new(hwnd, NativeMethods.ExtendedBounds(hwnd), NativeMethods.WindowText(hwnd));

    public static IReadOnlyList<CaptureCandidate> EnumerateFast(
        CaptureWindowInfo target) => EnumerateCore(target, includeUia: false);

    public static IReadOnlyList<CaptureCandidate> Enumerate(
        CaptureWindowInfo target) => EnumerateCore(target, includeUia: true);

    private static IReadOnlyList<CaptureCandidate> EnumerateCore(
        CaptureWindowInfo target,
        bool includeUia)
    {
        var candidates = new List<CaptureCandidate>();
        var targetRect = target.Bounds;
        var seen = new HashSet<(int, int, int, int)>();

        void Add(SKRectI rect, string name, string kind, bool root = false)
        {
            if (rect.Width < 4 || rect.Height < 4)
            {
                return;
            }
            if (rect.Left < targetRect.Left - 2 || rect.Top < targetRect.Top - 2 ||
                rect.Right > targetRect.Right + 2 || rect.Bottom > targetRect.Bottom + 2)
            {
                return;
            }

            var key = (rect.Left, rect.Top, rect.Width, rect.Height);
            if (seen.Add(key))
            {
                candidates.Add(new CaptureCandidate(
                    rect,
                    string.IsNullOrWhiteSpace(name) ? kind : name,
                    kind,
                    root));
            }
        }

        Add(targetRect, target.Title, "Window", root: true);

        if (includeUia)
        {
            try
            {
                var root = AutomationElement.FromHandle(target.Handle);
                if (root is not null)
                {
                    var descendants = root.FindAll(
                        TreeScope.Descendants,
                        Condition.TrueCondition);
                    for (var i = 0; i < descendants.Count; i++)
                    {
                        var element = descendants[i];
                        var type = element.Current.ControlType?.ProgrammaticName ?? "ControlType.Custom";
                        if (IgnoredControlTypes.Contains(type) ||
                            element.Current.IsOffscreen)
                        {
                            continue;
                        }

                        var bounds = element.Current.BoundingRectangle;
                        if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4)
                        {
                            continue;
                        }

                        var name = element.Current.Name;
                        Add(
                            new SKRectI(
                                (int)Math.Round(bounds.Left),
                                (int)Math.Round(bounds.Top),
                                (int)Math.Round(bounds.Right),
                                (int)Math.Round(bounds.Bottom)),
                            name,
                            type.Replace("ControlType.", string.Empty));
                    }
                }
            }
            catch
            {
                // Some applications expose a partial or broken UIA tree.
                // Continue with HWND children below.
            }
        }

        NativeMethods.EnumChildWindows(
            target.Handle,
            (child, _) =>
            {
                if (NativeMethods.IsWindowVisible(child) &&
                    NativeMethods.GetWindowRect(child, out var nativeRect))
                {
                    Add(
                        nativeRect.ToSkiaRect(),
                        NativeMethods.WindowText(child),
                        $"HWND:{NativeMethods.WindowClass(child)}");
                }
                return true;
            },
            IntPtr.Zero);

        return candidates
            .OrderBy(candidate => candidate.Area)
            .ThenByDescending(candidate => candidate.IsWindowRoot)
            .ToArray();
    }
}
