namespace Beacon.Protocol;

/// <summary>
/// The live map's <c>heading</c>: which way a player looks, as Unreal's yaw in
/// whole degrees from 0 to 359. 0 points along the world's +X axis and 90 along
/// +Y, so the angle grows turning right as seen from above (Unreal's left-handed
/// axes, Z up). The direction in the map's own x/y is (cos heading, sin heading).
/// See protocol/map-v1.md.
/// </summary>
public static class MapHeading
{
    /// <summary>
    /// Any yaw in degrees (Unreal reports -180..180 or 0..360 depending on the
    /// source) to a whole degree from 0 to 359; null for no value, NaN or an
    /// infinity. 359.5 and up rounds to 360, which is 0.
    /// </summary>
    public static int? Normalize(double? degrees)
    {
        if (degrees is not { } d || !double.IsFinite(d)) return null;
        var r = d % 360.0;
        if (r < 0) r += 360.0;
        var whole = (int)Math.Round(r, MidpointRounding.AwayFromZero);
        return whole >= 360 ? whole - 360 : whole;
    }
}
