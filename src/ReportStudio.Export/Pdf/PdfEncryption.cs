#pragma warning disable CA5350, CA5351 // MD5, RC4 and a fixed padding are what the PDF standard security handler (revision 4) prescribes.
using System.Security.Cryptography;
using System.Text;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// PDF standard security handler, version 4 / revision 4: AES-128 in CBC mode.
/// The file key is made from the user password, the owner entry, the permissions and the file id;
/// every string and stream gets its own key made from the file key and its object number.
/// </summary>
internal sealed class PdfEncryption
{
    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private readonly byte[] _fileKey;

    public byte[] OwnerEntry { get; }
    public byte[] UserEntry { get; }
    public int Permissions { get; }
    public byte[] FileId { get; }

    public PdfEncryption(PdfOptions options, byte[] fileId)
    {
        FileId = fileId;
        Permissions = PermissionBits(options);

        var userPassword = PadPassword(options.UserPassword);
        // No owner password: use a random one nobody knows, so the restrictions cannot be lifted later.
        var owner = string.IsNullOrEmpty(options.OwnerPassword)
            ? RandomNumberGenerator.GetBytes(32)
            : PadPassword(options.OwnerPassword);

        OwnerEntry = ComputeOwnerEntry(owner, userPassword);
        _fileKey = ComputeFileKey(userPassword, OwnerEntry, Permissions, fileId);
        UserEntry = ComputeUserEntry(_fileKey, fileId);
    }

    /// <summary>The /P value: bits 7-8 and 13-32 are set, the rest says what is allowed.</summary>
    public static int PermissionBits(PdfOptions options)
    {
        uint bits = 0xFFFFF0C0;
        if (options.AllowPrint) bits |= 4 | 2048;                // print, print in high quality
        if (options.AllowModify) bits |= 8 | 32 | 256 | 1024;    // modify, annotate, fill forms, assemble pages
        if (options.AllowCopy) bits |= 16 | 512;                 // copy, extract for accessibility
        return unchecked((int)bits);
    }

    /// <summary>The /Encrypt dictionary (written without being encrypted itself).</summary>
    public PdfDict ToDictionary()
    {
        var std = new PdfDict()
            .PutName("CFM", "AESV2")
            .PutName("AuthEvent", "DocOpen")
            .Put("Length", 16);
        var filters = new PdfDict().Put("StdCF", std);

        return new PdfDict()
            .PutName("Filter", "Standard")
            .Put("V", 4)
            .Put("R", 4)
            .Put("Length", 128)
            .Put("CF", filters)
            .PutName("StmF", "StdCF")
            .PutName("StrF", "StdCF")
            .Put("O", new PdfString(OwnerEntry))
            .Put("U", new PdfString(UserEntry))
            .Put("P", Permissions);
    }

    /// <summary>AES-128-CBC with a fresh random IV in front, PKCS#7 padding.</summary>
    public byte[] Encrypt(byte[] data, int objectNumber)
    {
        using var aes = Aes.Create();
        aes.Key = ObjectKey(objectNumber);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();
        var cipher = aes.EncryptCbc(data, aes.IV, PaddingMode.PKCS7);
        var result = new byte[16 + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, 16);
        Buffer.BlockCopy(cipher, 0, result, 16, cipher.Length);
        return result;
    }

    // ---- the standard handler's algorithms (ISO 32000-1, 7.6.3) ----

    private static byte[] PadPassword(string? password)
    {
        var bytes = Encoding.Latin1.GetBytes(password ?? "");
        var padded = new byte[32];
        var count = Math.Min(32, bytes.Length);
        Buffer.BlockCopy(bytes, 0, padded, 0, count);
        Buffer.BlockCopy(Padding, 0, padded, count, 32 - count);
        return padded;
    }

    private static byte[] ComputeOwnerEntry(byte[] ownerPadded, byte[] userPadded)
    {
        var hash = MD5.HashData(ownerPadded);
        for (int i = 0; i < 50; i++) hash = MD5.HashData(hash);

        var result = Rc4(hash, userPadded);
        for (int i = 1; i <= 19; i++) result = Rc4(Xor(hash, i), result);
        return result;
    }

    private static byte[] ComputeFileKey(byte[] userPadded, byte[] ownerEntry, int permissions, byte[] fileId)
    {
        var input = new List<byte>();
        input.AddRange(userPadded);
        input.AddRange(ownerEntry);
        input.AddRange(BitConverter.GetBytes(permissions));      // little-endian on every platform .NET runs on here
        input.AddRange(fileId);

        var hash = MD5.HashData(input.ToArray());
        for (int i = 0; i < 50; i++) hash = MD5.HashData(hash);
        return hash;                                             // 16 bytes = the whole 128-bit key
    }

    private static byte[] ComputeUserEntry(byte[] fileKey, byte[] fileId)
    {
        var input = new List<byte>(Padding);
        input.AddRange(fileId);
        var result = Rc4(fileKey, MD5.HashData(input.ToArray()));
        for (int i = 1; i <= 19; i++) result = Rc4(Xor(fileKey, i), result);

        var entry = new byte[32];                                // 16 bytes of result, 16 bytes of anything
        Buffer.BlockCopy(result, 0, entry, 0, 16);
        return entry;
    }

    private byte[] ObjectKey(int objectNumber)
    {
        var input = new byte[_fileKey.Length + 9];
        Buffer.BlockCopy(_fileKey, 0, input, 0, _fileKey.Length);
        var n = _fileKey.Length;
        input[n] = (byte)objectNumber;
        input[n + 1] = (byte)(objectNumber >> 8);
        input[n + 2] = (byte)(objectNumber >> 16);
        input[n + 3] = 0;                                        // generation number 0
        input[n + 4] = 0;
        input[n + 5] = 0x73; input[n + 6] = 0x41; input[n + 7] = 0x6C; input[n + 8] = 0x54;   // "sAlT" for AES
        return MD5.HashData(input);                              // 16 bytes
    }

    private static byte[] Xor(byte[] key, int value)
    {
        var result = new byte[key.Length];
        for (int i = 0; i < key.Length; i++) result[i] = (byte)(key[i] ^ value);
        return result;
    }

    /// <summary>RC4, only used to build the /O and /U entries (the file itself is AES).</summary>
    internal static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var result = new byte[data.Length];
        int a = 0, b = 0;
        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            result[k] = (byte)(data[k] ^ s[(s[a] + s[b]) & 0xFF]);
        }
        return result;
    }
}
