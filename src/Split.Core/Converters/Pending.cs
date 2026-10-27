namespace Split;

/// <summary>
/// The one place that says a conversion or a tool is not built yet. Each family and the tools
/// throw it from their Convert/Run until the real work replaces them.
/// </summary>
public static class Pending
{
    public static Problem Error() => new(L("This isn’t available yet."));
}
