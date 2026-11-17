// WebSocket link to `vcl serve` (GET /ws): receives one IR object per text
// message on a background task and hands them to the main thread through a
// queue. Reconnects with backoff, so the server can start after the app.
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Vcl
{
    public class Connection : IDisposable
    {
        public readonly string Url;
        public readonly ConcurrentQueue<string> Inbox = new ConcurrentQueue<string>();
        public volatile string State = "connecting";
        public volatile string LastError;
        readonly CancellationTokenSource stop = new CancellationTokenSource();

        public Connection(string url)
        {
            Url = url;
            _ = Run();
        }

        async System.Threading.Tasks.Task Run()
        {
            float backoff = 0.5f;
            while (!stop.IsCancellationRequested)
            {
                using (var ws = new ClientWebSocket())
                {
                    try
                    {
                        State = "connecting";
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                        {
                            timeout.CancelAfter(5000);
                            await ws.ConnectAsync(new Uri(Url), timeout.Token);
                        }
                        State = "live";
                        LastError = null;
                        backoff = 0.5f;
                        var buf = new byte[1 << 16];
                        var msg = new System.IO.MemoryStream();
                        while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
                        {
                            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), stop.Token);
                            if (r.MessageType == WebSocketMessageType.Close) break;
                            msg.Write(buf, 0, r.Count);
                            if (!r.EndOfMessage) continue;
                            var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                            msg.SetLength(0);
                            foreach (var line in text.Split('\n'))
                                if (line.Trim().Length > 0) Inbox.Enqueue(line);
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception e)
                    {
                        LastError = e.InnerException?.Message ?? e.Message;
                    }
                }
                State = "offline";
                try { await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(backoff), stop.Token); }
                catch (OperationCanceledException) { return; }
                backoff = Mathf.Min(backoff * 2, 5);
            }
        }

        public void Dispose() => stop.Cancel();
    }
}
