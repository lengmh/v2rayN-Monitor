using System.Globalization;
using System.Numerics;

namespace V2rayNMonitor.Core;

/// <summary>
/// Exact nonnegative provider values, bounded to a 96-bit coefficient and scale 0–28.
/// Unlike decimal parsing/addition, never rounds away digits that do not fit.
/// This type is separate from the local ledger's integer byte accounting.
/// </summary>
internal readonly record struct ProviderDecimal
{
    private static readonly BigInteger MaxCoefficient = (BigInteger.One << 96) - 1;
    private readonly BigInteger coefficient;
    private readonly int scale;

    private ProviderDecimal(BigInteger coefficient, int scale) { this.coefficient = coefficient; this.scale = scale; }
    internal BigInteger ScaledUnits => coefficient * BigInteger.Pow(10, 28 - scale);
    internal static string FormatScaledUnits(BigInteger units)
    {
        var scale = 28;
        while (scale > 0 && units % 10 == 0) { units /= 10; scale--; }
        return new ProviderDecimal(units, scale).ToString();
    }

    public static bool TryParse(string text, out ProviderDecimal value)
    {
        value = default;
        if (text.Length == 0) return false;
        var point = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is >= '0' and <= '9') continue;
            if (text[i] != '.' || point >= 0) return false;
            point = i;
        }
        if (text == ".") return false;
        var whole = (point < 0 ? text.AsSpan() : text.AsSpan(0, point)).TrimStart('0');
        var fraction = point < 0 ? ReadOnlySpan<char>.Empty : text.AsSpan(point + 1).TrimEnd('0');
        // Remove insignificant zeroes before applying the bound; no lossy rounding is permitted.
        if (fraction.Length > 28 || whole.Length + fraction.Length > 29) return false;
        var digits = string.Concat(whole, fraction);
        var coefficient = digits.Length == 0 ? BigInteger.Zero : BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (coefficient > MaxCoefficient) return false;
        value = new(coefficient, coefficient.IsZero ? 0 : fraction.Length);
        return true;
    }

    public static ProviderDecimal Sum(IEnumerable<ProviderDecimal> values)
    {
        var scale = 0;
        var coefficient = BigInteger.Zero;
        foreach (var value in values)
        {
            var nextScale = Math.Max(scale, value.scale);
            coefficient = coefficient * BigInteger.Pow(10, nextScale - scale)
                + value.coefficient * BigInteger.Pow(10, nextScale - value.scale);
            scale = nextScale;
        }
        // Only bound the final exact total: a later fractional carry can make an
        // otherwise unrepresentable intermediate total fit. Row order must not matter.
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (coefficient > MaxCoefficient)
            throw new OverflowException("服务商计费量合计超出可无损表示的十进制范围（96 位系数、最多 28 位小数）；未导入任何记录。");
        return new(coefficient, scale);
    }

    public override string ToString()
    {
        var digits = coefficient.ToString(CultureInfo.InvariantCulture);
        if (scale == 0) return digits;
        digits = digits.PadLeft(scale + 1, '0');
        return digits.Insert(digits.Length - scale, ".");
    }
}
