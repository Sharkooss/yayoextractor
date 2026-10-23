using System;
using System.Linq;
using System.Security.Cryptography;

namespace YayoPont.Http
{
    /// <summary>
    /// Le pont écoute sur la machine de Yayo : n'importe quel site ouvert dans son
    /// navigateur pourrait l'appeler. Trois verrous : l'hôte (contre le
    /// DNS rebinding), l'origine (seul yayoextractor), et un jeton que seule une
    /// origine acceptée peut lire en réponse à POST /session.
    /// </summary>
    internal sealed class Security
    {
        public static readonly string[] AllowedOrigins =
        {
            "https://yayoextractor.louis-nectoux.fr",
            "http://localhost:8124",
            "http://127.0.0.1:8124",
        };

        private readonly int port;

        public string Token { get; }

        public Security(int port)
        {
            this.port = port;
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static bool OriginAllowed(string origin) => origin != null && AllowedOrigins.Contains(origin, StringComparer.Ordinal);

        /// <summary>null si la requête passe, sinon la réponse d'erreur à renvoyer.</summary>
        public HttpResponse Gate(HttpRequest req, bool needsToken)
        {
            if (!HostAllowed(req.Header("Host"))) return HttpResponse.Error(403, "Hôte refusé.");
            var origin = req.Header("Origin");
            if (origin != null && !OriginAllowed(origin)) return HttpResponse.Error(403, "Origine refusée.");
            if (needsToken && !SameSecret(req.Header("X-Yayo-Token") ?? req.QueryValue("t"), Token))
                return HttpResponse.Error(401, "Jeton manquant ou invalide.");
            return null;
        }

        public HttpResponse Preflight(HttpRequest req)
        {
            var origin = req.Header("Origin");
            if (!HostAllowed(req.Header("Host")) || !OriginAllowed(origin)) return HttpResponse.Error(403, "Origine refusée.");
            var res = new HttpResponse { Status = 204, ContentType = null };
            Decorate(req, res);
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Yayo-Token";
            res.Headers["Access-Control-Max-Age"] = "600";
            return res;
        }

        /// <summary>En-têtes CORS pour une origine acceptée (y compris sur les erreurs, pour que le site les lise).</summary>
        public void Decorate(HttpRequest req, HttpResponse res)
        {
            var origin = req.Header("Origin");
            res.Headers["Vary"] = "Origin";
            if (!OriginAllowed(origin)) return;
            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Access-Control-Allow-Private-Network"] = "true";
        }

        private bool HostAllowed(string host) =>
            host == $"127.0.0.1:{port}" || host == $"localhost:{port}";

        private static bool SameSecret(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
