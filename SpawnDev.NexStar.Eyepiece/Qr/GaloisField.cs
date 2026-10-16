// Copied from SpawnDev.ILGPU (MIT, same author), QR/GaloisField.cs: only the encoder, which needs no GPU. A standalone package would let both share it.
namespace SpawnDev.NexStar.Eyepiece.Qr;

/// <summary>
/// Galois Field GF(256) arithmetic for QR code Reed-Solomon error correction.
/// Uses the irreducible polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D = 285).
///
/// All QR code error correction operates in this field:
///   Addition = XOR
///   Multiplication = antilog[log[a] + log[b]] (mod 255)
///
/// The log/antilog tables map between integer and exponent representations,
/// enabling O(1) multiplication via exponent addition.
/// </summary>
public static class GaloisField
{
    /// <summary>Primitive polynomial: x^8 + x^4 + x^3 + x^2 + 1 = 285.</summary>
    public const int Primitive = 0x11D;

    /// <summary>Exponent → integer. antilog[i] = 2^i in GF(256).</summary>
    public static readonly byte[] Exp = new byte[512]; // doubled for mod-free wraparound

    /// <summary>Integer → exponent. log[antilog[i]] = i. log[0] is undefined (set to 0).</summary>
    public static readonly byte[] Log = new byte[256];

    static GaloisField()
    {
        // Generate exp (antilog) table: successive powers of 2 in GF(256)
        int val = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)val;
            Log[val] = (byte)i;
            val <<= 1; // multiply by 2
            if (val >= 256)
                val ^= Primitive; // reduce modulo primitive polynomial
        }
        // Wrap the table for convenient mod-free access: exp[i] = exp[i % 255]
        for (int i = 255; i < 512; i++)
            Exp[i] = Exp[i - 255];
    }

    /// <summary>Multiply two values in GF(256).</summary>
    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return Exp[Log[a] + Log[b]]; // auto-wraps via doubled table
    }

    /// <summary>Divide a by b in GF(256). b must not be 0.</summary>
    public static byte Divide(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException("GF(256) division by zero");
        if (a == 0) return 0;
        return Exp[Log[a] + 255 - Log[b]];
    }

    /// <summary>Raise a to the power n in GF(256).</summary>
    public static byte Power(byte a, int n)
    {
        if (a == 0) return 0;
        return Exp[(Log[a] * n) % 255];
    }

    /// <summary>Compute the inverse of a in GF(256).</summary>
    public static byte Inverse(byte a)
    {
        if (a == 0) throw new DivideByZeroException("GF(256) inverse of zero");
        return Exp[255 - Log[a]];
    }

    /// <summary>
    /// Multiply two polynomials in GF(256).
    /// Coefficients are ordered highest degree first: poly[0] is the leading coefficient.
    /// </summary>
    public static byte[] PolyMultiply(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++)
            {
                result[i + j] ^= Multiply(a[i], b[j]);
            }
        }
        return result;
    }

    /// <summary>
    /// Polynomial division in GF(256). Returns the remainder.
    /// Used for Reed-Solomon encoding: remainder of message / generator = EC codewords.
    /// </summary>
    public static byte[] PolyDivide(byte[] dividend, byte[] divisor)
    {
        var result = new byte[dividend.Length];
        System.Array.Copy(dividend, result, dividend.Length);

        for (int i = 0; i < dividend.Length - divisor.Length + 1; i++)
        {
            if (result[i] == 0) continue;
            byte coef = result[i];
            for (int j = 1; j < divisor.Length; j++)
            {
                result[i + j] ^= Multiply(divisor[j], coef);
            }
        }

        // Return only the remainder (last divisor.Length - 1 terms)
        var remainder = new byte[divisor.Length - 1];
        System.Array.Copy(result, dividend.Length - remainder.Length, remainder, 0, remainder.Length);
        return remainder;
    }

    /// <summary>
    /// Build a Reed-Solomon generator polynomial for n error correction codewords.
    /// Generator = (x - α^0)(x - α^1)...(x - α^(n-1))
    /// </summary>
    public static byte[] BuildGeneratorPolynomial(int ecCodewords)
    {
        var generator = new byte[] { 1 };
        for (int i = 0; i < ecCodewords; i++)
        {
            generator = PolyMultiply(generator, new byte[] { 1, Exp[i] });
        }
        return generator;
    }

    /// <summary>
    /// Compute Reed-Solomon error correction codewords for the given data.
    /// </summary>
    /// <param name="data">Data codewords.</param>
    /// <param name="ecCount">Number of error correction codewords to generate.</param>
    /// <returns>Error correction codewords.</returns>
    public static byte[] ComputeEC(byte[] data, int ecCount)
    {
        var generator = BuildGeneratorPolynomial(ecCount);

        // Pad data with ecCount zeros (multiply by x^ecCount)
        var padded = new byte[data.Length + ecCount];
        System.Array.Copy(data, padded, data.Length);

        return PolyDivide(padded, generator);
    }
}
