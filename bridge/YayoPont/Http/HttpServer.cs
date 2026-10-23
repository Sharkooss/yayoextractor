using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace YayoPont.Http
{
    /// <summary>
    /// Mini serveur HTTP/1.1 sur TcpListener. Pas de HttpListener : celui-ci passe
    /// par http.sys, qui exige une réservation d'URL (donc l'administrateur).
    /// </summary>
    internal sealed class HttpServer : IDisposable
    {
        private const int MaxHeadBytes = 16 * 1024;
        private const int MaxBodyBytes = 1024 * 1024;

        private readonly TcpListener listener;
        private readonly Func<HttpRequest, HttpResponse> handler;
        private volatile bool running;

        public HttpServer(int port, Func<HttpRequest, HttpResponse> handler)
        {
            listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            this.handler = handler;
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Start()
        {
            listener.Start();
            running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "http-accept" }.Start();
        }

        public void Dispose()
        {
            running = false;
            try { listener.Stop(); } catch (SocketException) { }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 60000;
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new BufferedReader(stream);
                try
                {
                    while (running)
                    {
                        var head = reader.ReadHead(MaxHeadBytes);
                        if (head == null) return;
                        HttpRequest req;
                        try { req = HttpRequest.ParseHead(head); }
                        catch (FormatException) { Write(stream, HttpResponse.Error(400, "Requête invalide."), false); return; }

                        int length = 0;
                        if (req.Header("Content-Length") is string cl && (!int.TryParse(cl, out length) || length < 0 || length > MaxBodyBytes))
                        {
                            Write(stream, HttpResponse.Error(413, "Corps trop gros."), false);
                            return;
                        }
                        req.Body = reader.ReadExactly(length);

                        HttpResponse res;
                        try { res = handler(req); }
                        catch (Exception ex)
                        {
                            Log.Error("requête " + req.Method + " " + req.Path, ex);
                            res = HttpResponse.Error(500, "Erreur interne du pont.");
                        }
                        bool keepAlive = !string.Equals(req.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                        Write(stream, res, keepAlive);
                        if (!keepAlive) return;
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static void Write(Stream stream, HttpResponse res, bool keepAlive)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(HttpResponse.Reason(res.Status)).Append("\r\n");
            if (res.ContentType != null) head.Append("Content-Type: ").Append(res.ContentType).Append("\r\n");
            if (!res.Headers.ContainsKey("Cache-Control")) head.Append("Cache-Control: no-store\r\n");
            foreach (var header in res.Headers) head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");

            if (res.Stream == null)
            {
                head.Append("Content-Length: ").Append(res.Body.Length).Append("\r\n\r\n");
                var bytes = Encoding.ASCII.GetBytes(head.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Write(res.Body, 0, res.Body.Length);
                return;
            }

            // Flux de longueur inconnue (fichier du téléphone) : encodage chunked.
            head.Append("Transfer-Encoding: chunked\r\n\r\n");
            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            using (res.Stream)
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = res.Stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var size = Encoding.ASCII.GetBytes(read.ToString("x") + "\r\n");
                    stream.Write(size, 0, size.Length);
                    stream.Write(buffer, 0, read);
                    stream.Write(new byte[] { 13, 10 }, 0, 2);
                }
            }
            var end = Encoding.ASCII.GetBytes("0\r\n\r\n");
            stream.Write(end, 0, end.Length);
        }

        /// <summary>Lecture tamponnée : l'en-tête jusqu'à la ligne vide, puis le corps.</summary>
        private sealed class BufferedReader
        {
            private readonly Stream stream;
            private readonly byte[] buffer = new byte[MaxHeadBytes];
            private int start, end;

            public BufferedReader(Stream stream) { this.stream = stream; }

            public string ReadHead(int max)
            {
                while (true)
                {
                    for (int i = start; i + 3 < end; i++)
                    {
                        if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                        {
                            var head = Encoding.ASCII.GetString(buffer, start, i - start);
                            start = i + 4;
                            return head;
                        }
                    }
                    if (start > 0)
                    {
                        Array.Copy(buffer, start, buffer, 0, end - start);
                        end -= start;
                        start = 0;
                    }
                    if (end >= max) throw new IOException("En-tête trop long");
                    int read = stream.Read(buffer, end, buffer.Length - end);
                    if (read <= 0) return null;
                    end += read;
                }
            }

            public byte[] ReadExactly(int count)
            {
                var body = new byte[count];
                int filled = Math.Min(count, end - start);
                Array.Copy(buffer, start, body, 0, filled);
                start += filled;
                while (filled < count)
                {
                    int read = stream.Read(body, filled, count - filled);
                    if (read <= 0) throw new IOException("Corps incomplet");
                    filled += read;
                }
                return body;
            }
        }
    }
}
