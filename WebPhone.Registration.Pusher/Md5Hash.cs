using System.Buffers.Binary;

namespace WebPhone.Registration.Pusher;

internal static class Md5Hash
{
    private static readonly uint[] S =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
    ];

    private static readonly uint[] K =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391
    ];

    public static string ComputeHashHex(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] ComputeHash(byte[] input)
    {
        var originalLength = input.Length;
        ulong bitLength = (ulong)originalLength * 8;
        var paddingLength = (56 - ((originalLength + 1) % 64) + 64) % 64;
        var totalLength = originalLength + 1 + paddingLength + 8;

        var buffer = new byte[totalLength];
        Buffer.BlockCopy(input, 0, buffer, 0, originalLength);
        buffer[originalLength] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(totalLength - 8), bitLength);

        uint a0 = 0x67452301;
        uint b0 = 0xefcdab89;
        uint c0 = 0x98badcfe;
        uint d0 = 0x10325476;

        var chunk = new uint[16];

        for (var offset = 0; offset < buffer.Length; offset += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                chunk[i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + i * 4, 4));
            }

            var a = a0;
            var b = b0;
            var c = c0;
            var d = d0;

            for (var i = 0; i < 64; i++)
            {
                uint f;
                int g;

                if (i < 16)
                {
                    f = (b & c) | (~b & d);
                    g = i;
                }
                else if (i < 32)
                {
                    f = (d & b) | (~d & c);
                    g = (5 * i + 1) % 16;
                }
                else if (i < 48)
                {
                    f = b ^ c ^ d;
                    g = (3 * i + 5) % 16;
                }
                else
                {
                    f = c ^ (b | ~d);
                    g = (7 * i) % 16;
                }

                var temp = d;
                d = c;
                c = b;
                b = b + LeftRotate(a + f + K[i] + chunk[g], (int)S[i]);
                a = temp;
            }

            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
        }

        var output = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), a0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8, 4), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12, 4), d0);
        return output;
    }

    private static uint LeftRotate(uint value, int shift)
        => (value << shift) | (value >> (32 - shift));
}
