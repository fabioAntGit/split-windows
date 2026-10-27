using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace Split.App;

/// <summary>The wheel as a named group; each slice is a button for the screen reader, named like its label, and inside a group a Back button comes first.</summary>
public sealed class WheelAutomationPeer(WheelView wheel) : FrameworkElementAutomationPeer(wheel)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => "Wheel";

    protected override string GetNameCore() => wheel.Subtitle.Length == 0 ? wheel.Title : $"{wheel.Title}, {wheel.Subtitle}";

    protected override List<AutomationPeer> GetChildrenCore()
    {
        List<AutomationPeer> children = [.. Enumerable.Range(0, wheel.Items.Count).Select(index => (AutomationPeer)new SliceAutomationPeer(wheel, index))];
        if (wheel.IsInGroup) children.Insert(0, new SliceAutomationPeer(wheel, -1));
        return children;
    }
}

/// <summary>One slice, or (with index -1) the centre of an open group, which goes back.</summary>
public sealed class SliceAutomationPeer(WheelView wheel, int index) : AutomationPeer, IInvokeProvider
{
    private bool IsBack => index < 0;

    public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : null;

    public void Invoke()
    {
        if (IsBack) wheel.Dispatcher.Invoke(() => wheel.GoBack());
        else wheel.Dispatcher.Invoke(() => wheel.Choose(index));
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    protected override string GetClassNameCore() => "WheelSlice";

    protected override string GetNameCore() => IsBack ? L("Back to %@", wheel.ParentTitle ?? "") : wheel.Items[index].Title;

    protected override string GetHelpTextCore() => IsBack ? "" : wheel.Items[index].Detail ?? "";

    protected override string GetLocalizedControlTypeCore() => !IsBack && wheel.Items[index].IsGroup ? L("group") : base.GetLocalizedControlTypeCore();

    protected override string GetAutomationIdCore() => IsBack ? "back" : wheel.Items[index].Id;

    protected override Rect GetBoundingRectangleCore() => IsBack ? wheel.HubScreenFrame : wheel.ScreenFrame(index);

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    protected override bool IsOffscreenCore() => false;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override bool HasKeyboardFocusCore() => !IsBack && wheel.Highlighted == index;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => true;
    protected override bool IsKeyboardFocusableCore() => false;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override string GetItemStatusCore() => "";
    protected override string GetItemTypeCore() => "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override Point GetClickablePointCore() => GetBoundingRectangleCore() is { IsEmpty: false } rect ? new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2) : new Point(double.NaN, double.NaN);
    protected override void SetFocusCore() { }
}
