using System;
using System.Collections.Generic;

namespace DROPME.Server
{
    /// <summary>
    /// Описывает разобранный HTTP-запрос для внутренних обработчиков DROPME.
    /// </summary>
    internal sealed class HttpRequest
    {
        public string Method { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public string HttpVersion { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string BodyFilePath { get; set; } = string.Empty;
        public long BodyLength { get; set; }
        public string BodySha256 { get; set; } = string.Empty;
        public string RemoteIp { get; set; } = string.Empty;
        public string IncomingTransferId { get; set; } = string.Empty;
        public string IncomingTransferName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Описывает HTTP-ответ и при необходимости потоковый файл для внутренних обработчиков DROPME.
    /// </summary>
    internal sealed class HttpResponse
    {
        public int StatusCode { get; set; } = 200;
        public string ReasonPhrase { get; set; } = "OK";
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string BodyFilePath { get; set; } = string.Empty;
        public long BodyFileLength { get; set; }
    }
}
