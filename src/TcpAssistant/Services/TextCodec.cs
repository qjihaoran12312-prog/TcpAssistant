using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TcpAssistant.Services
{
    /// <summary>编码、HEX、转义三件套。和界面无关，可单测。</summary>
    public static class TextCodec
    {
        public static readonly IReadOnlyList<string> Encodings = new List<string>
        {
            "UTF-8", "GBK", "ASCII", "Latin-1"
        };

        private static readonly Dictionary<string, Encoding> Cache = new Dictionary<string, Encoding>
        {
            { "UTF-8", new UTF8Encoding(false) },
            { "GBK", Encoding.GetEncoding(936) },
            { "ASCII", Encoding.ASCII },
            { "Latin-1", Encoding.GetEncoding(28591) }
        };

        public static bool IsSupportedEncoding(string name) => name != null && Cache.ContainsKey(name);

        public static Encoding Resolve(string name)
        {
            Encoding enc;
            return Cache.TryGetValue(name ?? "UTF-8", out enc) ? enc : Cache["UTF-8"];
        }

        /// <summary>文本 -> 字节。不可表示的字符替换为 ?，绝不在发数据时抛异常。</summary>
        public static byte[] Encode(string text, string encodingName)
        {
            if (string.IsNullOrEmpty(text)) return new byte[0];
            return Resolve(encodingName).GetBytes(text);
        }

        /// <summary>字节 -> 文本。非法序列用替换符显示，保证日志一定能看见东西。</summary>
        public static string Decode(byte[] data, string encodingName)
        {
            if (data == null || data.Length == 0) return string.Empty;
            return Resolve(encodingName).GetString(data);
        }

        public static string ToHex(byte[] data, string separator = " ")
        {
            if (data == null || data.Length == 0) return string.Empty;
            var sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(separator);
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>解析 HEX 字符串，容忍空格/逗号/0x 前缀。失败返回 false 并给出原因。</summary>
        public static bool TryParseHex(string text, out byte[] data, out string error)
        {
            data = new byte[0];
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;

            var cleaned = Regex.Replace(text, @"0[xX]|[\s,;:_\-]|\\x", "");
            if (cleaned.Length == 0) return true;
            if (cleaned.Length % 2 != 0)
            {
                error = "HEX 长度必须为偶数（每两个字符代表一个字节）";
                return false;
            }
            if (!Regex.IsMatch(cleaned, "^[0-9A-Fa-f]+$"))
            {
                error = "HEX 中含有非十六进制字符";
                return false;
            }

            var bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(cleaned.Substring(i * 2, 2), NumberStyles.HexNumber);
            }
            data = bytes;
            return true;
        }

        private static readonly Dictionary<char, char> Escapes = new Dictionary<char, char>
        {
            { 'r', '\r' }, { 'n', '\n' }, { 't', '\t' }, { '0', '\0' }, { '\\', '\\' }, { 's', ' ' }
        };

        /// <summary>把界面里输入的 \r\n \t \0 \x41 还原成真实字符。</summary>
        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch != '\\' || i == text.Length - 1)
                {
                    sb.Append(ch);
                    continue;
                }

                char next = text[i + 1];
                if ((next == 'x' || next == 'X') && i + 3 < text.Length)
                {
                    int code;
                    if (int.TryParse(text.Substring(i + 2, 2), NumberStyles.HexNumber,
                                     CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 3;
                        continue;
                    }
                }

                char mapped;
                if (Escapes.TryGetValue(next, out mapped))
                {
                    sb.Append(mapped);
                    i++;
                    continue;
                }

                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>反向：把控制字符显示成 \r\n 之类，方便在单行里看。</summary>
        public static string EscapeControl(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length + 8);
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\x").Append(((int)ch).ToString("X2"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成一行可读摘要：可读性差就退化成 HEX，避免二进制报文把日志刷成乱码。
        /// </summary>
        public static string BuildPreview(byte[] data, string encodingName, int limit = 200)
        {
            if (data == null || data.Length == 0) return "";

            string text = Decode(data, encodingName);
            int readable = 0;
            foreach (char ch in text)
            {
                // 换行、制表符算可读，否则短报文（PING\r\n）会被误判成二进制而显示成 HEX
                if (!char.IsControl(ch) || ch == '\r' || ch == '\n' || ch == '\t') readable++;
            }

            string body = text.Length > 0 && readable * 10 >= text.Length * 7
                ? EscapeControl(text)
                : ToHex(data);

            body = body.Replace("\r", " ").Replace("\n", " ").Trim();
            if (body.Length > limit) body = body.Substring(0, limit) + "…";
            return body;
        }
    }
}
