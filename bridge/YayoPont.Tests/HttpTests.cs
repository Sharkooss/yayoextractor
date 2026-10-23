using System;
using System.Collections.Generic;
using System.Text;
using YayoPont.Http;
using Xunit;

namespace YayoPont.Tests
{
    public class HttpTests
    {
        private const int Port = 47863;
        private const string Site = "https://yayoextractor.louis-nectoux.fr";

        private static HttpRequest Request(string method, string target, string host = "127.0.0.1:47863",
            string origin = null, string token = null)
        {
            var head = new StringBuilder($"{method} {target} HTTP/1.1\r\nHost: {host}\r\n");
            if (origin != null) head.Append($"Origin: {origin}\r\n");
            if (token != null) head.Append($"X-Yayo-Token: {token}\r\n");
            return HttpRequest.ParseHead(head.ToString());
        }

        [Fact]
        public void Analyse_la_ligne_de_requete_et_la_query()
        {
            var req = Request("GET", "/thumb/o%2F12?t=abc&x=1");
            Assert.Equal("GET", req.Method);
            Assert.Equal("/thumb/o/12", req.Path);
            Assert.Equal("abc", req.Query["t"]);
            Assert.Equal("127.0.0.1:47863", req.Header("HOST"));
        }

        [Fact]
        public void Refuse_un_hote_etranger()
        {
            var sec = new Security(Port);
            Assert.Equal(403, sec.Gate(Request("GET", "/status", host: "evil.com:47863"), false).Status);
        }

        [Fact]
        public void Refuse_une_origine_etrangere_meme_sans_jeton_requis()
        {
            var sec = new Security(Port);
            Assert.Equal(403, sec.Gate(Request("POST", "/session", origin: "https://evil.com"), false).Status);
            Assert.Equal(403, sec.Gate(Request("POST", "/session", origin: "null"), false).Status);
        }

        [Fact]
        public void Exige_le_jeton_sur_les_routes_protegees()
        {
            var sec = new Security(Port);
            Assert.Equal(401, sec.Gate(Request("GET", "/devices", origin: Site), true).Status);
            Assert.Equal(401, sec.Gate(Request("GET", "/devices", origin: Site, token: "faux"), true).Status);
            Assert.Null(sec.Gate(Request("GET", "/devices", origin: Site, token: sec.Token), true));
            Assert.Null(sec.Gate(Request("GET", "/thumb/1?t=" + sec.Token), true));
            Assert.Null(sec.Gate(Request("GET", "/status", origin: "http://localhost:8124"), false));
        }

        [Fact]
        public void Repond_au_preflight_avec_l_acces_reseau_prive()
        {
            var sec = new Security(Port);
            var res = sec.Preflight(Request("OPTIONS", "/scan", origin: Site));
            Assert.Equal(204, res.Status);
            Assert.Equal(Site, res.Headers["Access-Control-Allow-Origin"]);
            Assert.Equal("true", res.Headers["Access-Control-Allow-Private-Network"]);
            Assert.Contains("X-Yayo-Token", res.Headers["Access-Control-Allow-Headers"]);
            Assert.Equal(403, sec.Preflight(Request("OPTIONS", "/scan", origin: "https://evil.com")).Status);
        }

        [Fact]
        public void Json_aller_retour_avec_accents_et_echappements()
        {
            var json = Json.Serialize(new Dictionary<string, object>
            {
                ["nom"] = "Photo « été » \"1\"\n",
                ["taille"] = 1234567890123L,
                ["ok"] = true,
                ["vide"] = null,
                ["ids"] = new List<object> { "a", "b" },
                ["date"] = new DateTime(2026, 10, 21, 14, 3, 22, DateTimeKind.Local),
            });
            var back = (Dictionary<string, object>)Json.Parse(json);
            Assert.Equal("Photo « été » \"1\"\n", back["nom"]);
            Assert.Equal(1234567890123d, back["taille"]);
            Assert.Equal(true, back["ok"]);
            Assert.Null(back["vide"]);
            Assert.Equal(new List<object> { "a", "b" }, back["ids"]);
            Assert.Equal("2026-10-21T14:03:22", back["date"]);
        }

        [Fact]
        public void Json_parse_un_corps_de_transfert()
        {
            var body = (Dictionary<string, object>)Json.Parse("{\"driveId\":\"E\",\"ids\":[\"o1\",\"o2\"]}");
            Assert.Equal("E", body["driveId"]);
            Assert.Equal(2, ((List<object>)body["ids"]).Count);
        }
    }
}
