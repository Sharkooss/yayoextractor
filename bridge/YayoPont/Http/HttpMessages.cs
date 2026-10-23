using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YayoPont.Http
{
    internal sealed class HttpRequest
    {
        public string Method { get; private set; }
        public string Path { get; private set; }
        public Dictionary<string, string> Query { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = new byte[0];

        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        public string QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;

        public string BodyText => Encoding.UTF8.GetString(Body);

        public static HttpRequest ParseHead(string head)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length < 3) throw new FormatException("Ligne de requête invalide");
            var req = new HttpRequest { Method = parts[0].ToUpperInvariant() };
            var target = parts[1];
            int q = target.IndexOf('?');
            req.Path = Uri.UnescapeDataString(q < 0 ? target : target.Substring(0, q));
            if (q >= 0)
            {
                foreach (var pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    var key = Decode(eq < 0 ? pair : pair.Substring(0, eq));
                    req.Query[key] = eq < 0 ? "" : Decode(pair.Substring(eq + 1));
                }
            }
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return req;
        }

        private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    internal sealed class HttpResponse
    {
        public int Status { get; set; } = 200;
        public string ContentType { get; set; } = "application/json; charset=utf-8";
        public byte[] Body { get; set; } = new byte[0];

        /// <summary>Corps envoyé en flux (fichier du téléphone) ; fermé après envoi.</summary>
        public Stream Stream { get; set; }

        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static HttpResponse Json(object value, int status = 200) => new HttpResponse
        {
            Status = status,
            Body = Encoding.UTF8.GetBytes(Http.Json.Serialize(value)),
        };

        public static HttpResponse Error(int status, string message) =>
            Json(new Dictionary<string, object> { ["error"] = message }, status);

        public static HttpResponse Bytes(byte[] data, string contentType) => new HttpResponse
        {
            Body = data,
            ContentType = contentType,
        };

        public static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 503: return "Service Unavailable";
                default: return "Error";
            }
        }
    }
}
