namespace AsciiVision.Processing;

/// <summary>
/// Chooses a line-drawing character from the averaged structure tensor of the edge pixels in a cell.
///
/// The tensor (Sxx = Σgx², Syy = Σgy², Sxy = Σgx·gy) gives the dominant gradient orientation
/// φ = ½·atan2(2Sxy, Sxx − Syy) without the sign ambiguity of averaging raw gradients
/// (both sides of a thin line point in opposite directions). The edge itself runs perpendicular
/// to the gradient. Image rows grow downwards, so the angle is flipped to the usual y-up convention
/// before binning: 0° → '-', 45° → '/', 90° → '|', 135° → '\'.
/// </summary>
public static class EdgeCharacterMapper
{
    public const char Vertical = '|';
    public const char Horizontal = '-';
    public const char Low = '_';
    public const char Rising = '/';
    public const char Falling = '\\';
    public const char Junction = '+';

    public static readonly string AllCharacters = "|/\\-_+";

    /// <summary>Edge tangent angle in degrees, y-up, normalized to [0, 180).</summary>
    public static double EdgeAngleDegrees(double sxx, double syy, double sxy)
    {
        double gradientDown = 0.5 * Math.Atan2(2 * sxy, sxx - syy); // y-down image coordinates
        double tangentUp = -gradientDown * 180.0 / Math.PI + 90.0;
        tangentUp %= 180.0;
        if (tangentUp < 0)
        {
            tangentUp += 180.0;
        }

        return tangentUp;
    }

    /// <summary>0 = isotropic (corner/noise), 1 = a single clean orientation.</summary>
    public static double Coherence(double sxx, double syy, double sxy)
    {
        double trace = sxx + syy;
        if (trace <= 1e-9)
        {
            return 0;
        }

        double diff = sxx - syy;
        return Math.Sqrt(diff * diff + 4 * sxy * sxy) / trace;
    }

    public static char FromAngle(double angleDegrees, double centroidY = 0.5)
    {
        if (angleDegrees < 22.5 || angleDegrees >= 157.5)
        {
            // A horizontal edge hugging the bottom of the cell reads better as an underscore.
            return centroidY > 0.7 ? Low : Horizontal;
        }

        if (angleDegrees < 67.5)
        {
            return Rising;
        }

        return angleDegrees < 112.5 ? Vertical : Falling;
    }

    /// <param name="centroidY">Mean vertical position of edge pixels inside the cell, 0 = top, 1 = bottom.</param>
    /// <param name="strength">Edge pixel count relative to the detection threshold (≥ 1 means "is an edge").</param>
    public static char Classify(double sxx, double syy, double sxy, double centroidY, double strength = 1.0)
    {
        double coherence = Coherence(sxx, syy, sxy);
        if (coherence < 0.3 && strength >= 1.6)
        {
            // Several strong orientations meet: a junction or corner.
            return Junction;
        }

        return FromAngle(EdgeAngleDegrees(sxx, syy, sxy), centroidY);
    }
}
