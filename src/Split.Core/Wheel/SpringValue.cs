namespace Split;

/// <summary>
/// A value that follows its target like a critically damped spring and can be
/// re-targeted mid-flight without a jump (it keeps its current value and velocity).
/// </summary>
public sealed class SpringValue
{
    public double Value { get; set; }
    public double Velocity { get; set; }
    public double Target { get; set; }
    public double Response { get; set; } = 0.26;
    public double Damping { get; set; } = 1;

    public void Step(double dt)
    {
        var stiffness = Math.Pow(2 * Math.PI / Response, 2);
        var friction = 4 * Math.PI * Damping / Response;
        const int steps = 4;
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            Velocity += (-stiffness * (Value - Target) - friction * Velocity) * h;
            Value += Velocity * h;
        }
        if (IsSettled)
        {
            Value = Target;
            Velocity = 0;
        }
    }

    public bool IsSettled => Math.Abs(Value - Target) < 0.002 && Math.Abs(Velocity) < 0.02;
}
