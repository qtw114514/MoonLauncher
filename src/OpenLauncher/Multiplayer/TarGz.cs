using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using OpenLauncher.Core;

namespace OpenLauncher.Multiplayer
{
    /// <summary>零依赖的受限 tar.gz 读取器：只允许明确列出的普通文件，不处理链接和路径。</summary>
    internal static class TarGz
    {
        const int BlockSize = 512;
        const long MaxFileSize = 512L * 1024 * 1024;

        internal static void ExtractFiles(string archive, string destination, ISet<string> expectedNames, CancellationToken ct)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            using (var file = new FileStream(Util.LP(archive), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                var header = new byte[BlockSize];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ReadBlock(gzip, header)) break;
                    if (IsZeroBlock(header)) break;

                    string name = ReadString(header, 0, 100);
                    string prefix = ReadString(header, 345, 155);
                    if (prefix.Length > 0) name = prefix + "/" + name;
                    byte type = header[156];
                    long size = ReadOctal(header, 124, 12);
                    if (size < 0 || size > MaxFileSize) throw new InvalidDataException("tar 条目大小无效或过大");

                    // 目录不含输出，可安全跳过；其他条目必须是白名单中的根目录普通文件。
                    if (type == (byte)'5')
                    {
                        Skip(gzip, size, ct);
                        SkipPadding(gzip, size, ct);
                        continue;
                    }
                    if ((type != 0 && type != (byte)'0') || !expectedNames.Contains(name) || !found.Add(name))
                        throw new InvalidDataException("tar 包含未允许、重复或非普通文件条目：" + name);

                    string target = Path.Combine(destination, name);
                    Util.EnsureParent(target);
                    using (var output = new FileStream(Util.LP(target), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        CopyExactly(gzip, output, size, ct);
                    SkipPadding(gzip, size, ct);
                }
            }

            foreach (string name in expectedNames)
                if (!found.Contains(name)) throw new InvalidDataException("tar 缺少预期文件：" + name);
        }

        static bool ReadBlock(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    if (offset == 0) return false;
                    throw new EndOfStreamException("tar 头部不完整");
                }
                offset += read;
            }
            return true;
        }

        static bool IsZeroBlock(byte[] block)
        {
            foreach (byte value in block) if (value != 0) return false;
            return true;
        }

        static string ReadString(byte[] buffer, int offset, int count)
        {
            int length = 0;
            while (length < count && buffer[offset + length] != 0) length++;
            return Encoding.UTF8.GetString(buffer, offset, length);
        }

        static long ReadOctal(byte[] buffer, int offset, int count)
        {
            long value = 0;
            int i = offset, end = offset + count;
            while (i < end && (buffer[i] == 0 || buffer[i] == (byte)' ')) i++;
            for (; i < end && buffer[i] != 0 && buffer[i] != (byte)' '; i++)
            {
                byte c = buffer[i];
                if (c < (byte)'0' || c > (byte)'7') throw new InvalidDataException("tar 八进制字段无效");
                value = checked(value * 8 + c - (byte)'0');
            }
            return value;
        }

        static void CopyExactly(Stream input, Stream output, long size, CancellationToken ct)
        {
            var buffer = new byte[81920];
            while (size > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size));
                if (read == 0) throw new EndOfStreamException("tar 文件内容不完整");
                output.Write(buffer, 0, read);
                size -= read;
            }
        }

        static void Skip(Stream input, long size, CancellationToken ct)
        {
            var buffer = new byte[81920];
            while (size > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size));
                if (read == 0) throw new EndOfStreamException("tar 条目内容不完整");
                size -= read;
            }
        }

        static void SkipPadding(Stream input, long size, CancellationToken ct)
        {
            long padding = (BlockSize - size % BlockSize) % BlockSize;
            Skip(input, padding, ct);
        }
    }
}
